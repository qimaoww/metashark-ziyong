// <copyright file="LlmTmdbCorrectionMapPersistenceResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;

    public sealed class LlmTmdbCorrectionMapPersistenceResult
    {
        private LlmTmdbCorrectionMapPersistenceResult(
            LlmTmdbCorrectionMapPersistenceStatus status,
            string reason,
            string previousMapping,
            string currentMapping,
            Exception? exception)
        {
            this.Status = status;
            this.Reason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason;
            this.PreviousMapping = previousMapping;
            this.CurrentMapping = currentMapping;
            this.Exception = exception;
        }

        public LlmTmdbCorrectionMapPersistenceStatus Status { get; }

        public string Reason { get; }

        public string PreviousMapping { get; }

        public string CurrentMapping { get; }

        public Exception? Exception { get; }

        public bool Saved => this.Status == LlmTmdbCorrectionMapPersistenceStatus.Saved;

        public static LlmTmdbCorrectionMapPersistenceResult SavedResult(string previousMapping, string currentMapping)
        {
            return new LlmTmdbCorrectionMapPersistenceResult(LlmTmdbCorrectionMapPersistenceStatus.Saved, string.Empty, previousMapping, currentMapping, null);
        }

        public static LlmTmdbCorrectionMapPersistenceResult NoChange(string mapping)
        {
            return new LlmTmdbCorrectionMapPersistenceResult(LlmTmdbCorrectionMapPersistenceStatus.NoChange, string.Empty, mapping, mapping, null);
        }

        public static LlmTmdbCorrectionMapPersistenceResult Failed(string reason, string previousMapping, string currentMapping, Exception? exception)
        {
            return new LlmTmdbCorrectionMapPersistenceResult(LlmTmdbCorrectionMapPersistenceStatus.Failed, string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason, previousMapping, currentMapping, exception);
        }
    }
}
