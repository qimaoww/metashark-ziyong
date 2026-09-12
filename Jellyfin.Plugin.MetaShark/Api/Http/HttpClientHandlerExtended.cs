// <copyright file="HttpClientHandlerExtended.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api.Http
{
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    public class HttpClientHandlerExtended : HttpClientHandler
    {
        public HttpClientHandlerExtended()
        {
            // 使用 .NET 默认的 TLS 证书校验；此前无条件放行任意证书会允许中间人篡改豆瓣流量。
            this.CheckCertificateRevocationList = true;
            this.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            this.CookieContainer = new CookieContainer();
            this.UseCookies = true;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return base.SendAsync(request, cancellationToken);
        }
    }
}
