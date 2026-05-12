// <copyright file="ITmdbCorrectionRefreshIntentStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;

    public interface ITmdbCorrectionRefreshIntentStore
    {
        void Save(Guid itemId, string? itemPath = null);

        bool HasPending(Guid itemId, string? itemPath = null);

        bool TryConsume(Guid itemId, string? itemPath = null);
    }
}
