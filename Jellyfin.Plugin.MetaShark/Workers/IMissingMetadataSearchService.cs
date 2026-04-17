// <copyright file="IMissingMetadataSearchService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IMissingMetadataSearchService
    {
        Task RunFullLibrarySearchAsync(IProgress<double> progress, CancellationToken cancellationToken);
    }
}
