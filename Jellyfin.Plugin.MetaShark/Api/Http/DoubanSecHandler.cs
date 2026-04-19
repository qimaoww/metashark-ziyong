// <copyright file="DoubanSecHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Net;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using AngleSharp;
    using Microsoft.Extensions.Logging;

    // DelegatingHandler that detects Douban sec.douban.com challenge pages,
    // solves the SHA-512 nonce challenge and retries the original request once.
    public class DoubanSecHandler : DelegatingHandler
    {
        private static readonly Action<ILogger, string?, Exception?> LogRiskControlTriggered =
            LoggerMessage.Define<string?>(LogLevel.Warning, new EventId(1, nameof(SendAsync)), "[MetaShark] Douban 触发风控. requestUri={RequestUri}");

        private static readonly Action<ILogger, string?, Exception?> LogChallengeFailure =
            LoggerMessage.Define<string?>(LogLevel.Warning, new EventId(2, nameof(SendAsync)), "[MetaShark] 处理 Douban 验证页面失败. requestUri={RequestUri}");

        private readonly ILogger logger;

        public DoubanSecHandler(ILogger logger)
        {
            this.logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Save original request target (before any redirects/rewrites by inner handlers)
            var originalRequestUri = request.RequestUri;

            // Send initial request down the handler chain
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            try
            {
                var respHost = response.RequestMessage?.RequestUri?.Host ?? string.Empty;
                if (!string.Equals(respHost, "sec.douban.com", StringComparison.OrdinalIgnoreCase))
                {
                    return response;
                }

                // Read body and detect challenge form only for sec.douban.com redirect.
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(body) && (body.Contains("name=\"cha\"", StringComparison.OrdinalIgnoreCase) || body.Contains("id=\"cha\"", StringComparison.OrdinalIgnoreCase) || body.Contains("name=\"tok\"", StringComparison.OrdinalIgnoreCase)))
                {
                    LogRiskControlTriggered(this.logger, originalRequestUri?.ToString(), null);

                    var context = BrowsingContext.New();
                    var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);

                    var tok = doc.QuerySelector("#tok")?.GetAttribute("value") ?? doc.QuerySelector("input[name=tok]")?.GetAttribute("value") ?? string.Empty;
                    var cha = doc.QuerySelector("#cha")?.GetAttribute("value") ?? doc.QuerySelector("input[name=cha]")?.GetAttribute("value") ?? string.Empty;
                    var diffStr = doc.QuerySelector("#difficulty")?.GetAttribute("value") ?? doc.QuerySelector("input[name=difficulty]")?.GetAttribute("value");
                    var difficulty = 4;
                    if (!string.IsNullOrEmpty(diffStr) && int.TryParse(diffStr, out var d))
                    {
                        difficulty = d;
                    }

                    if (!string.IsNullOrEmpty(cha))
                    {
                        var sol = await SolveNonceAsync(cha, difficulty, cancellationToken).ConfigureAwait(false);

                        // Prefer form action if present; otherwise use current response request URI.
                        var formEl = doc.QuerySelector("form");
                        var action = formEl?.GetAttribute("action") ?? "/c";

                        // Resolve action to absolute URI when necessary
                        var postUri = new Uri("https://sec.douban.com" + action);

                        var form = new List<KeyValuePair<string, string>>()
                        {
                            new KeyValuePair<string, string>("tok", tok),
                            new KeyValuePair<string, string>("cha", cha),
                            new KeyValuePair<string, string>("sol", sol.ToString(CultureInfo.InvariantCulture)),
                        };

                        using (var req = new HttpRequestMessage(HttpMethod.Post, postUri))
                        {
                            req.Content = new FormUrlEncodedContent(form);

                            // set referrer to original request if available
                            if (request.RequestUri != null)
                            {
                                req.Headers.Referrer = request.RequestUri;
                            }

                            // Send the validation POST so the inner handler can store cookies
                            using var postResp = await base.SendAsync(req, cancellationToken).ConfigureAwait(false);
                        }

                        // Retry the original request once using the saved original target URI.
                        using var retry = await CloneHttpRequestMessageAsync(request, cancellationToken).ConfigureAwait(false);
                        retry.RequestUri = originalRequestUri ?? request.RequestUri;

                        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogChallengeFailure(this.logger, originalRequestUri?.ToString(), ex);
            }
            catch (HttpRequestException ex)
            {
                LogChallengeFailure(this.logger, originalRequestUri?.ToString(), ex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return response;
        }

        private static string ComputeSha512Hex(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA512.HashData(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static async Task<long> SolveNonceAsync(string data, int difficulty, CancellationToken cancellationToken)
        {
            var targetPrefix = new string('0', Math.Max(0, difficulty));
            long nonce = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nonce++;
                var hash = ComputeSha512Hex(data + nonce.ToString(CultureInfo.InvariantCulture));
                if (hash.StartsWith(targetPrefix, StringComparison.Ordinal))
                {
                    return nonce;
                }

                if ((nonce & 0xFFF) == 0)
                {
                    await Task.Yield();
                }
            }
        }

        private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage req, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(req.Method, req.RequestUri);

            // Copy the request content (if any)
            if (req.Content != null)
            {
                var ms = await req.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var content = new ByteArrayContent(ms);

                // copy content headers
                foreach (var h in req.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }

                clone.Content = content;
            }

            // copy headers
            foreach (var header in req.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // // copy properties (Options) if any
            // foreach (var prop in req.Options)
            // {
            //     clone.Options.Set(prop.Key, prop.Value);
            // }
            return clone;
        }
    }
}
