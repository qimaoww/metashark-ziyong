// <copyright file="BoxSetManager.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
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
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromSeconds(60);

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
    private readonly MetaSharkOrdinaryItemLibraryCapabilityResolver ordinaryItemLibraryCapabilityResolver;
    private readonly IBoxSetDebounceScheduler scheduler;
    private readonly TimeSpan debounceDelay;
    private readonly object syncRoot = new object();
    private readonly HashSet<string> queuedTmdbCollection;
    private readonly ILogger<BoxSetManager> logger; // TODO logging
    private List<string> inFlightTmdbCollection = new List<string>();
    private Task? currentDrainTask;
    private bool isStopped;
    private int drainRunning;

    public BoxSetManager(ILibraryManager libraryManager, ICollectionManager collectionManager, ILoggerFactory loggerFactory)
        : this(libraryManager, collectionManager, loggerFactory, DefaultDebounceDelay, new TimerBoxSetDebounceScheduler())
    {
    }

    internal BoxSetManager(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILoggerFactory loggerFactory,
        TimeSpan debounceDelay,
        IBoxSetDebounceScheduler scheduler)
    {
        this.libraryManager = libraryManager;
        this.collectionManager = collectionManager;
        this.ordinaryItemLibraryCapabilityResolver = new MetaSharkOrdinaryItemLibraryCapabilityResolver(libraryManager);
        this.logger = loggerFactory.CreateLogger<BoxSetManager>();
        this.debounceDelay = debounceDelay;
        this.scheduler = scheduler;
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
        lock (this.syncRoot)
        {
            this.isStopped = false;
        }

        this.libraryManager.ItemUpdated += this.OnLibraryManagerItemUpdated;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        this.libraryManager.ItemUpdated -= this.OnLibraryManagerItemUpdated;
        Task? drainTask;
        lock (this.syncRoot)
        {
            this.isStopped = true;
            this.queuedTmdbCollection.Clear();
            this.scheduler.Cancel();
            drainTask = this.currentDrainTask;
        }

        if (drainTask != null)
        {
            await drainTask.ConfigureAwait(false);
        }
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
            var startIndex = 0;
            var pagesize = 1000;

            while (true)
            {
                var movies = this.libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    IsVirtualItem = false,
                    Recursive = true,
                    OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                    Parent = library,
                    StartIndex = startIndex,
                    Limit = pagesize,
                }).OfType<Movie>().ToList();

                foreach (var movie in movies)
                {
                    if (!this.IsMovieMetadataEnabled(movie))
                    {
                        continue;
                    }

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
            this.QueueCollection(collectionName);
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
            this.scheduler.Dispose();
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

        if (!this.IsMovieMetadataEnabled(movie))
        {
            return;
        }

        this.QueueCollection(movie.CollectionName);
    }

    private bool IsMovieMetadataEnabled(Movie movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        return this.ordinaryItemLibraryCapabilityResolver.Resolve(movie, MetaSharkLibraryCapability.Metadata).Allowed;
    }

    private void QueueCollection(string collectionName)
    {
        lock (this.syncRoot)
        {
            if (this.isStopped)
            {
                return;
            }

            this.queuedTmdbCollection.Add(collectionName);
            this.scheduler.Schedule(this.debounceDelay, this.DrainQueuedCollectionsAsync);
        }
    }

    private Task DrainQueuedCollectionsAsync()
    {
        lock (this.syncRoot)
        {
            if (this.isStopped)
            {
                return Task.CompletedTask;
            }
        }

        if (Interlocked.CompareExchange(ref this.drainRunning, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        string[] tmdbCollectionNames;
        TaskCompletionSource drainCompletion;
        lock (this.syncRoot)
        {
            if (this.isStopped)
            {
                Interlocked.Exchange(ref this.drainRunning, 0);
                return Task.CompletedTask;
            }

            drainCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tmdbCollectionNames = this.queuedTmdbCollection.ToArray();
            this.queuedTmdbCollection.Clear();
            this.inFlightTmdbCollection = tmdbCollectionNames.ToList();
            this.currentDrainTask = drainCompletion.Task;
        }

        _ = this.RunDrainQueuedCollectionsAsync(tmdbCollectionNames, drainCompletion);
        return drainCompletion.Task;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A single collection drain failure must requeue that collection and continue the state machine.")]
    private async Task RunDrainQueuedCollectionsAsync(string[] tmdbCollectionNames, TaskCompletionSource drainCompletion)
    {
        try
        {
            await this.DrainQueuedCollectionsCoreAsync(tmdbCollectionNames).ConfigureAwait(false);
            drainCompletion.TrySetResult();
        }
        catch (Exception ex)
        {
            drainCompletion.TrySetException(ex);
        }
        finally
        {
            Interlocked.Exchange(ref this.drainRunning, 0);
            lock (this.syncRoot)
            {
                this.currentDrainTask = null;
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A single collection drain failure must requeue that collection and continue the state machine.")]
    private async Task DrainQueuedCollectionsCoreAsync(string[] tmdbCollectionNames)
    {
        var failedRetry = new HashSet<string>();
        List<BoxSet> boxSets;
        IDictionary<string, IList<Movie>> movies;
        try
        {
            boxSets = this.GetAllBoxSetsFromLibrary();
            movies = this.GetMoviesFromLibrary();
        }
        catch
        {
            failedRetry.UnionWith(tmdbCollectionNames);
            boxSets = new List<BoxSet>();
            movies = new Dictionary<string, IList<Movie>>();
        }

        foreach (var collectionName in tmdbCollectionNames)
        {
            if (!movies.TryGetValue(collectionName, out var collectionMovies))
            {
                continue;
            }

            try
            {
                var boxSet = boxSets.FirstOrDefault(b => b?.Name == collectionName);
                await this.AddMoviesToCollection(collectionMovies, collectionName, boxSet).ConfigureAwait(false);
            }
            catch
            {
                failedRetry.Add(collectionName);
            }
        }

        lock (this.syncRoot)
        {
            this.inFlightTmdbCollection.Clear();
            this.queuedTmdbCollection.UnionWith(failedRetry);
            if (!this.isStopped && this.queuedTmdbCollection.Count > 0)
            {
                this.scheduler.Schedule(this.debounceDelay, this.DrainQueuedCollectionsAsync);
            }
        }
    }
}

internal interface IBoxSetDebounceScheduler : IDisposable
{
    void Schedule(TimeSpan delay, Func<Task> callback);

    void Cancel();
}

internal sealed class TimerBoxSetDebounceScheduler : IBoxSetDebounceScheduler
{
    private readonly object syncRoot = new object();
    private readonly Timer timer;
    private Func<Task>? callback;

    public TimerBoxSetDebounceScheduler()
    {
        this.timer = new Timer(_ => this.OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Schedule(TimeSpan delay, Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (this.syncRoot)
        {
            this.callback = callback;
            this.timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Cancel()
    {
        lock (this.syncRoot)
        {
            this.callback = null;
            this.timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        this.timer.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Timer callback tasks must be observed; collection failures are handled by the drain state machine.")]
    private static async Task RunObservedAsync(Func<Task> callback)
    {
        try
        {
            await callback().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void OnTimerElapsed()
    {
        Func<Task>? scheduledCallback;
        lock (this.syncRoot)
        {
            this.timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            scheduledCallback = this.callback;
            this.callback = null;
        }

        if (scheduledCallback != null)
        {
            _ = RunObservedAsync(scheduledCallback);
        }
    }
}
