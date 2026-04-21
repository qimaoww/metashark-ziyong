// <copyright file="IMovieSeriesPeopleOverwriteRefreshCandidateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;

    public interface IMovieSeriesPeopleOverwriteRefreshCandidateStore
    {
        void Save(MovieSeriesPeopleOverwriteRefreshCandidate candidate);

        MovieSeriesPeopleOverwriteRefreshCandidate? Peek(Guid itemId);

        MovieSeriesPeopleOverwriteRefreshCandidate? Consume(Guid itemId, string itemPath);
    }
}
