// <copyright file="ServiceRegistrator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark
{
    using System;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Controller.BaseItemManager;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Plugins;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.IO;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;

    /// <inheritdoc />
    public class ServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            ArgumentNullException.ThrowIfNull(serviceCollection);
            ArgumentNullException.ThrowIfNull(applicationHost);

            serviceCollection.AddHostedService<BoxSetManager>();
            serviceCollection.AddHostedService<TvMissingImageRefillItemUpdatedWorker>();
            serviceCollection.AddSingleton<ITvMissingImageRefillService, TvMissingImageRefillService>();
            serviceCollection.AddSingleton<IFileSystem>(applicationHost.Resolve<IFileSystem>());
            serviceCollection.AddSingleton<ILibraryManager>(applicationHost.Resolve<ILibraryManager>());
            serviceCollection.AddSingleton<IProviderManager>(applicationHost.Resolve<IProviderManager>());
            serviceCollection.AddSingleton<IBaseItemManager>(applicationHost.Resolve<IBaseItemManager>());
            serviceCollection.AddSingleton((ctx) =>
            {
                return new DoubanApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new TmdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new OmdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new ImdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new TvdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
        }
    }
}
