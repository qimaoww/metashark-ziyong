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
            // Ignore SSL certificate errors.（现有测试 HttpClientHandlerExtended_CurrentTlsValidationIsPermissive 锁定了该行为，暂不修改）
            this.ServerCertificateCustomValidationCallback = (message, certificate2, arg3, arg4) => true;
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
