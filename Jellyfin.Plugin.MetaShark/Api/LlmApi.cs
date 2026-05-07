// <copyright file="LlmApi.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Microsoft.Extensions.Logging;

    public sealed class LlmApi : ILlmApi, IDisposable
    {
        private const int MaxRetryCount = 2;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null,
        };

        private static readonly Action<ILogger, int, string, Exception?> LogLlmRequestFailed =
            LoggerMessage.Define<int, string>(LogLevel.Warning, new EventId(1, nameof(LogLlmRequestFailed)), "[MetaShark] LLM 请求失败. 状态码={StatusCode} 诊断={Diagnostic}");

        private static readonly Action<ILogger, int, string, Exception?> LogLlmRequestRetry =
            LoggerMessage.Define<int, string>(LogLevel.Warning, new EventId(2, nameof(LogLlmRequestRetry)), "[MetaShark] LLM 请求异常，将重试. 尝试={Attempt} 诊断={Diagnostic}");

        private static readonly Action<ILogger, string, Exception?> LogLlmRequestException =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(3, nameof(LogLlmRequestException)), "[MetaShark] LLM 请求异常. 诊断={Diagnostic}");

        private readonly ILogger<LlmApi> logger;
        private readonly HttpClient httpClient;

        public LlmApi(ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            this.logger = loggerFactory.CreateLogger<LlmApi>();
            var timeoutSeconds = MetaSharkPlugin.Instance?.Configuration.LlmTimeoutSeconds ?? new PluginConfiguration().LlmTimeoutSeconds;
            this.httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        }

        public async Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            var configuration = MetaSharkPlugin.Instance?.Configuration ?? new PluginConfiguration();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(configuration.LlmTimeoutSeconds));
            var requestCancellationToken = timeoutSource.Token;
            var lastDiagnostic = string.Empty;

            for (var attempt = 0; attempt <= MaxRetryCount; attempt++)
            {
                try
                {
                    using var request = CreateRequest(configuration, prompt);
                    using var response = await this.httpClient.SendAsync(request, requestCancellationToken).ConfigureAwait(false);
                    var responseBody = await response.Content.ReadAsStringAsync(requestCancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var parsed = LlmResponseParser.Parse(responseBody, configuration.LlmStructuredOutputMode);
                        if (!parsed.Success)
                        {
                            var diagnostic = RedactDiagnostic(parsed.Diagnostic, configuration.LlmApiKey, prompt, responseBody);
                            LogLlmRequestFailed(this.logger, 0, diagnostic, null);
                            return LlmApiResult.Failed(diagnostic);
                        }

                        return parsed;
                    }

                    var httpDiagnostic = RedactDiagnostic(LlmResponseParser.ParseErrorDiagnostic(responseBody), configuration.LlmApiKey, prompt, responseBody);
                    lastDiagnostic = $"HTTP {(int)response.StatusCode}: {httpDiagnostic}";
                    if (ShouldRetry(response.StatusCode) && attempt < MaxRetryCount)
                    {
                        LogLlmRequestRetry(this.logger, attempt + 1, lastDiagnostic, null);
                        await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    LogLlmRequestFailed(this.logger, (int)response.StatusCode, httpDiagnostic, null);
                    return LlmApiResult.Failed(lastDiagnostic);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (OperationCanceledException ex) when (requestCancellationToken.IsCancellationRequested)
                {
                    var diagnostic = RedactDiagnostic(ex.Message, configuration.LlmApiKey, prompt, null);
                    LogLlmRequestException(this.logger, diagnostic, new InvalidOperationException(diagnostic));
                    return LlmApiResult.Failed($"LLM request timeout: {diagnostic}");
                }
                catch (HttpRequestException ex) when (attempt < MaxRetryCount)
                {
                    lastDiagnostic = RedactDiagnostic(ex.Message, configuration.LlmApiKey, prompt, null);
                    LogLlmRequestRetry(this.logger, attempt + 1, lastDiagnostic, new InvalidOperationException(lastDiagnostic));
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    var diagnostic = RedactDiagnostic(ex.Message, configuration.LlmApiKey, prompt, null);
                    LogLlmRequestException(this.logger, diagnostic, new InvalidOperationException(diagnostic));
                    return LlmApiResult.Failed($"LLM request exception: {diagnostic}");
                }
            }

            return LlmApiResult.Failed(lastDiagnostic);
        }

        public void Dispose()
        {
            this.httpClient.Dispose();
        }

        private static HttpRequestMessage CreateRequest(PluginConfiguration configuration, string prompt)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUrl(configuration.LlmBaseUrl));
            if (!string.IsNullOrWhiteSpace(configuration.LlmApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.LlmApiKey);
            }

            var body = new LlmChatCompletionRequest
            {
                Model = configuration.LlmModel,
                Messages = new[] { new LlmChatMessage { Role = "user", Content = prompt } },
                Temperature = 0,
                MaxTokens = configuration.LlmMaxTokens,
                ResponseFormat = CreateResponseFormat(configuration.LlmStructuredOutputMode),
            };
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            return request;
        }

        private static string BuildChatCompletionsUrl(string baseUrl)
        {
            return baseUrl.TrimEnd('/') + "/chat/completions";
        }

        private static LlmResponseFormat? CreateResponseFormat(string structuredOutputMode)
        {
            return structuredOutputMode switch
            {
                PluginConfiguration.LlmStructuredOutputModeJsonSchema => new LlmResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new LlmJsonSchema
                    {
                        Name = "metashark_metadata_match",
                        Strict = true,
                        Schema = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = true,
                        },
                    },
                },
                PluginConfiguration.LlmStructuredOutputModeJsonObject => new LlmResponseFormat { Type = "json_object" },
                _ => null,
            };
        }

        private static bool ShouldRetry(HttpStatusCode statusCode)
        {
            return statusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.ServiceUnavailable;
        }

        private static string RedactDiagnostic(string diagnostic, string apiKey, string prompt, string? rawResponse)
        {
            return LlmRedactor.Redact(diagnostic, apiKey, prompt, rawResponse);
        }

        private static async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
        {
            var jitterMilliseconds = RandomNumberGenerator.GetInt32(0, 15);
            var delay = TimeSpan.FromMilliseconds((25 * Math.Pow(2, attempt)) + jitterMilliseconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        private sealed class LlmChatCompletionRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [JsonPropertyName("messages")]
            public IReadOnlyList<LlmChatMessage> Messages { get; set; } = Array.Empty<LlmChatMessage>();

            [JsonPropertyName("temperature")]
            public int Temperature { get; set; }

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }

            [JsonPropertyName("response_format")]
            public LlmResponseFormat? ResponseFormat { get; set; }
        }

        private sealed class LlmChatMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = string.Empty;

            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }

        private sealed class LlmResponseFormat
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = string.Empty;

            [JsonPropertyName("json_schema")]
            public LlmJsonSchema? JsonSchema { get; set; }
        }

        private sealed class LlmJsonSchema
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("strict")]
            public bool Strict { get; set; }

            [JsonPropertyName("schema")]
            public Dictionary<string, object> Schema { get; set; } = new Dictionary<string, object>();
        }
    }
}
