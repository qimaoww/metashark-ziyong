// <copyright file="MetaSharkPlugin.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
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

        byte[]? originalFileBytes = null;
        var configurationFilePath = this.ConfigurationFilePath;
        var fileExisted = File.Exists(configurationFilePath);
        if (fileExisted)
        {
            originalFileBytes = File.ReadAllBytes(configurationFilePath);
        }

        try
        {
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
