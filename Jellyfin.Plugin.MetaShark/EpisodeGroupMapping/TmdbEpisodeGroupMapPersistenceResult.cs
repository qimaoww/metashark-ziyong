// <copyright file="TmdbEpisodeGroupMapPersistenceResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;

    public sealed class TmdbEpisodeGroupMapPersistenceResult
    {
        private TmdbEpisodeGroupMapPersistenceResult(
            TmdbEpisodeGroupMapPersistenceStatus status,
            string reason,
            string previousMapping,
            string currentMapping,
            Exception? exception)
        {
            this.Status = status;
            this.Reason = reason;
            this.PreviousMapping = previousMapping;
            this.CurrentMapping = currentMapping;
            this.Exception = exception;
        }

        public TmdbEpisodeGroupMapPersistenceStatus Status { get; }

        public string Reason { get; }

        public string PreviousMapping { get; }

        public string CurrentMapping { get; }

        public Exception? Exception { get; }

        public bool Saved => this.Status == TmdbEpisodeGroupMapPersistenceStatus.Saved;

        public static TmdbEpisodeGroupMapPersistenceResult NoChange(string mapping)
        {
            return new TmdbEpisodeGroupMapPersistenceResult(TmdbEpisodeGroupMapPersistenceStatus.NoChange, string.Empty, mapping, mapping, null);
        }

        public static TmdbEpisodeGroupMapPersistenceResult Conflict(string previousMapping, string currentMapping)
        {
            return new TmdbEpisodeGroupMapPersistenceResult(TmdbEpisodeGroupMapPersistenceStatus.Conflict, "MappingChangedBeforeSave", previousMapping, currentMapping, null);
        }

        public static TmdbEpisodeGroupMapPersistenceResult SavedResult(string previousMapping, string currentMapping)
        {
            return new TmdbEpisodeGroupMapPersistenceResult(TmdbEpisodeGroupMapPersistenceStatus.Saved, string.Empty, previousMapping, currentMapping, null);
        }

        public static TmdbEpisodeGroupMapPersistenceResult Failed(string reason, string previousMapping, string currentMapping, Exception? exception)
        {
            return new TmdbEpisodeGroupMapPersistenceResult(TmdbEpisodeGroupMapPersistenceStatus.Failed, NormalizeReason(reason), previousMapping, currentMapping, exception);
        }

        private static string NormalizeReason(string reason)
        {
            return string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason;
        }
    }
}
