// <copyright file="LlmObservabilityLog.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Microsoft.Extensions.Logging;

    internal static class LlmObservabilityLog
    {
        private static readonly Action<ILogger, string, bool, string, string, bool, Exception?> LogLlmAssistTriggerEvaluated =
            LoggerMessage.Define<string, bool, string, string, bool>(LogLevel.Information, new EventId(101, "LlmAssistTrigger.Evaluated"), "[MetaShark] LLM 触发已评估. reason={ReasonCode} accepted={Accepted} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}");

        private static readonly Action<ILogger, string, string, string, bool, Exception?> LogLlmAssistTriggerAccepted =
            LoggerMessage.Define<string, string, string, bool>(LogLevel.Information, new EventId(102, "LlmAssistTrigger.Accepted"), "[MetaShark] LLM 触发已接受. reason={ReasonCode} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}");

        private static readonly Action<ILogger, string, string, string, bool, Exception?> LogLlmAssistTriggerRejected =
            LoggerMessage.Define<string, string, string, bool>(LogLevel.Information, new EventId(103, "LlmAssistTrigger.Rejected"), "[MetaShark] LLM 触发已拒绝. reason={ReasonCode} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}");

        private static readonly Action<ILogger, string, bool, string, string, bool, Exception?> LogTmdbCorrectionEvaluated =
            LoggerMessage.Define<string, bool, string, string, bool>(LogLevel.Information, new EventId(201, "TmdbCorrection.Evaluated"), "[MetaShark] LLM TMDb 纠错已评估. reason={ReasonCode} accepted={Accepted} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}");

        private static readonly Action<ILogger, string, string, string, bool, Exception?> LogTmdbCorrectionRejectedMessage =
            LoggerMessage.Define<string, string, string, bool>(LogLevel.Information, new EventId(202, "TmdbCorrection.Rejected"), "[MetaShark] LLM TMDb 纠错已拒绝. reason={ReasonCode} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}");

        private static readonly Action<ILogger, string, string, Exception?> LogTmdbCorrectionAppliedMessage =
            LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(203, "TmdbCorrection.Applied"), "[MetaShark] LLM TMDb 纠错已应用. reason={ReasonCode} mediaType={MediaType}");

        public static void LogLlmAssistTriggerDecision(ILogger? logger, LlmAssistTriggerContext context, LlmAssistTriggerDecision decision)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(decision);
            if (logger == null)
            {
                return;
            }

            var reasonCode = NormalizeReasonCode(decision.Reason);
            var mediaType = NormalizeValue(context.MediaType);
            var semantic = context.Semantic.ToString();
            LogLlmAssistTriggerEvaluated(logger, reasonCode, decision.ShouldTrigger, mediaType, semantic, context.IsImageProvider, null);
            if (decision.ShouldTrigger)
            {
                LogLlmAssistTriggerAccepted(logger, reasonCode, mediaType, semantic, context.IsImageProvider, null);
                return;
            }

            LogLlmAssistTriggerRejected(logger, reasonCode, mediaType, semantic, context.IsImageProvider, null);
        }

        public static void LogLlmAssistRejected(ILogger? logger, string reasonCode, string? mediaType, DefaultScraperSemantic semantic, bool isImageProvider)
        {
            if (logger == null)
            {
                return;
            }

            var normalizedReason = NormalizeReasonCode(reasonCode);
            var normalizedMediaType = NormalizeValue(mediaType);
            var semanticText = semantic.ToString();
            LogLlmAssistTriggerEvaluated(logger, normalizedReason, false, normalizedMediaType, semanticText, isImageProvider, null);
            LogLlmAssistTriggerRejected(logger, normalizedReason, normalizedMediaType, semanticText, isImageProvider, null);
        }

        public static void LogTmdbCorrectionTriggerDecision(ILogger? logger, LlmAssistTriggerContext context, LlmAssistTriggerDecision decision)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(decision);
            if (logger == null)
            {
                return;
            }

            var reasonCode = NormalizeReasonCode(decision.Reason);
            var mediaType = NormalizeValue(context.MediaType);
            var semantic = context.Semantic.ToString();
            LogTmdbCorrectionEvaluated(logger, reasonCode, decision.ShouldTrigger, mediaType, semantic, context.IsImageProvider, null);
            if (!decision.ShouldTrigger)
            {
                LogTmdbCorrectionRejectedMessage(logger, reasonCode, mediaType, semantic, context.IsImageProvider, null);
            }
        }

        public static void LogTmdbCorrectionRejected(ILogger? logger, string reasonCode, string? mediaType, DefaultScraperSemantic semantic, bool isImageProvider)
        {
            if (logger == null)
            {
                return;
            }

            LogTmdbCorrectionRejectedMessage(logger, NormalizeReasonCode(reasonCode), NormalizeValue(mediaType), semantic.ToString(), isImageProvider, null);
        }

        public static void LogTmdbCorrectionApplied(ILogger? logger, string reasonCode, string? mediaType)
        {
            if (logger == null)
            {
                return;
            }

            LogTmdbCorrectionAppliedMessage(logger, NormalizeReasonCode(reasonCode), NormalizeValue(mediaType), null);
        }

        public static string NormalizeReasonCode(string? reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
            {
                return "Unknown";
            }

            if (reasonCode.Contains("ImdbEvidenceDoesNotAlign", StringComparison.Ordinal)
                || reasonCode.Contains("TvdbOwnershipUnverifiable", StringComparison.Ordinal)
                || reasonCode.Contains("DoubanOwnershipUnverifiable", StringComparison.Ordinal))
            {
                return "StaleExternalIdConflict";
            }

            return reasonCode.Trim();
        }

        private static string NormalizeValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
        }
    }
}
