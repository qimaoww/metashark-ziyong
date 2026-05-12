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
    public const string LlmStructuredOutputModeJsonSchema = "json-schema";
    public const string LlmStructuredOutputModeJsonObject = "json-object";
    public const string LlmStructuredOutputModeTextJson = "text-json";

    private string? defaultScraperMode = DefaultScraperModeDefault;
    private int llmTimeoutSeconds = 15;
    private int llmMaxTokens = 512;
    private double llmConfidenceThreshold = 0.75;
    private double llmEpisodeGroupMappingMinConfidence = 0.80;
    private int llmEpisodeGroupMappingMaxCandidateGroups = 8;
    private string? llmStructuredOutputMode = LlmStructuredOutputModeJsonSchema;

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
    /// Gets or sets LLM verified Douban to TMDB correction mapping.
    /// </summary>
    public string LlmTmdbCorrectionMap { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets LLM verified Douban to TMDB ordinary completion mapping.
    /// </summary>
    public string LlmTmdbCompletionMap { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether LLM verified Douban to TMDB corrections should be persisted and reused.
    /// </summary>
    public bool EnableLlmTmdbCorrectionPersistence { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether LLM verified Douban to TMDB ordinary completions should be persisted and reused.
    /// </summary>
    public bool EnableLlmTmdbCompletionPersistence { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether LLM can suggest TMDB episode group mappings.
    /// </summary>
    public bool EnableLlmEpisodeGroupMappingAssist { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether LLM assisted scraping is enabled.
    /// </summary>
    public bool EnableLlmAssist { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether LLM can correct wrong TMDb IDs after strong verification.
    /// </summary>
    public bool EnableLlmTmdbIdCorrection { get; set; }

    /// <summary>
    /// Gets or sets the OpenAI-compatible LLM base URL.
    /// </summary>
    [SuppressMessage("Design", "CA1056:Uri properties should not be strings", Justification = "XML serialization in Jellyfin cannot handle System.Uri.")]
    public string LlmBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the LLM API key.
    /// </summary>
    public string LlmApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the LLM model name.
    /// </summary>
    public string LlmModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the LLM request timeout in seconds. Default is 15, with a 1 to 30 second range.
    /// </summary>
    public int LlmTimeoutSeconds
    {
        get => this.llmTimeoutSeconds;
        set => this.llmTimeoutSeconds = Math.Clamp(value, 1, 30);
    }

    /// <summary>
    /// Gets or sets the maximum LLM output token count.
    /// </summary>
    public int LlmMaxTokens
    {
        get => this.llmMaxTokens;
        set => this.llmMaxTokens = Math.Clamp(value, 64, 4096);
    }

    /// <summary>
    /// Gets or sets a value indicating whether relative path context can be sent to LLM.
    /// </summary>
    public bool LlmAllowRelativePathContext { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether title and overview text completion output is allowed.
    /// This remains disabled by default and is separate from the global LLM assist switch.
    /// </summary>
    public bool LlmAllowTextCompletion { get; set; }

    /// <summary>
    /// Gets or sets the minimum confidence threshold for LLM results.
    /// </summary>
    public double LlmConfidenceThreshold
    {
        get => this.llmConfidenceThreshold;
        set => this.llmConfidenceThreshold = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Gets or sets the minimum confidence threshold for LLM episode group mapping results.
    /// </summary>
    public double LlmEpisodeGroupMappingMinConfidence
    {
        get => this.llmEpisodeGroupMappingMinConfidence;
        set => this.llmEpisodeGroupMappingMinConfidence = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Gets or sets the maximum TMDB episode group candidates sent to LLM.
    /// </summary>
    public int LlmEpisodeGroupMappingMaxCandidateGroups
    {
        get => this.llmEpisodeGroupMappingMaxCandidateGroups;
        set => this.llmEpisodeGroupMappingMaxCandidateGroups = Math.Clamp(value, 1, 50);
    }

    /// <summary>
    /// Gets or sets the expected LLM structured output mode.
    /// </summary>
    public string LlmStructuredOutputMode
    {
        get => NormalizeLlmStructuredOutputMode(this.llmStructuredOutputMode);
        set => this.llmStructuredOutputMode = NormalizeLlmStructuredOutputMode(value);
    }

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

    private static string NormalizeLlmStructuredOutputMode(string? value)
    {
        return value switch
        {
            LlmStructuredOutputModeJsonSchema => LlmStructuredOutputModeJsonSchema,
            LlmStructuredOutputModeJsonObject => LlmStructuredOutputModeJsonObject,
            LlmStructuredOutputModeTextJson => LlmStructuredOutputModeTextJson,
            _ => LlmStructuredOutputModeJsonSchema,
        };
    }
}
