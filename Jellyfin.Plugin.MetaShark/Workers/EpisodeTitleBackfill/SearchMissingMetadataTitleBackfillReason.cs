// <copyright file="SearchMissingMetadataTitleBackfillReason.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill
{
    internal enum SearchMissingMetadataTitleBackfillReason
    {
        FeatureDisabled = 0,
        RequestNotSearchMissingMetadata = 1,
        EpisodeIdMissing = 2,
        OriginalTitleNotDefault = 3,
        ResolvedTitleEmpty = 4,
        ResolvedTitleSameAsOriginal = 5,
        StrictZhCnRejected = 6,
        CandidateQueued = 7,
    }
}
