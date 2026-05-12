// <copyright file="TmdbCorrectionRefreshIntentStartupFilter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark
{
    using System;
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;

    public sealed class TmdbCorrectionRefreshIntentStartupFilter : IStartupFilter
    {
        private readonly ILibraryManager libraryManager;
        private readonly ITmdbCorrectionRefreshIntentStore refreshIntentStore;

        public TmdbCorrectionRefreshIntentStartupFilter(ILibraryManager libraryManager, ITmdbCorrectionRefreshIntentStore refreshIntentStore)
        {
            this.libraryManager = libraryManager;
            this.refreshIntentStore = refreshIntentStore;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            return (app) =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (TmdbCorrectionRefreshIntentClassifier.TryResolveExplicitSearchMissingMetadataRefreshItemId(context, out var itemId))
                    {
                        this.refreshIntentStore.Save(itemId, ResolveItemPath(this.libraryManager.GetItemById(itemId)));
                    }

                    await nextMiddleware().ConfigureAwait(false);
                });

                next(app);
            };
        }

        private static string? ResolveItemPath(BaseItem? item)
        {
            return string.IsNullOrWhiteSpace(item?.Path)
                ? null
                : item.Path;
        }
    }
}
