// <copyright file="PersonMissingImageRefillService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Configuration;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using Microsoft.Extensions.Logging;

    public sealed class PersonMissingImageRefillService : IPersonMissingImageRefillService
    {
        private static readonly Action<ILogger, int, int, int, string, Exception?> LogScanSummary =
            LoggerMessage.Define<int, int, int, string>(LogLevel.Information, new EventId(1, nameof(QueueMissingImagesForFullLibraryScan)), "[MetaShark] 人物缺图回填扫描完成. candidateCount={CandidateCount} queuedCount={QueuedCount} skippedCount={SkippedCount} skippedReasons={SkippedReasons}.");

        private static readonly Action<ILogger, string, Guid, Exception?> LogQueuedRefresh =
            LoggerMessage.Define<string, Guid>(LogLevel.Debug, new EventId(2, nameof(QueueMissingImagesForFullLibraryScan)), "[MetaShark] 已排队人物缺图回填. name={Name} itemId={Id}.");

        private readonly ILogger<PersonMissingImageRefillService> logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly IPersonImageRefillStateStore stateStore;
        private readonly MetaSharkSharedEntityLibraryCapabilityResolver sharedEntityLibraryCapabilityResolver;

        public PersonMissingImageRefillService(
            ILoggerFactory loggerFactory,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            IPersonImageRefillStateStore? stateStore = null,
            MetaSharkSharedEntityLibraryCapabilityResolver? sharedEntityLibraryCapabilityResolver = null)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(providerManager);
            ArgumentNullException.ThrowIfNull(fileSystem);

            this.logger = loggerFactory.CreateLogger<PersonMissingImageRefillService>();
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.stateStore = stateStore ?? new FilePersonImageRefillStateStore(
                Path.Combine(Path.GetTempPath(), MetaSharkPlugin.PluginName, $"person-image-refill-state-{Guid.NewGuid():N}.json"),
                loggerFactory);
            this.sharedEntityLibraryCapabilityResolver = sharedEntityLibraryCapabilityResolver ?? new MetaSharkSharedEntityLibraryCapabilityResolver(libraryManager);
        }

        public PersonMissingImageRefillScanSummary QueueMissingImagesForFullLibraryScan(CancellationToken cancellationToken)
        {
            var persons = this.GetPersonsForRefill();

            var queuedCount = 0;
            var skippedCount = 0;
            var skippedReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var person in persons)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var skippedReason = this.QueueIfMissing(person);
                if (skippedReason is null)
                {
                    queuedCount++;
                    continue;
                }

                skippedCount++;
                if (skippedReasonCounts.TryGetValue(skippedReason, out var count))
                {
                    skippedReasonCounts[skippedReason] = count + 1;
                }
                else
                {
                    skippedReasonCounts[skippedReason] = 1;
                }
            }

            var summary = new PersonMissingImageRefillScanSummary(persons.Count, queuedCount, skippedCount, FormatSkippedReasons(skippedReasonCounts));
            LogScanSummary(this.logger, summary.CandidateCount, summary.QueuedCount, summary.SkippedCount, summary.SkippedReasons, null);
            return summary;
        }

        public void QueueMissingImagesForUpdatedItem(ItemChangeEventArgs e, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(e);
            cancellationToken.ThrowIfCancellationRequested();

            if (e.Item is not Person person)
            {
                return;
            }

            if (e.UpdateReason.HasFlag(ItemUpdateType.ImageUpdate))
            {
                if (HasUsablePrimaryImage(person))
                {
                    this.MarkCompleted(person, "ImageUpdateCompleted");
                }
                else
                {
                    var skippedReason = this.QueueIfMissing(person);
                    if (skippedReason is not null)
                    {
                        this.MarkRetryable(person, "PrimaryImageStillMissingAfterImageUpdate");
                    }
                }

                return;
            }

            _ = this.QueueIfMissing(person);
        }

        public void MarkCompleted(Person person, string reason)
        {
            ArgumentNullException.ThrowIfNull(person);

            if (person.Id == Guid.Empty)
            {
                return;
            }

            var currentState = this.stateStore.GetState(person.Id);
            var currentFingerprint = CreateFingerprint(person);
            var now = DateTimeOffset.UtcNow;
            if (!HasUsablePrimaryImage(person))
            {
                this.stateStore.Save(new PersonImageRefillState
                {
                    PersonId = person.Id,
                    Fingerprint = currentFingerprint,
                    Status = PersonImageRefillStatus.Pending,
                    AttemptCount = GetAttemptCountForSameFingerprint(currentState, currentFingerprint),
                    LastReason = NormalizeReason(reason, "PrimaryImageStillMissing"),
                    UpdatedAtUtc = now,
                });
                return;
            }

            this.stateStore.Save(new PersonImageRefillState
            {
                PersonId = person.Id,
                Fingerprint = currentFingerprint,
                Status = PersonImageRefillStatus.Completed,
                AttemptCount = GetAttemptCountForSameFingerprint(currentState, currentFingerprint),
                LastReason = NormalizeReason(reason, "PrimaryImagePresent"),
                UpdatedAtUtc = now,
            });
        }

        public void MarkRetryable(Person person, string reason, DateTimeOffset? nextRetryAtUtc = null)
        {
            ArgumentNullException.ThrowIfNull(person);

            if (person.Id == Guid.Empty)
            {
                return;
            }

            if (HasUsablePrimaryImage(person))
            {
                this.MarkCompleted(person, "PrimaryImagePresent");
                return;
            }

            var currentState = this.stateStore.GetState(person.Id);
            var currentFingerprint = CreateFingerprint(person);
            var attemptCount = string.Equals(currentState?.Fingerprint, currentFingerprint, StringComparison.Ordinal)
                ? currentState?.AttemptCount ?? 0
                : 0;

            this.stateStore.Save(new PersonImageRefillState
            {
                PersonId = person.Id,
                Fingerprint = currentFingerprint,
                Status = PersonImageRefillStatus.Retryable,
                AttemptCount = attemptCount + 1,
                LastReason = NormalizeReason(reason, "Retryable"),
                NextRetryAtUtc = nextRetryAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(30),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        private static int GetAttemptCountForSameFingerprint(PersonImageRefillState? currentState, string fingerprint)
        {
            return string.Equals(currentState?.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? currentState?.AttemptCount ?? 0
                : 0;
        }

        private static string NormalizeReason(string reason, string fallback)
        {
            return string.IsNullOrWhiteSpace(reason) ? fallback : reason;
        }

        private static string FormatSkippedReasons(Dictionary<string, int> skippedReasonCounts)
        {
            if (skippedReasonCounts.Count == 0)
            {
                return "None";
            }

            return string.Join(
                ";",
                skippedReasonCounts
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
        }

        private static string CreateFingerprint(Person person)
        {
            ArgumentNullException.ThrowIfNull(person);

            var tmdbId = person.GetProviderId(MetadataProvider.Tmdb) ?? string.Empty;
            var doubanId = person.GetProviderId(BaseProvider.DoubanProviderId) ?? string.Empty;
            return string.Join("|", person.Name ?? string.Empty, tmdbId, doubanId);
        }

        private static bool HasUsablePrimaryImage(Person person)
        {
            ArgumentNullException.ThrowIfNull(person);

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

        private List<Person> GetPersonsForRefill()
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Person },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
            };

            return this.libraryManager
                .GetItemList(query)
                .OfType<Person>()
                .Where(person => !HasUsablePrimaryImage(person))
                .ToList();
        }

        private string? QueueIfMissing(Person person)
        {
            ArgumentNullException.ThrowIfNull(person);

            if (person.Id == Guid.Empty)
            {
                return "EmptyId";
            }

            var currentFingerprint = CreateFingerprint(person);
            var currentState = this.stateStore.GetState(person.Id);
            if (currentState?.Status == PersonImageRefillStatus.Pending && string.Equals(currentState.Fingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                return "AlreadyPending";
            }

            if (currentState?.Status == PersonImageRefillStatus.Retryable
                && string.Equals(currentState.Fingerprint, currentFingerprint, StringComparison.Ordinal)
                && currentState.NextRetryAtUtc.HasValue
                && currentState.NextRetryAtUtc.Value > DateTimeOffset.UtcNow)
            {
                return "CooldownActive";
            }

            if (!this.IsImageAllowed(person, out var gateDecision))
            {
                return gateDecision?.Reason.ToString() ?? "ImageGateDenied";
            }

            var refreshOptions = this.CreateRefreshOptions();
            this.stateStore.Save(new PersonImageRefillState
            {
                PersonId = person.Id,
                Fingerprint = currentFingerprint,
                Status = PersonImageRefillStatus.Pending,
                AttemptCount = GetAttemptCountForSameFingerprint(currentState, currentFingerprint) + 1,
                LastReason = "QueuedRefresh",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            LogQueuedRefresh(this.logger, person.Name ?? string.Empty, person.Id, null);
            this.providerManager.QueueRefresh(person.Id, refreshOptions, RefreshPriority.Normal);

            return null;
        }

        private MetadataRefreshOptions CreateRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(this.fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
            };
        }

        private bool IsImageAllowed(Person person, out MetaSharkLibraryCapabilityDecision? gateDecision)
        {
            gateDecision = this.sharedEntityLibraryCapabilityResolver.Resolve(person, MetaSharkLibraryCapability.Image);
            return gateDecision.Allowed;
        }
    }
}
