// <copyright file="ITmdbCorrectionRefreshIntentStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;

    public interface ITmdbCorrectionRefreshIntentStore
    {
        void Save(Guid itemId, string? itemPath = null);

        void SaveSearchMissing(Guid itemId, string? itemPath = null);

        void SaveOverwrite(Guid itemId, string? itemPath = null);

        bool HasPending(Guid itemId, string? itemPath = null);

        bool HasPendingSearchMissing(Guid itemId, string? itemPath = null);

        bool HasPendingOverwrite(Guid itemId, string? itemPath = null);

        bool TryConsume(Guid itemId, string? itemPath = null);

        bool TryConsumeSearchMissing(Guid itemId, string? itemPath = null);

        bool TryConsumeOverwrite(Guid itemId, string? itemPath = null);
    }
}
