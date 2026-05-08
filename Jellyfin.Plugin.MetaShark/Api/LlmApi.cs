// <copyright file="LlmApi.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, nameof(LogLlmRequestException)), "[MetaShark] LLM 请求异常. 诊断={Diagnostic}");

        private static readonly Action<ILogger, string, Exception?> LogLlmRequestTimeout =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, nameof(LogLlmRequestTimeout)), "[MetaShark] LLM 请求超时. 诊断={Diagnostic}");

        private readonly ILogger<LlmApi> logger;
        private readonly HttpClient httpClient;

        public LlmApi(ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            this.logger = loggerFactory.CreateLogger<LlmApi>();
            this.httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        public async Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
        {
            return await this.CompleteAsync(prompt, LlmResponseSchemaKind.MetadataSuggestions, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LlmApiResult> CompleteAsync(string prompt, LlmResponseSchemaKind responseSchemaKind, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            var lastDiagnostic = string.Empty;

            for (var attempt = 0; attempt <= MaxRetryCount; attempt++)
            {
                var configuration = MetaSharkPlugin.Instance?.Configuration ?? new PluginConfiguration();
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(configuration.LlmTimeoutSeconds));
                var requestCancellationToken = timeoutSource.Token;
                try
                {
                    using var request = CreateRequest(configuration, prompt, responseSchemaKind);
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
                catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
                {
                    lastDiagnostic = $"LLM request timeout after {configuration.LlmTimeoutSeconds} seconds.";
                    if (attempt < MaxRetryCount)
                    {
                        LogLlmRequestRetry(this.logger, attempt + 1, lastDiagnostic, null);
                        await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    LogLlmRequestTimeout(this.logger, lastDiagnostic, null);
                    return LlmApiResult.Failed(lastDiagnostic);
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

        private static HttpRequestMessage CreateRequest(PluginConfiguration configuration, string prompt, LlmResponseSchemaKind responseSchemaKind)
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
                ResponseFormat = CreateResponseFormat(configuration.LlmStructuredOutputMode, responseSchemaKind),
            };
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            return request;
        }

        private static string BuildChatCompletionsUrl(string baseUrl)
        {
            return baseUrl.TrimEnd('/') + "/chat/completions";
        }

        private static LlmResponseFormat? CreateResponseFormat(string structuredOutputMode, LlmResponseSchemaKind responseSchemaKind)
        {
            return structuredOutputMode switch
            {
                PluginConfiguration.LlmStructuredOutputModeJsonSchema => CreateJsonSchemaResponseFormat(responseSchemaKind),
                PluginConfiguration.LlmStructuredOutputModeJsonObject => new LlmResponseFormat { Type = "json_object" },
                _ => null,
            };
        }

        private static LlmResponseFormat CreateJsonSchemaResponseFormat(LlmResponseSchemaKind schemaKind)
        {
            var (name, schema) = schemaKind switch
            {
                LlmResponseSchemaKind.ExternalIdCandidates => ("metashark_external_id_candidates", CreateExternalIdCandidateSchema()),
                LlmResponseSchemaKind.EpisodeGroupMapping => ("metashark_episode_group_mapping", CreateEpisodeGroupMappingSchema()),
                _ => ("metashark_metadata_suggestions", CreateMetadataSuggestionSchema()),
            };

            return new LlmResponseFormat
            {
                Type = "json_schema",
                JsonSchema = new LlmJsonSchema
                {
                    Name = name,
                    Strict = true,
                    Schema = schema,
                },
            };
        }

        private static Dictionary<string, object> CreateMetadataSuggestionSchema()
        {
            var metadataFields = new[] { "mediaType", "title", "year", "seasonNumber", "episodeNumber", "originalTitle", "overview", "confidence" };
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new[] { "suggestions" },
                ["properties"] = new Dictionary<string, object>
                {
                    ["suggestions"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["required"] = metadataFields,
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["mediaType"] = NullableEnumStringSchema("Movie", "Series", "Season", "Episode"),
                                ["title"] = NullableStringSchema(200),
                                ["year"] = NullableIntegerSchema(1874, DateTime.UtcNow.Year + 2),
                                ["seasonNumber"] = NullableIntegerSchema(0, null),
                                ["episodeNumber"] = NullableIntegerSchema(0, null),
                                ["originalTitle"] = NullableStringSchema(200),
                                ["overview"] = NullableStringSchema(4000),
                                ["confidence"] = ConfidenceSchema(),
                            },
                        },
                    },
                },
            };
        }

        private static Dictionary<string, object> CreateExternalIdCandidateSchema()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new[] { "externalIdCandidates" },
                ["properties"] = new Dictionary<string, object>
                {
                    ["externalIdCandidates"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["required"] = new[] { "provider", "id", "mediaType", "confidence", "reason", "evidence" },
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["provider"] = EnumStringSchema("TMDb", "IMDb", "TVDB", "Douban"),
                                ["id"] = StringSchema(64),
                                ["mediaType"] = EnumStringSchema("Movie", "Series", "Episode"),
                                ["confidence"] = ConfidenceSchema(),
                                ["reason"] = StringSchema(500),
                                ["evidence"] = StringSchema(1000),
                            },
                        },
                    },
                },
            };
        }

        private static Dictionary<string, object> CreateEpisodeGroupMappingSchema()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new[] { "selectedGroupId", "confidence", "reason" },
                ["properties"] = new Dictionary<string, object>
                {
                    ["selectedGroupId"] = StringSchema(128),
                    ["confidence"] = ConfidenceSchema(),
                    ["reason"] = NullableStringSchema(500),
                },
            };
        }

        private static Dictionary<string, object> StringSchema(int maxLength)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "string",
                ["maxLength"] = maxLength,
            };
        }

        private static Dictionary<string, object> NullableStringSchema(int maxLength)
        {
            return new Dictionary<string, object>
            {
                ["type"] = new[] { "string", "null" },
                ["maxLength"] = maxLength,
            };
        }

        private static Dictionary<string, object> EnumStringSchema(params string[] values)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = values,
            };
        }

        private static Dictionary<string, object> NullableEnumStringSchema(params string[] values)
        {
            return new Dictionary<string, object>
            {
                ["type"] = new[] { "string", "null" },
                ["enum"] = values.Cast<object>().Concat(new object?[] { null }).ToArray(),
            };
        }

        private static Dictionary<string, object> IntegerSchema(int minimum, int? maximum)
        {
            var schema = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["minimum"] = minimum,
            };
            if (maximum.HasValue)
            {
                schema["maximum"] = maximum.Value;
            }

            return schema;
        }

        private static Dictionary<string, object> NullableIntegerSchema(int minimum, int? maximum)
        {
            var schema = new Dictionary<string, object>
            {
                ["type"] = new[] { "integer", "null" },
                ["minimum"] = minimum,
            };
            if (maximum.HasValue)
            {
                schema["maximum"] = maximum.Value;
            }

            return schema;
        }

        private static Dictionary<string, object> ConfidenceSchema()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "number",
                ["minimum"] = 0.0,
                ["maximum"] = 1.0,
            };
        }

        private static bool ShouldRetry(HttpStatusCode statusCode)
        {
            return statusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                || (int)statusCode >= 500;
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
