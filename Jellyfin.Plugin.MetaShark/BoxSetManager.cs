// <copyright file="BoxSetManager.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Core;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class BoxSetManager : IHostedService, IDisposable
{
    private static readonly Action<ILogger, Exception?> LogCollectionDisabled =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(ScanLibrary)), "[MetaShark] 跳过自动创建合集扫描. reason=FeatureDisabled.");

    private static readonly Action<ILogger, int, Exception?> LogCollectionsFound =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(2, nameof(ScanLibrary)), "[MetaShark] 找到 {Count} 个待处理合集.");

    private static readonly Action<ILogger, string, string, Exception?> LogCreateCollection =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(3, nameof(AddMoviesToCollection)), "[MetaShark] 已创建合集. collectionName={CollectionName} movies={MoviesNames}.");

    private static readonly Action<ILogger, string, string, Exception?> LogUpdateCollection =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(4, nameof(AddMoviesToCollection)), "[MetaShark] 已更新合集. collectionName={CollectionName} movies={MoviesNames}.");

    private readonly ILibraryManager libraryManager;
    private readonly ICollectionManager collectionManager;
    private readonly Timer timer;
    private readonly HashSet<string> queuedTmdbCollection;
    private readonly ILogger<BoxSetManager> logger; // TODO logging

    public BoxSetManager(ILibraryManager libraryManager, ICollectionManager collectionManager, ILoggerFactory loggerFactory)
    {
        this.libraryManager = libraryManager;
        this.collectionManager = collectionManager;
        this.logger = loggerFactory.CreateLogger<BoxSetManager>();
        this.timer = new Timer(_ => this.OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        this.queuedTmdbCollection = new HashSet<string>();
    }

    public async Task ScanLibrary(IProgress<double> progress)
    {
        if (!(MetaSharkPlugin.Instance?.Configuration.EnableTmdbCollection ?? false))
        {
            LogCollectionDisabled(this.logger, null);
            progress?.Report(100);
            return;
        }

        var boxSets = this.GetAllBoxSetsFromLibrary();
        var movieCollections = this.GetMoviesFromLibrary();

        LogCollectionsFound(this.logger, movieCollections.Count, null);
        int index = 0;
        foreach (var (collectionName, collectionMovies) in movieCollections)
        {
            progress?.Report(100.0 * index / movieCollections.Count);

            var boxSet = boxSets.FirstOrDefault(b => b?.Name == collectionName);
            await this.AddMoviesToCollection(collectionMovies, collectionName, boxSet).ConfigureAwait(false);
            index++;
        }

        progress?.Report(100);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.libraryManager.ItemUpdated += this.OnLibraryManagerItemUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.libraryManager.ItemUpdated -= this.OnLibraryManagerItemUpdated;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    public IDictionary<string, IList<Movie>> GetMoviesFromLibrary()
    {
        var collectionMoviesMap = new Dictionary<string, IList<Movie>>();

        foreach (var library in this.libraryManager.RootFolder.Children)
        {
            // 判断当前是媒体库是否是电影，并开启了 metashark 插件
            var typeOptions = this.libraryManager.GetLibraryOptions(library).TypeOptions;
            if (typeOptions.FirstOrDefault(x => x.Type == "Movie" && x.MetadataFetchers.Contains(MetaSharkPlugin.PluginName)) == null)
            {
                continue;
            }

            var startIndex = 0;
            var pagesize = 1000;

            while (true)
            {
                var movies = this.libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    IsVirtualItem = false,
                    OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                    Parent = library,
                    StartIndex = startIndex,
                    Limit = pagesize,
                }).OfType<Movie>().ToList();

                foreach (var movie in movies)
                {
                    // 从tmdb获取合集信息
                    movie.ProviderIds.TryGetValue("TmdbCollection", out var collectionName);
                    if (string.IsNullOrEmpty(collectionName))
                    {
                        continue;
                    }

                    if (!collectionMoviesMap.TryGetValue(collectionName, out var collectionMovies))
                    {
                        collectionMovies = new List<Movie>();
                        collectionMoviesMap.Add(collectionName, collectionMovies);
                    }

                    collectionMovies.Add(movie);
                }

                if (movies.Count < pagesize)
                {
                    break;
                }

                startIndex += pagesize;
            }
        }

        return collectionMoviesMap;
    }

    private async Task AddMoviesToCollection(IList<Movie> movies, string collectionName, BoxSet? boxSet)
    {
        if (movies.Count < 2)
        {
            // won't automatically create collection if only one movie in it
            return;
        }

        var movieIds = movies.Select(m => m.Id).ToList();
        if (boxSet is null)
        {
            var movieNames = string.Join(", ", movies.Select(m => m.Name));
            LogCreateCollection(this.logger, collectionName, movieNames, null);
            boxSet = await this.collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = collectionName,
            }).ConfigureAwait(false);

            await this.collectionManager.AddToCollectionAsync(boxSet.Id, movieIds).ConfigureAwait(false);

            // HACK: 等获取 boxset 元数据后再更新一次合集，用于修正刷新元数据后丢失关联电影的 BUG
            this.queuedTmdbCollection.Add(collectionName);
            this.timer.Change(60000, Timeout.Infinite);
        }
        else
        {
            var movieNames = string.Join(", ", movies.Select(m => m.Name));
            LogUpdateCollection(this.logger, collectionName, movieNames, null);
            await this.collectionManager.AddToCollectionAsync(boxSet.Id, movieIds).ConfigureAwait(false);
        }
    }

    private List<BoxSet> GetAllBoxSetsFromLibrary()
    {
        return this.libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            CollapseBoxSetItems = false,
            Recursive = true,
        }).OfType<BoxSet>().ToList();
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.timer.Dispose();
        }
    }

    private void OnLibraryManagerItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (!(MetaSharkPlugin.Instance?.Configuration.EnableTmdbCollection ?? false))
        {
            return;
        }

        // Only support movies at this time
        if (e.Item is not Movie movie || e.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        if (string.IsNullOrEmpty(movie.CollectionName))
        {
            return;
        }

        // 判断 item 所在的媒体库是否是电影，并开启了 metashark 插件
        var typeOptions = this.libraryManager.GetLibraryOptions(movie).TypeOptions;
        if (typeOptions.FirstOrDefault(x => x.Type == "Movie" && x.MetadataFetchers.Contains(MetaSharkPlugin.PluginName)) == null)
        {
            return;
        }

        this.queuedTmdbCollection.Add(movie.CollectionName);

        // Restart the timer. After idling for 60 seconds it should trigger the callback. This is to avoid clobbering during a large library update.
        this.timer.Change(60000, Timeout.Infinite);
    }

    private void OnTimerElapsed()
    {
        // Stop the timer until next update
        this.timer.Change(Timeout.Infinite, Timeout.Infinite);

        var tmdbCollectionNames = this.queuedTmdbCollection.ToArray();

        // Clear the queue now, TODO what if it crashes? Should it be cleared after it's done?
        this.queuedTmdbCollection.Clear();

        var boxSets = this.GetAllBoxSetsFromLibrary();
        var movies = this.GetMoviesFromLibrary();
        foreach (var collectionName in tmdbCollectionNames)
        {
            if (movies.TryGetValue(collectionName, out var collectionMovies))
            {
                var boxSet = boxSets.FirstOrDefault(b => b?.Name == collectionName);
                this.AddMoviesToCollection(collectionMovies, collectionName, boxSet).GetAwaiter().GetResult();
            }
        }
    }
}
