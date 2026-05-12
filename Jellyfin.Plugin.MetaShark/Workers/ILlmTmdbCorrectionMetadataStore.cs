// <copyright file="ILlmTmdbCorrectionMetadataStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;

    public interface ILlmTmdbCorrectionMetadataStore
    {
        void Save(LlmTmdbCorrectionMetadataSnapshot snapshot);

        LlmTmdbCorrectionMetadataSnapshot? Peek(Guid itemId);

        LlmTmdbCorrectionMetadataSnapshot? PeekByPath(string itemPath);

        LlmTmdbCorrectionMetadataSnapshot? TryClaim(Guid itemId, string itemPath, Guid currentItemId, string currentItemPath, string claimToken);

        void ReleaseClaim(Guid itemId, string itemPath, string claimToken);

        void Remove(Guid itemId, string itemPath);
    }
}
