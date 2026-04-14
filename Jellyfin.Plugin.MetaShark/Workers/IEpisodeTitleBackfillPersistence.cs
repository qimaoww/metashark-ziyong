// <copyright file="IEpisodeTitleBackfillPersistence.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Entities.TV;

    public interface IEpisodeTitleBackfillPersistence
    {
        Task SaveAsync(Episode episode, CancellationToken cancellationToken);
    }
}
