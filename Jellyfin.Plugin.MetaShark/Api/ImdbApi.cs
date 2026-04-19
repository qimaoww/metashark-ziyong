// <copyright file="ImdbApi.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Linq;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Core;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;

    public class ImdbApi : IDisposable
    {
        private static readonly Action<ILogger, string, Exception?> LogCheckNewImdbIdError =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, nameof(CheckNewIDAsync)), "[MetaShark] 检查 IMDB 新 ID 失败. IMDB编号={ImdbId}");

        private static readonly Action<ILogger, string, Exception?> LogCheckPersonImdbIdError =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, nameof(CheckPersonNewIDAsync)), "[MetaShark] 检查人物 IMDB 新 ID 失败. IMDB编号={ImdbId}");

        private readonly ILogger<ImdbApi> logger;
        private readonly MemoryCache memoryCache;
        private readonly HttpClientHandler handler;
        private readonly HttpClient httpClient;

        private Regex regId = new Regex(@"/(tt\d+)", RegexOptions.Compiled);
        private Regex regPersonId = new Regex(@"/(nm\d+)", RegexOptions.Compiled);

        public ImdbApi(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<ImdbApi>();
            this.memoryCache = new MemoryCache(new MemoryCacheOptions());

            this.handler = new HttpClientHandler()
            {
                AllowAutoRedirect = false,
                CheckCertificateRevocationList = true,
            };
            this.httpClient = new HttpClient(this.handler, disposeHandler: false);
            this.httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// 通过imdb获取信息（会返回最新的imdb id）.
        /// </summary>
        /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
        public async Task<string?> CheckNewIDAsync(string id, CancellationToken cancellationToken)
        {
            var cacheKey = $"CheckNewImdbID_{id}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            if (this.memoryCache.TryGetValue<string?>(cacheKey, out var item))
            {
                return item;
            }

            try
            {
                var url = new Uri($"https://www.imdb.com/title/{id}/");
                var resp = await this.httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (resp.Headers.TryGetValues("Location", out var values))
                {
                    var location = values.First();
                    var newId = location.GetMatchGroup(this.regId);
                    if (!string.IsNullOrEmpty(newId))
                    {
                        item = newId;
                    }
                }

                this.memoryCache.Set(cacheKey, item, expiredOption);
                return item;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                LogCheckNewImdbIdError(this.logger, id, ex);
                this.memoryCache.Set<string?>(cacheKey, null, expiredOption);
                return null;
            }
            catch (TaskCanceledException ex)
            {
                LogCheckNewImdbIdError(this.logger, id, ex);
                this.memoryCache.Set<string?>(cacheKey, null, expiredOption);
                return null;
            }
        }

        /// <summary>
        /// 通过imdb获取信息（会返回最新的imdb id）.
        /// </summary>
        /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
        public async Task<string?> CheckPersonNewIDAsync(string id, CancellationToken cancellationToken)
        {
            var cacheKey = $"CheckPersonNewImdbID_{id}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            if (this.memoryCache.TryGetValue<string?>(cacheKey, out var item))
            {
                return item;
            }

            try
            {
                var url = new Uri($"https://www.imdb.com/name/{id}/");
                var resp = await this.httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (resp.Headers.TryGetValues("Location", out var values))
                {
                    var location = values.First();
                    var newId = location.GetMatchGroup(this.regPersonId);
                    if (!string.IsNullOrEmpty(newId))
                    {
                        item = newId;
                    }
                }

                this.memoryCache.Set(cacheKey, item, expiredOption);
                return item;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                LogCheckPersonImdbIdError(this.logger, id, ex);
                this.memoryCache.Set<string?>(cacheKey, null, expiredOption);
                return null;
            }
            catch (TaskCanceledException ex)
            {
                LogCheckPersonImdbIdError(this.logger, id, ex);
                this.memoryCache.Set<string?>(cacheKey, null, expiredOption);
                return null;
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.memoryCache.Dispose();
                this.httpClient.Dispose();
                this.handler.Dispose();
            }
        }

        private static bool IsEnable()
        {
            return MetaSharkPlugin.Instance?.Configuration.EnableTmdb ?? true;
        }
    }
}
