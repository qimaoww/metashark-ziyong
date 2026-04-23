// <copyright file="IEpisodeTitleBackfillPostProcessService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill
{
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Library;

    public interface IEpisodeTitleBackfillPostProcessService
    {
        public const string ItemUpdatedTrigger = "ItemUpdated";

        public const string DeferredRetryTrigger = "DeferredRetry";

        Task TryApplyAsync(ItemChangeEventArgs e, string triggerName, CancellationToken cancellationToken);
    }
}
