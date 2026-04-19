// <copyright file="TvdbApi.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;

    public sealed class TvdbApi : IDisposable
    {
        private const string DefaultApiHost = "https://api4.thetvdb.com/v4/";
        private const string TokenCacheKey = "tvdb_token";
        private const int MaxPageCount = 20;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private static readonly Action<ILogger, int, Exception?> LogTvdbDisabled =
            LoggerMessage.Define<int>(LogLevel.Debug, new EventId(2, nameof(LogTvdbDisabled)), "[MetaShark] TVDB 已在配置中禁用. 剧集ID={SeriesId}");

        private static readonly Action<ILogger, int, string, int, string, Exception?> LogTvdbFetchEpisodes =
            LoggerMessage.Define<int, string, int, string>(LogLevel.Debug, new EventId(3, nameof(LogTvdbFetchEpisodes)), "[MetaShark] 开始获取 TVDB 剧集. 剧集ID={SeriesId} 季类型={SeasonType} 季号={Season} 语言={Lang}");

        private static readonly Action<ILogger, int, string, Exception?> LogTvdbRequestPage =
            LoggerMessage.Define<int, string>(LogLevel.Debug, new EventId(4, nameof(LogTvdbRequestPage)), "[MetaShark] 请求 TVDB 分页数据. 页码={Page} 地址={Url}");

        private static readonly Action<ILogger, int, Exception?> LogTvdbNullResponse =
            LoggerMessage.Define<int>(LogLevel.Debug, new EventId(5, nameof(LogTvdbNullResponse)), "[MetaShark] TVDB 请求返回空响应. 剧集ID={SeriesId}");

        private static readonly Action<ILogger, int, Exception?> LogTvdbRequestFailed =
            LoggerMessage.Define<int>(LogLevel.Debug, new EventId(6, nameof(LogTvdbRequestFailed)), "[MetaShark] TVDB 请求失败. 状态码={StatusCode}");

        private static readonly Action<ILogger, int, string, Exception?> LogTvdbRequestFailedInfo =
            LoggerMessage.Define<int, string>(LogLevel.Information, new EventId(15, nameof(LogTvdbRequestFailedInfo)), "[MetaShark] TVDB 请求失败. 状态码={StatusCode} 地址={Url}");

        private static readonly Action<ILogger, int, Exception?> LogTvdbMissingEpisodes =
            LoggerMessage.Define<int>(LogLevel.Debug, new EventId(7, nameof(LogTvdbMissingEpisodes)), "[MetaShark] TVDB 响应缺少剧集数据. 剧集ID={SeriesId}");

        private static readonly Action<ILogger, int, Exception?> LogTvdbEpisodesPageCount =
            LoggerMessage.Define<int>(LogLevel.Debug, new EventId(8, nameof(LogTvdbEpisodesPageCount)), "[MetaShark] TVDB 剧集页条目数={Count}");

        private static readonly Action<ILogger, int, Exception?> LogTvdbPaginationEnded =
            LoggerMessage.Define<int>(LogLevel.Debug, new EventId(9, nameof(LogTvdbPaginationEnded)), "[MetaShark] TVDB 分页结束. 页码={Page}");

        private static readonly Action<ILogger, Exception?> LogTvdbTokenCacheHit =
            LoggerMessage.Define(LogLevel.Debug, new EventId(10, nameof(LogTvdbTokenCacheHit)), "[MetaShark] 命中 TVDB 令牌缓存");

        private static readonly Action<ILogger, Exception?> LogTvdbApiKeyMissing =
            LoggerMessage.Define(LogLevel.Debug, new EventId(11, nameof(LogTvdbApiKeyMissing)), "[MetaShark] 未配置 TVDB 访问密钥");

        private static readonly Action<ILogger, int, Exception?> LogTvdbLoginFailed =
            LoggerMessage.Define<int>(LogLevel.Debug, new EventId(12, nameof(LogTvdbLoginFailed)), "[MetaShark] TVDB 登录失败. 状态码={StatusCode}");

        private static readonly Action<ILogger, Exception?> LogTvdbEmptyToken =
            LoggerMessage.Define(LogLevel.Debug, new EventId(13, nameof(LogTvdbEmptyToken)), "[MetaShark] TVDB 登录返回空令牌");

        private static readonly Action<ILogger, Exception?> LogTvdbTokenStored =
            LoggerMessage.Define(LogLevel.Debug, new EventId(14, nameof(LogTvdbTokenStored)), "[MetaShark] TVDB 令牌已写入缓存");

        private static readonly Action<ILogger, string, bool, bool, Exception?> LogTvdbConfigLoaded =
            LoggerMessage.Define<string, bool, bool>(LogLevel.Information, new EventId(17, nameof(LogTvdbConfigLoaded)), "[MetaShark] 已加载 TVDB 配置. 主机={Host} 已配置密钥={HasKey} 已配置口令={HasPin}");

        private readonly ILogger<TvdbApi> logger;
        private readonly MemoryCache memoryCache;
        private readonly HttpClient httpClient;
        private readonly Action<ILogger, string, Exception?> logTvdbError;
        private readonly string apiKey;
        private readonly string pin;
        private readonly string apiHost;

        public TvdbApi(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<TvdbApi>();
            this.memoryCache = new MemoryCache(new MemoryCacheOptions());
            this.logTvdbError = LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, nameof(TvdbApi)), "[MetaShark] TVDB 请求异常. 操作={Operation}");

            var config = MetaSharkPlugin.Instance?.Configuration;
            this.apiKey = config?.TvdbApiKey ?? string.Empty;
            this.pin = config?.TvdbPin ?? string.Empty;
            this.apiHost = NormalizeApiHost(config?.TvdbHost);

            this.httpClient = new HttpClient { BaseAddress = new Uri(this.apiHost), Timeout = TimeSpan.FromSeconds(10) };

            LogTvdbConfigLoaded(this.logger, this.apiHost, !string.IsNullOrWhiteSpace(this.apiKey), !string.IsNullOrWhiteSpace(this.pin), null);
        }

        public async Task<IReadOnlyList<TvdbEpisode>> GetSeriesEpisodesAsync(
            int seriesId,
            string seasonType,
            int seasonNumber,
            string? language,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled())
            {
                LogTvdbDisabled(this.logger, seriesId, null);
                return Array.Empty<TvdbEpisode>();
            }

            var episodes = new List<TvdbEpisode>();
            var lang = NormalizeLanguage(language);
            var basePath = string.IsNullOrWhiteSpace(lang)
                ? $"series/{seriesId}/episodes/{seasonType}"
                : $"series/{seriesId}/episodes/{seasonType}/{lang}";

            LogTvdbFetchEpisodes(this.logger, seriesId, seasonType, seasonNumber, lang ?? string.Empty, null);

            for (var page = 0; page < MaxPageCount; page++)
            {
                var url = $"{basePath}?page={page.ToString(CultureInfo.InvariantCulture)}&season={seasonNumber.ToString(CultureInfo.InvariantCulture)}";
                LogTvdbRequestPage(this.logger, page, url, null);
                TvdbEpisodesResponse? responsePayload;
                try
                {
                    using var response = await this.SendWithTokenAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)
                        .ConfigureAwait(false);
                    if (response == null)
                    {
                        LogTvdbNullResponse(this.logger, seriesId, null);
                        return episodes;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        LogTvdbRequestFailed(this.logger, (int)response.StatusCode, null);
                        LogTvdbRequestFailedInfo(this.logger, (int)response.StatusCode, url, null);
                        this.logTvdbError(this.logger, nameof(this.GetSeriesEpisodesAsync), new HttpRequestException(response.StatusCode.ToString()));
                        return episodes;
                    }

                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    responsePayload = JsonSerializer.Deserialize<TvdbEpisodesResponse>(json, JsonOptions);
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    this.logTvdbError(this.logger, nameof(this.GetSeriesEpisodesAsync), ex);
                    return episodes;
                }
                catch (HttpRequestException ex)
                {
                    this.logTvdbError(this.logger, nameof(this.GetSeriesEpisodesAsync), ex);
                    return episodes;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                if (responsePayload?.Data?.Episodes == null)
                {
                    LogTvdbMissingEpisodes(this.logger, seriesId, null);
                    return episodes;
                }

                LogTvdbEpisodesPageCount(this.logger, responsePayload.Data.Episodes.Count, null);
                foreach (var episode in responsePayload.Data.Episodes)
                {
                    episodes.Add(new TvdbEpisode
                    {
                        SeasonNumber = episode.SeasonNumber,
                        Number = episode.Number,
                        AirsBeforeSeason = episode.AirsBeforeSeason,
                        AirsBeforeEpisode = episode.AirsBeforeEpisode,
                        AirsAfterSeason = episode.AirsAfterSeason,
                        Aired = ParseAiredDate(episode.Aired),
                    });
                }

                if (string.IsNullOrWhiteSpace(responsePayload.Links?.Next))
                {
                    LogTvdbPaginationEnded(this.logger, page, null);
                    break;
                }
            }

            return episodes;
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        private static DateTime? ParseAiredDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed.Date;
            }

            return null;
        }

        private static string NormalizeApiHost(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultApiHost;
            }

            var normalized = value.Trim();
            if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "https://" + normalized;
            }

            normalized = normalized.TrimEnd('/');
            if (!normalized.EndsWith("/v4", StringComparison.OrdinalIgnoreCase))
            {
                normalized += "/v4";
            }

            if (!normalized.EndsWith('/'))
            {
                normalized += "/";
            }

            return normalized;
        }

        private static string? NormalizeLanguage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim().ToUpperInvariant();
            if (normalized.StartsWith("ZH", StringComparison.Ordinal))
            {
                return "zho";
            }

            if (normalized.StartsWith("EN", StringComparison.Ordinal))
            {
                return "eng";
            }

            if (normalized.StartsWith("JA", StringComparison.Ordinal))
            {
                return "jpn";
            }

            if (normalized.StartsWith("KO", StringComparison.Ordinal))
            {
                return "kor";
            }

            try
            {
                var culture = CultureInfo.GetCultureInfo(value);
                if (!string.IsNullOrWhiteSpace(culture.ThreeLetterISOLanguageName))
                {
                    return culture.ThreeLetterISOLanguageName;
                }
            }
            catch (CultureNotFoundException)
            {
                return null;
            }

            return null;
        }

        private static bool IsEnabled()
        {
            var config = MetaSharkPlugin.Instance?.Configuration;
            return (config?.EnableTvdbSpecialsWithinSeasons ?? false)
                && !string.IsNullOrWhiteSpace(config?.TvdbApiKey);
        }

        private async Task<string?> EnsureTokenAsync(CancellationToken cancellationToken)
        {
            if (this.memoryCache.TryGetValue<string>(TokenCacheKey, out var token) && !string.IsNullOrWhiteSpace(token))
            {
                LogTvdbTokenCacheHit(this.logger, null);
                return token;
            }

            if (string.IsNullOrWhiteSpace(this.apiKey))
            {
                LogTvdbApiKeyMissing(this.logger, null);
                return null;
            }

            try
            {
                var payload = new Dictionary<string, string>
                {
                    ["apikey"] = this.apiKey,
                };
                if (!string.IsNullOrWhiteSpace(this.pin))
                {
                    payload["pin"] = this.pin;
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, "login")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
                };

                using var response = await this.httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    LogTvdbLoginFailed(this.logger, (int)response.StatusCode, null);
                    this.logTvdbError(this.logger, nameof(this.EnsureTokenAsync), new HttpRequestException(response.StatusCode.ToString()));
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var login = JsonSerializer.Deserialize<TvdbLoginResponse>(json, JsonOptions);
                token = login?.Data?.Token;
                if (string.IsNullOrWhiteSpace(token))
                {
                    LogTvdbEmptyToken(this.logger, null);
                    return null;
                }

                var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(20) };
                this.memoryCache.Set(TokenCacheKey, token, options);
                LogTvdbTokenStored(this.logger, null);
                return token;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTvdbError(this.logger, nameof(this.EnsureTokenAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTvdbError(this.logger, nameof(this.EnsureTokenAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        private async Task<HttpResponseMessage?> SendWithTokenAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
        {
            var token = await this.EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            using var authedRequest = requestFactory();
            authedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await this.httpClient.SendAsync(authedRequest, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
            this.memoryCache.Remove(TokenCacheKey);
            token = await this.EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            using var retryAuthedRequest = requestFactory();
            retryAuthedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await this.httpClient.SendAsync(retryAuthedRequest, cancellationToken).ConfigureAwait(false);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.memoryCache.Dispose();
                this.httpClient.Dispose();
            }
        }

        [SuppressMessage("Performance", "CA1812", Justification = "Deserialized by System.Text.Json")]
        private sealed class TvdbLoginResponse
        {
            [JsonPropertyName("data")]
            public TvdbLoginData? Data { get; set; }
        }

        [SuppressMessage("Performance", "CA1812", Justification = "Deserialized by System.Text.Json")]
        private sealed class TvdbLoginData
        {
            [JsonPropertyName("token")]
            public string? Token { get; set; }
        }

        [SuppressMessage("Performance", "CA1812", Justification = "Deserialized by System.Text.Json")]
        private sealed class TvdbEpisodesResponse
        {
            [JsonPropertyName("data")]
            public TvdbEpisodesData? Data { get; set; }

            [JsonPropertyName("links")]
            public TvdbLinks? Links { get; set; }
        }

        [SuppressMessage("Performance", "CA1812", Justification = "Deserialized by System.Text.Json")]
        private sealed class TvdbEpisodesData
        {
            [JsonPropertyName("episodes")]
            public List<TvdbEpisodeBaseRecord>? Episodes { get; set; }
        }

        [SuppressMessage("Performance", "CA1812", Justification = "Deserialized by System.Text.Json")]
        private sealed class TvdbLinks
        {
            [JsonPropertyName("next")]
            public string? Next { get; set; }
        }

        [SuppressMessage("Performance", "CA1812", Justification = "Deserialized by System.Text.Json")]
        private sealed class TvdbEpisodeBaseRecord
        {
            [JsonPropertyName("seasonNumber")]
            public int? SeasonNumber { get; set; }

            [JsonPropertyName("number")]
            public int? Number { get; set; }

            [JsonPropertyName("airsBeforeSeason")]
            public int? AirsBeforeSeason { get; set; }

            [JsonPropertyName("airsBeforeEpisode")]
            public int? AirsBeforeEpisode { get; set; }

            [JsonPropertyName("airsAfterSeason")]
            public int? AirsAfterSeason { get; set; }

            [JsonPropertyName("aired")]
            public string? Aired { get; set; }
        }
    }
}
