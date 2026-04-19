// <copyright file="OmdbApi.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Model;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;

    public class OmdbApi : IDisposable
    {
        public const string DEFAULTAPIKEY = "2c9d9507";

        private static readonly Action<ILogger, string, Exception?> LogGetByImdbError =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, nameof(GetByImdbID)), "[MetaShark] 通过 IMDB ID 获取 OMDB 数据失败. IMDB编号={ImdbId}");

        private readonly ILogger<OmdbApi> logger;
        private readonly MemoryCache memoryCache;
        private readonly HttpClient httpClient;

        public OmdbApi(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<OmdbApi>();
            this.memoryCache = new MemoryCache(new MemoryCacheOptions());
            this.httpClient = new HttpClient();
            this.httpClient.Timeout = TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// 通过imdb获取信息（会返回最新的imdb id）.
        /// </summary>
        /// <param name="id">imdb id.</param>
        /// <param name="cancellationToken"></param>
        /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
        public async Task<OmdbItem?> GetByImdbID(string id, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            var cacheKey = $"GetByImdbID_{id}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            if (this.memoryCache.TryGetValue<OmdbItem?>(cacheKey, out var item))
            {
                return item;
            }

            try
            {
                var url = new Uri($"https://www.omdbapi.com/?i={id}&apikey={DEFAULTAPIKEY}");
                item = await this.httpClient.GetFromJsonAsync<OmdbItem>(url, cancellationToken).ConfigureAwait(false);
                this.memoryCache.Set(cacheKey, item, expiredOption);
                return item;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                LogGetByImdbError(this.logger, id, ex);
                this.memoryCache.Set<OmdbItem?>(cacheKey, null, expiredOption);
                return null;
            }
            catch (TaskCanceledException ex)
            {
                LogGetByImdbError(this.logger, id, ex);
                this.memoryCache.Set<OmdbItem?>(cacheKey, null, expiredOption);
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
            }
        }

        private static bool IsEnable()
        {
            return MetaSharkPlugin.Instance?.Configuration.EnableTmdb ?? true;
        }
    }
}
