// <copyright file="IEpisodeTitleBackfillCandidateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using Jellyfin.Plugin.MetaShark.Model;

    public interface IEpisodeTitleBackfillCandidateStore
    {
        void Save(EpisodeTitleBackfillCandidate candidate);

        EpisodeTitleBackfillCandidate? Consume(Guid itemId, DateTimeOffset nowUtc);

        void Remove(Guid itemId);
    }
}
