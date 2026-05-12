// <copyright file="ILlmTmdbCorrectionMetadataPostProcessService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Library;

    public interface ILlmTmdbCorrectionMetadataPostProcessService
    {
        public const string ItemUpdatedTrigger = "ItemUpdated";

        Task TryApplyAsync(ItemChangeEventArgs e, string triggerName, CancellationToken cancellationToken);
    }
}
