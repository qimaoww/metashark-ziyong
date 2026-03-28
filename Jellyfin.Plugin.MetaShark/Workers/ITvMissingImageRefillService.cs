// <copyright file="ITvMissingImageRefillService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System.Threading;
    using MediaBrowser.Controller.Library;

    public interface ITvMissingImageRefillService
    {
        void QueueMissingImagesForFullLibraryScan(CancellationToken cancellationToken);

        void QueueMissingImagesForUpdatedItem(ItemChangeEventArgs e, CancellationToken cancellationToken);
    }
}
