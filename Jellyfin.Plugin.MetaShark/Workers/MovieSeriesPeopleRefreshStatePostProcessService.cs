// <copyright file="MovieSeriesPeopleRefreshStatePostProcessService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Core;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Configuration;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using Microsoft.Extensions.Logging;

    public class MovieSeriesPeopleRefreshStatePostProcessService
    {
        public const string ItemUpdatedTrigger = "ItemUpdated";

        private const string LegacyPeopleRefreshStateProviderId = "MetaSharkPeopleRefreshState";

        private static readonly Regex LegacyPeopleRefreshStateNfoTagRegex = new Regex(
            @"^[ \t]*<metasharkpeoplerefreshstateid>.*?</metasharkpeoplerefreshstateid>[ \t]*\r?\n?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Action<ILogger, Guid, string, string, ItemUpdateType, string, Exception?> LogQueuedSearchMissingOverwriteRefresh =
            LoggerMessage.Define<Guid, string, string, ItemUpdateType, string>(LogLevel.Information, new EventId(4, nameof(TryQueueSearchMissingMetadataOverwriteRefresh)), "[MetaShark] 已排队影视人物 search-missing 单次 overwrite refresh. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} queuedItemPath={QueuedItemPath}.");

        private static readonly Action<ILogger, Guid, string, string, ItemUpdateType, int, Exception?> LogQueuedRelatedParentRefresh =
            LoggerMessage.Define<Guid, string, string, ItemUpdateType, int>(LogLevel.Information, new EventId(5, nameof(TryApplyAsync)), "[MetaShark] 已排队人物图片完成后的关联影视 refresh. personId={PersonId} trigger={Trigger} personPath={PersonPath} updateReason={UpdateReason} queuedParentCount={QueuedParentCount}.");

        private readonly ILogger<MovieSeriesPeopleRefreshStatePostProcessService> logger;
        private readonly IPeopleRefreshStateStore stateStore;
        private readonly IProviderManager? providerManager;
        private readonly IMovieSeriesPeopleOverwriteRefreshCandidateStore? overwriteRefreshCandidateStore;
        private readonly IFileSystem? fileSystem;
        private readonly ILibraryManager? libraryManager;

        public MovieSeriesPeopleRefreshStatePostProcessService(ILogger<MovieSeriesPeopleRefreshStatePostProcessService> logger, IPeopleRefreshStateStore stateStore, IProviderManager? providerManager = null, IMovieSeriesPeopleOverwriteRefreshCandidateStore? overwriteRefreshCandidateStore = null, IFileSystem? fileSystem = null, ILibraryManager? libraryManager = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(stateStore);

            this.logger = logger;
            this.stateStore = stateStore;
            this.providerManager = providerManager;
            this.overwriteRefreshCandidateStore = overwriteRefreshCandidateStore ?? InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore.Shared;
            this.fileSystem = fileSystem;
            this.libraryManager = libraryManager;
        }

#pragma warning disable CA1848
        public virtual async Task TryApplyAsync(ItemChangeEventArgs e, string triggerName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(e);
            ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
            cancellationToken.ThrowIfCancellationRequested();

            ValidateTriggerName(triggerName);

            if (e.Item is not BaseItem item)
            {
                return;
            }

            if (item.Id == Guid.Empty)
            {
                return;
            }

            this.logger.LogDebug(
                "[MetaShark] 收到影视人物刷新状态后处理事件. trigger={Trigger} itemId={ItemId} itemPath={ItemPath} updateReason={UpdateReason}.",
                triggerName,
                item.Id,
                item.Path ?? string.Empty,
                e.UpdateReason);

            if (item is Person person)
            {
                await this.TryQueueRelatedParentRefreshForPersonImageAsync(person, triggerName, e.UpdateReason, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!IsAcceptedUpdateReason(e.UpdateReason))
            {
                this.LogSkip("UpdateReasonRejected", triggerName, item, e.UpdateReason, null);
                return;
            }

            if (item is not Movie and not Series)
            {
                this.LogSkip(
                    "UnsupportedItemType",
                    triggerName,
                    item,
                    e.UpdateReason,
                    item.GetType().Name,
                    item is Season or Episode ? LogLevel.Debug : LogLevel.Information);
                return;
            }

            var currentState = this.stateStore.GetState(item.Id);
            if (this.TryQueueSearchMissingMetadataOverwriteRefresh(item, triggerName, e.UpdateReason, out var authoritativePeopleSnapshot))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(item.GetProviderId(MetadataProvider.Tmdb)))
            {
                this.LogSkip("MissingTmdbProviderId", triggerName, item, e.UpdateReason, null);
                return;
            }

            var legacyResidueRemoved = RemoveLegacyPeopleRefreshStateProviderId(item);

            if (authoritativePeopleSnapshot == null && PeopleRefreshState.HasCurrentState(item, currentState))
            {
                await this.PersistLegacyResidueCleanupAsync(item, triggerName, e.UpdateReason, legacyResidueRemoved, cancellationToken).ConfigureAwait(false);
                var legacyNfoResidueRemoved = this.CleanupLegacyPeopleRefreshStateNfoResidue(item, triggerName, e.UpdateReason);

                if (legacyResidueRemoved || legacyNfoResidueRemoved)
                {
                    return;
                }

                this.LogSkip("AlreadyCurrentState", triggerName, item, e.UpdateReason, PeopleRefreshState.CurrentVersion);
                return;
            }

            if (authoritativePeopleSnapshot == null
                && currentState?.AuthoritativePeopleSnapshot != null
                && PeopleRefreshState.GetCurrentAuthoritativePeopleStatus(item, currentState) == CurrentItemAuthoritativePeopleStatus.NonAuthoritative)
            {
                this.LogSkip("CurrentItemNotAuthoritative", triggerName, item, e.UpdateReason, PeopleRefreshState.CurrentVersion);
                return;
            }

            var settlementAuthoritativePeopleSnapshot = authoritativePeopleSnapshot;
            if (settlementAuthoritativePeopleSnapshot == null
                && (!TmdbAuthoritativePeopleSnapshot.TryCreateFromCurrentItem(item, out settlementAuthoritativePeopleSnapshot)
                    || settlementAuthoritativePeopleSnapshot == null))
            {
                this.LogSkip("StateSnapshotRejected", triggerName, item, e.UpdateReason, null);
                return;
            }

            var nextStateCreated = PeopleRefreshState.TryCreateCurrent(item, settlementAuthoritativePeopleSnapshot, out var nextState);
            if (!nextStateCreated || nextState == null)
            {
                this.LogSkip("StateSnapshotRejected", triggerName, item, e.UpdateReason, null);
                return;
            }

            try
            {
                this.stateStore.Save(nextState);
            }
            catch (Exception ex)
            {
                this.logger.LogError(
                    ex,
                    "[MetaShark] 影视人物刷新状态保存失败. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason}.",
                    item.Id,
                    triggerName,
                    item.Path ?? string.Empty,
                    e.UpdateReason);
                throw;
            }

            await this.PersistLegacyResidueCleanupAsync(item, triggerName, e.UpdateReason, legacyResidueRemoved, cancellationToken).ConfigureAwait(false);
            this.CleanupLegacyPeopleRefreshStateNfoResidue(item, triggerName, e.UpdateReason);

            this.logger.LogInformation(
                "[MetaShark] 已结清影视人物刷新状态. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} stateVersion={StateVersion}.",
                item.Id,
                triggerName,
                item.Path ?? string.Empty,
                e.UpdateReason,
                PeopleRefreshState.CurrentVersion);
        }
#pragma warning restore CA1848

        private static bool IsAcceptedUpdateReason(ItemUpdateType updateReason)
        {
            return updateReason.HasFlag(ItemUpdateType.MetadataImport)
                || updateReason.HasFlag(ItemUpdateType.MetadataDownload);
        }

        private static void ValidateTriggerName(string triggerName)
        {
            if (string.Equals(triggerName, ItemUpdatedTrigger, StringComparison.Ordinal))
            {
                return;
            }

            throw new ArgumentOutOfRangeException(nameof(triggerName), triggerName, "Only ItemUpdated trigger is supported.");
        }

        private static bool RemoveLegacyPeopleRefreshStateProviderId(BaseItem item)
        {
            return item.ProviderIds?.Remove(LegacyPeopleRefreshStateProviderId) ?? false;
        }

        private static MetadataRefreshOptions CreateSearchMissingOverwriteRefreshOptions(IFileSystem fileSystem)
        {
            return new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ReplaceAllImages = false,
            };
        }

        private static MetadataRefreshOptions CreateRelatedParentRefreshOptions(IFileSystem fileSystem)
        {
            return new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
            };
        }

        private static bool HasUsablePrimaryImage(Person person)
        {
            if (!person.HasImage(ImageType.Primary))
            {
                return false;
            }

            return HasValidImagePath(person.GetImagePath(ImageType.Primary, 0));
        }

        private static bool HasValidImagePath(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return false;
            }

            var candidates = imagePath
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => !string.IsNullOrWhiteSpace(path));

            return candidates.Any(IsValidImageCandidate);
        }

        private static bool IsValidImageCandidate(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                {
                    return File.Exists(uri.LocalPath);
                }

                return true;
            }

            return File.Exists(path);
        }

        private static bool TryGetCurrentPeople(BaseItem item, ILibraryManager libraryManager, out IReadOnlyList<object?> people)
        {
            if (item.SupportsPeople)
            {
                var peopleFromLibraryManager = libraryManager.GetPeople(item);
                if (peopleFromLibraryManager != null)
                {
                    people = peopleFromLibraryManager.Cast<object?>().ToArray();
                    return true;
                }
            }

            var currentType = item.GetType();
            while (currentType != null)
            {
                var getPeopleMethod = currentType.GetMethod("GetPeople", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (getPeopleMethod?.Invoke(item, null) is System.Collections.IEnumerable peopleFromMethod)
                {
                    people = peopleFromMethod.Cast<object?>().ToArray();
                    return true;
                }

                var peopleProperty = currentType.GetProperty("People", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (peopleProperty?.GetValue(item) is System.Collections.IEnumerable peopleFromProperty)
                {
                    people = peopleFromProperty.Cast<object?>().ToArray();
                    return true;
                }

                currentType = currentType.BaseType;
            }

            people = Array.Empty<object?>();
            return false;
        }

        private static IEnumerable<string> GetLegacyPeopleRefreshStateNfoPaths(BaseItem item)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                yield break;
            }

            if (item is Series)
            {
                if (Directory.Exists(item.Path))
                {
                    yield return Path.Combine(item.Path, "tvshow.nfo");
                }

                yield break;
            }

            if (item is not Movie)
            {
                yield break;
            }

            if (Directory.Exists(item.Path))
            {
                yield return Path.Combine(item.Path, "movie.nfo");
                yield break;
            }

            var directory = Path.GetDirectoryName(item.Path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                yield break;
            }

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(item.Path);
            if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            {
                yield return Path.Combine(directory, fileNameWithoutExtension + ".nfo");
            }

            yield return Path.Combine(directory, "movie.nfo");
        }

        private static bool TryRemoveLegacyPeopleRefreshStateTagFromNfo(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            var encoding = reader.CurrentEncoding;
            var updated = LegacyPeopleRefreshStateNfoTagRegex.Replace(content, string.Empty);
            if (string.Equals(content, updated, StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(path, updated, encoding);
            return true;
        }

#pragma warning disable CA1848
        private async Task PersistLegacyResidueCleanupAsync(BaseItem item, string triggerName, ItemUpdateType updateReason, bool legacyResidueRemoved, CancellationToken cancellationToken)
        {
            if (!legacyResidueRemoved)
            {
                return;
            }

            try
            {
                var repositoryUpdateReason = item.OnMetadataChanged();
                if (repositoryUpdateReason == 0)
                {
                    repositoryUpdateReason = ItemUpdateType.MetadataEdit;
                }

                await item.UpdateToRepositoryAsync(repositoryUpdateReason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(
                    ex,
                    "[MetaShark] 清理影视人物刷新 legacy provider id 残留失败. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} providerId={ProviderId}.",
                    item.Id,
                    triggerName,
                    item.Path ?? string.Empty,
                    updateReason,
                    LegacyPeopleRefreshStateProviderId);
                throw;
            }

            this.logger.LogInformation(
                "[MetaShark] 已清理影视人物刷新 legacy provider id 残留. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} providerId={ProviderId}.",
                item.Id,
                triggerName,
                item.Path ?? string.Empty,
                updateReason,
                LegacyPeopleRefreshStateProviderId);
        }

        private bool CleanupLegacyPeopleRefreshStateNfoResidue(BaseItem item, string triggerName, ItemUpdateType updateReason)
        {
            var cleaned = false;

            foreach (var nfoPath in GetLegacyPeopleRefreshStateNfoPaths(item))
            {
                try
                {
                    if (!TryRemoveLegacyPeopleRefreshStateTagFromNfo(nfoPath))
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogError(
                        ex,
                        "[MetaShark] 清理影视人物刷新 legacy NFO 残留失败. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} nfoPath={NfoPath}.",
                        item.Id,
                        triggerName,
                        item.Path ?? string.Empty,
                        updateReason,
                        nfoPath);
                    throw;
                }

                cleaned = true;
                this.logger.LogInformation(
                    "[MetaShark] 已清理影视人物刷新 legacy NFO 残留. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} nfoPath={NfoPath}.",
                    item.Id,
                    triggerName,
                    item.Path ?? string.Empty,
                    updateReason,
                    nfoPath);
            }

            return cleaned;
        }
#pragma warning restore CA1848

        private bool TryQueueSearchMissingMetadataOverwriteRefresh(BaseItem item, string triggerName, ItemUpdateType updateReason, out TmdbAuthoritativePeopleSnapshot? authoritativePeopleSnapshot)
        {
            authoritativePeopleSnapshot = null;

            if (!updateReason.HasFlag(ItemUpdateType.MetadataDownload)
                || this.providerManager == null
                || this.overwriteRefreshCandidateStore == null
                || this.fileSystem == null)
            {
                return false;
            }

            var candidate = this.overwriteRefreshCandidateStore.Consume(item.Id, item.Path ?? string.Empty);
            if (candidate == null)
            {
                return false;
            }

            var currentAuthoritativeStatus = CurrentItemAuthoritativePeopleChecker.Check(item, candidate.AuthoritativePeopleSnapshot);
            if (currentAuthoritativeStatus != CurrentItemAuthoritativePeopleStatus.NonAuthoritative)
            {
                authoritativePeopleSnapshot = candidate.AuthoritativePeopleSnapshot?.Clone();
                return false;
            }

            if (candidate.OverwriteQueued)
            {
                this.LogSkip(
                    "QueuedOverwriteStillNonAuthoritative",
                    triggerName,
                    item,
                    updateReason,
                    $"expectedPeopleCount={candidate.ExpectedPeopleCount}");
                this.overwriteRefreshCandidateStore.Save(candidate);
                return true;
            }

            try
            {
                this.providerManager.QueueRefresh(item.Id, CreateSearchMissingOverwriteRefreshOptions(this.fileSystem), RefreshPriority.Normal);
                candidate.OverwriteQueued = true;
                this.overwriteRefreshCandidateStore.Save(candidate);
            }
            catch
            {
                candidate.OverwriteQueued = false;
                this.overwriteRefreshCandidateStore.Save(candidate);
                throw;
            }

            LogQueuedSearchMissingOverwriteRefresh(
                this.logger,
                item.Id,
                triggerName,
                item.Path ?? string.Empty,
                updateReason,
                candidate.ItemPath ?? string.Empty,
                null);
            return true;
        }

        private Task TryQueueRelatedParentRefreshForPersonImageAsync(Person person, string triggerName, ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            if (!updateReason.HasFlag(ItemUpdateType.ImageUpdate)
                || !HasUsablePrimaryImage(person)
                || this.providerManager == null
                || this.fileSystem == null)
            {
                return Task.CompletedTask;
            }

            var personTmdbId = person.GetProviderId(MetadataProvider.Tmdb);
            if (string.IsNullOrWhiteSpace(personTmdbId))
            {
                return Task.CompletedTask;
            }

            var relatedItems = this.GetRelatedMovieSeriesItems(personTmdbId);
            if (relatedItems.Count == 0)
            {
                return Task.CompletedTask;
            }

            var refreshOptions = CreateRelatedParentRefreshOptions(this.fileSystem);
            var queuedIds = new HashSet<Guid>();

            foreach (var relatedItem in relatedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!queuedIds.Add(relatedItem.Id))
                {
                    continue;
                }

                this.providerManager.QueueRefresh(relatedItem.Id, refreshOptions, RefreshPriority.Normal);
            }

            if (queuedIds.Count > 0)
            {
                LogQueuedRelatedParentRefresh(this.logger, person.Id, triggerName, person.Path ?? string.Empty, updateReason, queuedIds.Count, null);
            }

            return Task.CompletedTask;
        }

        private List<BaseItem> GetRelatedMovieSeriesItems(string personTmdbId)
        {
            var effectiveLibraryManager = this.libraryManager ?? BaseItem.LibraryManager;
            if (effectiveLibraryManager == null)
            {
                return new List<BaseItem>();
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
            };

            var items = effectiveLibraryManager.GetItemList(query);
            return items
                .Where(item => item is Movie or Series)
                .Where(item => this.CurrentItemContainsTmdbPersonId(item, personTmdbId))
                .ToList();
        }

        private bool CurrentItemContainsTmdbPersonId(BaseItem item, string personTmdbId)
        {
            var effectiveLibraryManager = this.libraryManager ?? BaseItem.LibraryManager;
            if (effectiveLibraryManager == null)
            {
                return false;
            }

            if (!TryGetCurrentPeople(item, effectiveLibraryManager, out var people))
            {
                return false;
            }

            foreach (var currentPerson in people)
            {
                if (TmdbAuthoritativePersonFingerprint.TryCreateFromCurrentPerson(currentPerson, out var fingerprint)
                    && fingerprint != null
                    && string.Equals(fingerprint.TmdbPersonId, personTmdbId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

#pragma warning disable CA1848
        private void LogSkip(string reason, string triggerName, BaseItem item, ItemUpdateType updateReason, string? detail, LogLevel level = LogLevel.Information)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                this.logger.Log(
                    level,
                    "[MetaShark] 跳过影视人物刷新状态结清. reason={Reason} trigger={Trigger} itemId={ItemId} itemPath={ItemPath} updateReason={UpdateReason}.",
                    reason,
                    triggerName,
                    item.Id,
                    item.Path ?? string.Empty,
                    updateReason);
                return;
            }

            this.logger.Log(
                level,
                "[MetaShark] 跳过影视人物刷新状态结清. reason={Reason} trigger={Trigger} itemId={ItemId} itemPath={ItemPath} updateReason={UpdateReason} detail={Detail}.",
                reason,
                triggerName,
                item.Id,
                item.Path ?? string.Empty,
                updateReason,
                detail);
        }
#pragma warning restore CA1848
    }
}
