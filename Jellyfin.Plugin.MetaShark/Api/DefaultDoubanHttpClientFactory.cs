// <copyright file="DefaultDoubanHttpClientFactory.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Net.Http;
    using Jellyfin.Plugin.MetaShark.Api.Http;
    using Microsoft.Extensions.Logging;

    internal sealed class DefaultDoubanHttpClientFactory : IDoubanHttpClientFactory
    {
        public static readonly DefaultDoubanHttpClientFactory Shared = new DefaultDoubanHttpClientFactory();

        private DefaultDoubanHttpClientFactory()
        {
        }

        public DoubanHttpClientSet Create(ILogger logger)
        {
            var httpClientHandler = new HttpClientHandlerExtended();
            httpClientHandler.CheckCertificateRevocationList = true;
            var cookieContainer = httpClientHandler.CookieContainer;
            var doubanHandler = new DoubanSecHandler(logger) { InnerHandler = httpClientHandler };
            var httpClient = new HttpClient(doubanHandler, disposeHandler: false);
            httpClient.Timeout = TimeSpan.FromSeconds(20);
            httpClient.DefaultRequestHeaders.Add("User-Agent", DoubanApi.HTTPUSERAGENT);
            httpClient.DefaultRequestHeaders.Add("Origin", "https://movie.douban.com");
            httpClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");

            return new DoubanHttpClientSet(httpClient, httpClientHandler, doubanHandler, cookieContainer);
        }
    }
}
