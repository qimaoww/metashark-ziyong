// <copyright file="IEpisodeTitleBackfillPostProcessService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Library;

    public interface IEpisodeTitleBackfillPostProcessService
    {
        Task ProcessUpdatedItemAsync(ItemChangeEventArgs e, CancellationToken cancellationToken);
    }
}
