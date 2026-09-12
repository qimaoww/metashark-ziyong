// <copyright file="MetaSharkPlugin.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The main plugin.
/// </summary>
public class MetaSharkPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Gets the provider name.
    /// </summary>
    public const string PluginName = "MetaShark";

    /// <summary>
    /// Gets the provider id.
    /// </summary>
    public const string ProviderId = "MetaSharkID";

    private static readonly object ConfigurationSaveLock = new object();

    private readonly IServerApplicationHost appHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetaSharkPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public MetaSharkPlugin(IServerApplicationHost appHost, IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        this.appHost = appHost;
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static MetaSharkPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("9A19103F-16F7-4668-BE54-9A1E7A4F7556");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = this.Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", this.GetType().Namespace),
            },
        };
    }

    public Uri GetLocalApiBaseUrl()
    {
        return new Uri(this.appHost.GetLocalApiUrl("127.0.0.1", "http"), UriKind.Absolute);
    }

    public Uri GetApiBaseUrl(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int? requestPort = request.Host.Port;
        if (requestPort == null
            || (requestPort == 80 && string.Equals(request.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            || (requestPort == 443 && string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            requestPort = -1;
        }

        return new Uri(this.appHost.GetLocalApiUrl(request.Host.Host, request.Scheme, requestPort), UriKind.Absolute);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Configuration persistence must translate any serializer or file-system save failure into a recoverable false result and rollback the in-memory/file snapshot.")]
    public bool TrySaveConfigurationSafely(PluginConfiguration configuration, PluginConfiguration rollbackConfiguration, out Exception? saveException)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(rollbackConfiguration);

        lock (ConfigurationSaveLock)
        {
            byte[]? originalFileBytes = null;
            var configurationFilePath = this.ConfigurationFilePath;
            var fileExisted = false;

            try
            {
                fileExisted = File.Exists(configurationFilePath);
                if (fileExisted)
                {
                    originalFileBytes = File.ReadAllBytes(configurationFilePath);
                }

                this.SaveConfiguration(configuration);
                this.Configuration = configuration;
                saveException = null;
                return true;
            }
            catch (Exception ex)
            {
                Exception? rollbackException = null;

                try
                {
                    this.Configuration = rollbackConfiguration;
                    if (fileExisted)
                    {
                        File.WriteAllBytes(configurationFilePath, originalFileBytes ?? Array.Empty<byte>());
                    }
                    else if (File.Exists(configurationFilePath))
                    {
                        File.Delete(configurationFilePath);
                    }
                }
                catch (Exception restoreEx)
                {
                    rollbackException = restoreEx;
                }

                saveException = rollbackException == null ? ex : new AggregateException(ex, rollbackException);
                return false;
            }
        }
    }

    /// <summary>
    /// 在配置保存锁内基于最新配置执行一次变更，避免并发保存用锁外旧快照互相覆盖。
    /// <paramref name="update"/> 返回 null 表示无需保存（调用方自行记录结果）。
    /// </summary>
    /// <param name="update">基于当前最新配置计算新配置的委托。</param>
    /// <param name="saveException">保存失败时的异常。</param>
    /// <returns>是否需要并成功保存。</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "配置保存必须把序列化/文件失败以及变更委托的异常都翻译成可恢复的结果，不能抛给调用方。")]
    public bool TryUpdateConfigurationSafely(Func<PluginConfiguration, PluginConfiguration?> update, out Exception? saveException)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (ConfigurationSaveLock)
        {
            var currentConfiguration = this.Configuration;
            if (currentConfiguration == null)
            {
                saveException = null;
                return false;
            }

            var rollbackConfiguration = CloneConfiguration(currentConfiguration);
            PluginConfiguration? updatedConfiguration;
            try
            {
                updatedConfiguration = update(currentConfiguration);
            }
            catch (Exception ex)
            {
                saveException = ex;
                return false;
            }

            if (updatedConfiguration == null)
            {
                saveException = null;
                return true;
            }

            return this.TrySaveConfigurationSafely(updatedConfiguration, rollbackConfiguration, out saveException);
        }
    }

    /// <summary>
    /// 浅克隆插件配置（属性级），用于配置保存失败回滚与基于最新配置的增量修改。
    /// </summary>
    internal static PluginConfiguration CloneConfiguration(PluginConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var clone = new PluginConfiguration();
        foreach (var property in typeof(PluginConfiguration)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(static property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0))
        {
            property.SetValue(clone, property.GetValue(source));
        }

        return clone;
    }
}
