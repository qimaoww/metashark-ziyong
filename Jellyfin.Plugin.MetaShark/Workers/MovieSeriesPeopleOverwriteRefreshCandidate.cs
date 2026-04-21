// <copyright file="MovieSeriesPeopleOverwriteRefreshCandidate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;

    public sealed class MovieSeriesPeopleOverwriteRefreshCandidate
    {
        public Guid ItemId { get; set; }

        public string ItemPath { get; set; } = string.Empty;

        public int ExpectedPeopleCount { get; set; }
    }
}
