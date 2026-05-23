// <copyright file="DoubanHttpClientSet.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System.Net;
    using System.Net.Http;
    using Jellyfin.Plugin.MetaShark.Api.Http;

    internal sealed class DoubanHttpClientSet
    {
        public DoubanHttpClientSet(
            HttpClient httpClient,
            HttpClientHandlerExtended httpClientHandler,
            DoubanSecHandler doubanHandler,
            CookieContainer cookieContainer)
        {
            this.HttpClient = httpClient;
            this.HttpClientHandler = httpClientHandler;
            this.DoubanHandler = doubanHandler;
            this.CookieContainer = cookieContainer;
        }

        public HttpClient HttpClient { get; }

        public HttpClientHandlerExtended HttpClientHandler { get; }

        public DoubanSecHandler DoubanHandler { get; }

        public CookieContainer CookieContainer { get; }
    }
}
