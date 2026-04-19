// <copyright file="LoggingHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api.Http
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;

    public class LoggingHandler : DelegatingHandler
    {
        private static readonly Action<ILogger, string?, Exception?> LogRequest =
            LoggerMessage.Define<string?>(LogLevel.Information, new EventId(1, nameof(SendAsync)), "[MetaShark] 发起请求. requestUri={RequestUri}");

        private readonly ILogger<LoggingHandler> logger;

        public LoggingHandler(HttpMessageHandler innerHandler, ILoggerFactory loggerFactory)
            : base(innerHandler)
        {
            this.logger = loggerFactory.CreateLogger<LoggingHandler>();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            LogRequest(this.logger, request.RequestUri?.ToString(), null);

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
