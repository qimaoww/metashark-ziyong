// <copyright file="PluginConfiguration.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Configuration;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using MediaBrowser.Model.Plugins;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public const int MAXCASTMEMBERS = 15;
    public const int MAXSEARCHRESULT = 5;
    public const string DefaultScraperModeDefault = "default";
    public const string DefaultScraperModeTmdbOnly = "tmdb-only";

    private string? defaultScraperMode = DefaultScraperModeDefault;

    /// <summary>
    /// Gets 插件版本.
    /// </summary>
    public string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;

    public string DoubanCookies { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether 豆瓣开启防封禁.
    /// </summary>
    public bool EnableDoubanAvoidRiskControl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 豆瓣海报使用大图.
    /// </summary>
    public bool EnableDoubanLargePoster { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 豆瓣背景图使用原图.
    /// </summary>
    public bool EnableDoubanBackdropRaw { get; set; }

    /// <summary>
    /// Gets or sets 豆瓣图片代理地址.
    /// </summary>
    [SuppressMessage("Design", "CA1056:Uri properties should not be strings", Justification = "XML serialization in Jellyfin cannot handle System.Uri.")]
    public string DoubanImageProxyBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether 启用获取tmdb元数据.
    /// </summary>
    public bool EnableTmdb { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 启用显示tmdb搜索结果.
    /// </summary>
    public bool EnableTmdbSearch { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 启用tmdb自动匹配.
    /// </summary>
    public bool EnableTmdbMatch { get; set; } = true;

    /// <summary>
    /// Gets or sets the default scraper mode.
    /// </summary>
    public string DefaultScraperMode
    {
        get => NormalizeDefaultScraperMode(this.defaultScraperMode);
        set => this.defaultScraperMode = NormalizeDefaultScraperMode(value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether 启用tmdb获取背景图.
    /// </summary>
    public bool EnableTmdbBackdrop { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 启用tmdb获取商标.
    /// </summary>
    public bool EnableTmdbLogo { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 是否根据电影系列自动创建合集.
    /// </summary>
    public bool EnableTmdbCollection { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 启用tmdb获取成人内容.
    /// </summary>
    public bool EnableTmdbAdult { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 是否获取tmdb分级信息.
    /// </summary>
    public bool EnableTmdbOfficialRating { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 是否获取tmdb标签(关键词).
    /// </summary>
    public bool EnableTmdbTags { get; set; } = true;

    /// <summary>
    /// Gets or sets tmdb api key.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets tmdb api host.
    /// </summary>
    public string TmdbHost { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets tmdb series id to episode group id mapping.
    /// </summary>
    public string TmdbEpisodeGroupMap { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to backfill default episode titles after search missing metadata.
    /// </summary>
    public bool EnableSearchMissingMetadataEpisodeTitleBackfill { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use TVDB for specials placement.
    /// </summary>
    public bool EnableTvdbSpecialsWithinSeasons { get; set; } = true;

    /// <summary>
    /// Gets or sets TVDB api key.
    /// </summary>
    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets TVDB subscriber pin (optional for subscriber keys).
    /// </summary>
    public string TvdbPin { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets TVDB api host.
    /// </summary>
    public string TvdbHost { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 代理服务器类型，0-禁用，1-http，2-https，3-socket5.
    /// </summary>
    public string TmdbProxyType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 代理服务器host.
    /// </summary>
    public string TmdbProxyPort { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 代理服务器端口.
    /// </summary>
    public string TmdbProxyHost { get; set; } = string.Empty;

    public IWebProxy? GetTmdbWebProxy()
    {
        if (!string.IsNullOrEmpty(this.TmdbProxyType))
        {
            return new WebProxy($"{this.TmdbProxyType}://{this.TmdbProxyHost}:{this.TmdbProxyPort}", true);
        }

        return null;
    }

    private static string NormalizeDefaultScraperMode(string? value)
    {
        return value switch
        {
            DefaultScraperModeTmdbOnly => DefaultScraperModeTmdbOnly,
            DefaultScraperModeDefault => DefaultScraperModeDefault,
            _ => DefaultScraperModeDefault,
        };
    }
}
