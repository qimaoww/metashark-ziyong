using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using CandidateReason = Jellyfin.Plugin.MetaShark.Workers.MissingMetadataCandidateReason;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class PeriodicMissingMetadataSearchServiceTest
    {
        private const BindingFlags InstanceMemberBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchMissingProviderIds()
        {
            var movie = CreateItem<Movie>("Movie A", includeProviderIds: false, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(movie, CandidateReason.MissingProviderIds, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchEmptyOverview()
        {
            var season = CreateItem<Season>("Season 1", includeProviderIds: true, includeOverview: false, includePrimaryImage: true);

            AssertCandidate(season, CandidateReason.MissingOverview, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchMissingPrimaryImage()
        {
            var boxSet = CreateItem<BoxSet>("Collection A", includeProviderIds: true, includeOverview: true, includePrimaryImage: false);

            AssertCandidate(boxSet, CandidateReason.MissingPrimaryImage, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldKeepOverviewBeforePeopleRefreshState()
        {
            var series = CreateItem<Series>("Series Missing Overview", includeProviderIds: true, includeOverview: false, includePrimaryImage: true);

            AssertCandidate(series, CandidateReason.MissingOverview, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchDefaultEpisodeTitleOnlyForEpisode()
        {
            var episode = CreateItem<Episode>("第 1 集", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(episode, CandidateReason.DefaultEpisodeTitle, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchMissingPeopleRefreshStateForMovieWithoutState()
        {
            var movie = CreateItem<Movie>("Movie Missing People State", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(movie, CandidateReason.MissingPeopleRefreshState, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchMissingPeopleRefreshStateForSeriesWithStaleState()
        {
            var peopleRefreshStateStore = new TestPeopleRefreshStateStore();
            var series = CreateItem<Series>(
                "Series Stale People State",
                includeProviderIds: true,
                includeOverview: true,
                includePrimaryImage: true,
                peopleRefreshState: "tmdb-people-strict-zh-cn-v1",
                peopleRefreshStateStore: peopleRefreshStateStore);

            AssertCandidate(series, CandidateReason.MissingPeopleRefreshState, true, peopleRefreshStateStore);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldSkipLegacyAuthoritativeStateEvenWhenVersionIsOld()
        {
            var peopleRefreshStateStore = new TestPeopleRefreshStateStore();
            var authoritativePeople = new[]
            {
                CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"),
                CreatePerson("2001", "Director", "Director", "TMDb Director A"),
            };
            var movie = CreateAuthoritativeMovie("Movie Legacy Authoritative State", authoritativePeople);
            var snapshot = CreateAuthoritativePeopleSnapshot(movie, authoritativePeople);
            PeopleRefreshStateTestHelper.SaveState(peopleRefreshStateStore, movie, "tmdb-people-strict-zh-cn-v1", authoritativePeopleSnapshot: snapshot);

            AssertCandidate(movie, CandidateReason.CompleteMetadata, false, peopleRefreshStateStore);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchMissingPeopleRefreshStateWhenCurrentSnapshotDrifts()
        {
            var peopleRefreshStateStore = new TestPeopleRefreshStateStore();
            var authoritativePeople = new[]
            {
                CreatePerson("3001", "Actor", "角色B", "TMDb Actor B"),
                CreatePerson("4001", "Director", "Director", "TMDb Director B"),
            };
            var movie = CreateAuthoritativeMovie("Movie Drifted Current People State", authoritativePeople);
            var snapshot = CreateAuthoritativePeopleSnapshot(movie, authoritativePeople);
            PeopleRefreshStateTestHelper.SaveState(peopleRefreshStateStore, movie, PeopleRefreshState.CurrentVersion, authoritativePeopleSnapshot: snapshot);

            movie.SetSimulatedPeople(new[]
            {
                CreatePerson("3001", "Actor", "角色B-错误", "当前条目演员名"),
                CreatePerson("4001", "Director", "Director", "当前条目导演名"),
            });

            AssertCandidate(movie, CandidateReason.MissingPeopleRefreshState, true, peopleRefreshStateStore);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchMissingPeopleRefreshStateForCurrentVersionStateWithoutSnapshot()
        {
            var peopleRefreshStateStore = new TestPeopleRefreshStateStore();
            var movie = CreateAuthoritativeMovie("Movie Current People State Without Snapshot");
            PeopleRefreshStateTestHelper.SaveLegacyStateWithoutSnapshot(peopleRefreshStateStore, movie, PeopleRefreshState.CurrentVersion);

            AssertCandidate(movie, CandidateReason.MissingPeopleRefreshState, true, peopleRefreshStateStore);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldSkipCompleteSeriesEvenWhenNameLooksLikeDefaultEpisodeTitle()
        {
            var peopleRefreshStateStore = new TestPeopleRefreshStateStore();
            var series = CreateAuthoritativeSeries("第 1 集");
            series.Overview = "Series overview";
            series.SetImagePath(ImageType.Primary, "https://example.com/series-primary.jpg");
            PeopleRefreshStateTestHelper.SaveState(peopleRefreshStateStore, series, PeopleRefreshState.CurrentVersion);

            AssertCandidate(series, CandidateReason.CompleteMetadata, false, peopleRefreshStateStore);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldSkipMissingPeopleRefreshStateForNonMovieOrSeries()
        {
            var season = CreateItem<Season>("Season Current Metadata", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(season, CandidateReason.CompleteMetadata, false);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldSkipMissingPeopleRefreshStateWhenStateIsCurrent()
        {
            var peopleRefreshStateStore = new TestPeopleRefreshStateStore();
            var movie = CreateAuthoritativeMovie("Movie Current People State");
            PeopleRefreshStateTestHelper.SaveState(peopleRefreshStateStore, movie, PeopleRefreshState.CurrentVersion);

            AssertCandidate(movie, CandidateReason.CompleteMetadata, false, peopleRefreshStateStore);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldSkipUnsupportedType()
        {
            var person = CreateItem<Person>("Actor A", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(person, CandidateReason.UnsupportedType, false);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldSkipEmptyGuid()
        {
            var movie = CreateItem<Movie>("Movie B", id: Guid.Empty, includeProviderIds: true, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(movie, CandidateReason.EmptyId, false);
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_ShouldQuerySupportedItemTypesWithExpectedFlags()
        {
            InternalItemsQuery? capturedQuery = null;

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback<InternalItemsQuery>(query => capturedQuery = query)
                .Returns(new List<BaseItem>());

            var service = CreateService(libraryManagerStub.Object);

            await service.RunFullLibrarySearchAsync(new ProgressRecorder(), CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(capturedQuery);
            Assert.IsNotNull(capturedQuery!.IncludeItemTypes);
            Assert.AreEqual(5, capturedQuery.IncludeItemTypes!.Length);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Series,
                    BaseItemKind.Season,
                    BaseItemKind.Episode,
                    BaseItemKind.BoxSet,
                },
                capturedQuery.IncludeItemTypes);
            Assert.IsTrue(capturedQuery.Recursive);
            Assert.IsFalse(capturedQuery.IsVirtualItem);
            Assert.IsFalse(capturedQuery.IsMissing);
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenCandidatesAreEmpty_ShouldReport100AndSkipQueueing()
        {
            var peopleRefreshStateStore = new TestPeopleRefreshStateStore();
            var completeMovie = CreateAuthoritativeMovie("Movie Complete");
            PeopleRefreshStateTestHelper.SaveState(peopleRefreshStateStore, completeMovie, PeopleRefreshState.CurrentVersion);
            var completeSeason = CreateItem<Season>("Season Complete", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var unsupportedPerson = CreateItem<Person>("Person Non Candidate", includeProviderIds: false, includeOverview: false, includePrimaryImage: false);
            var progress = new ProgressRecorder();
            var delayInvocations = new List<DelayInvocation>();

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { completeMovie, completeSeason, unsupportedPerson });

            var providerManagerStub = new Mock<IProviderManager>();
            var loggerStub = new Mock<ILogger<MissingMetadataSearchService>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    return Task.CompletedTask;
                },
                logger: loggerStub.Object,
                peopleRefreshStateStore: peopleRefreshStateStore);

            await service.RunFullLibrarySearchAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { 100d }, progress.Values.ToArray());
            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
            Assert.AreEqual(0, delayInvocations.Count);
            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 开始全库搜索缺失元数据条目", messageContains: ["[MetaShark] 开始全库搜索缺失元数据条目"]);
            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 未找到缺失元数据条目", messageContains: ["[MetaShark] 未找到缺失元数据条目"]);
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenCandidatesExist_ShouldQueueOnlyCandidatesWithFixedRefreshOptionsAndDelay()
        {
            var peopleRefreshStateStore = new TestPeopleRefreshStateStore();
            var missingProviderMovie = CreateItem<Movie>("Movie Missing Provider", includeProviderIds: false, includeOverview: true, includePrimaryImage: true);
            var missingPeopleMovie = CreateItem<Movie>("Movie Missing People State", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var missingOverviewSeries = CreateItem<Series>("Series Missing Overview", includeProviderIds: true, includeOverview: false, includePrimaryImage: true);
            var stalePeopleSeries = CreateItem<Series>(
                "Series Stale People State",
                includeProviderIds: true,
                includeOverview: true,
                includePrimaryImage: true,
                peopleRefreshState: "tmdb-people-strict-zh-cn-v1",
                peopleRefreshStateStore: peopleRefreshStateStore);
            var legacyAuthoritativePeople = new[]
            {
                CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"),
                CreatePerson("2001", "Director", "Director", "TMDb Director A"),
            };
            var legacyAuthoritativeMovie = CreateAuthoritativeMovie("Movie Legacy Authoritative State", legacyAuthoritativePeople);
            PeopleRefreshStateTestHelper.SaveState(
                peopleRefreshStateStore,
                legacyAuthoritativeMovie,
                "tmdb-people-strict-zh-cn-v1",
                authoritativePeopleSnapshot: CreateAuthoritativePeopleSnapshot(legacyAuthoritativeMovie, legacyAuthoritativePeople));
            var driftedCurrentPeople = new[]
            {
                CreatePerson("3001", "Actor", "角色B", "TMDb Actor B"),
                CreatePerson("4001", "Director", "Director", "TMDb Director B"),
            };
            var driftedCurrentMovie = CreateAuthoritativeMovie("Movie Drifted Current People State", driftedCurrentPeople);
            PeopleRefreshStateTestHelper.SaveState(
                peopleRefreshStateStore,
                driftedCurrentMovie,
                PeopleRefreshState.CurrentVersion,
                authoritativePeopleSnapshot: CreateAuthoritativePeopleSnapshot(driftedCurrentMovie, driftedCurrentPeople));
            driftedCurrentMovie.SetSimulatedPeople(new[]
            {
                CreatePerson("3001", "Actor", "角色B-错误", "当前条目演员名"),
                CreatePerson("4001", "Director", "Director", "当前条目导演名"),
            });
            var currentNoSnapshotMovie = CreateAuthoritativeMovie("Movie Current People State Without Snapshot");
            PeopleRefreshStateTestHelper.SaveLegacyStateWithoutSnapshot(peopleRefreshStateStore, currentNoSnapshotMovie, PeopleRefreshState.CurrentVersion);
            var defaultTitleEpisode = CreateItem<Episode>("第 1 集", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var missingPeopleSeason = CreateItem<Season>("Season Missing People State", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var currentPeopleMovie = CreateAuthoritativeMovie("Movie Current People State");
            PeopleRefreshStateTestHelper.SaveState(peopleRefreshStateStore, currentPeopleMovie, PeopleRefreshState.CurrentVersion);
            var completeBoxSet = CreateItem<BoxSet>("Collection Complete", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var unsupportedPerson = CreateItem<Person>("Actor A", includeProviderIds: false, includeOverview: false, includePrimaryImage: false);
            var progress = new ProgressRecorder();
            var queueInvocations = new List<QueueRefreshInvocation>();
            var delayInvocations = new List<DelayInvocation>();
            using var cancellationTokenSource = new CancellationTokenSource();

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem>
                {
                    missingProviderMovie,
                    missingPeopleMovie,
                    unsupportedPerson,
                    missingOverviewSeries,
                    stalePeopleSeries,
                    legacyAuthoritativeMovie,
                    defaultTitleEpisode,
                    driftedCurrentMovie,
                    currentNoSnapshotMovie,
                    missingPeopleSeason,
                    currentPeopleMovie,
                    completeBoxSet,
                });

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) =>
                    queueInvocations.Add(new QueueRefreshInvocation
                    {
                        ItemId = itemId,
                        Options = options,
                        Priority = priority,
                    }));

            var loggerStub = new Mock<ILogger<MissingMetadataSearchService>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    return Task.CompletedTask;
                },
                logger: loggerStub.Object,
                peopleRefreshStateStore: peopleRefreshStateStore);

            await service.RunFullLibrarySearchAsync(progress, cancellationTokenSource.Token).ConfigureAwait(false);

            Assert.AreEqual(7, progress.Values.Count);
            Assert.IsTrue(progress.Values.SequenceEqual(progress.Values.OrderBy(x => x)), "Progress should be monotonic increasing.");
            Assert.AreEqual(100d, progress.Values[progress.Values.Count - 1], 0.000001d);

            CollectionAssert.AreEqual(
                new[] { missingProviderMovie.Id, missingPeopleMovie.Id, missingOverviewSeries.Id, stalePeopleSeries.Id, defaultTitleEpisode.Id, driftedCurrentMovie.Id, currentNoSnapshotMovie.Id },
                queueInvocations.Select(x => x.ItemId).ToArray());

            Assert.AreEqual(7, delayInvocations.Count);
            Assert.IsTrue(delayInvocations.All(x => x.Delay == TimeSpan.FromSeconds(5)));
            Assert.IsTrue(delayInvocations.All(x => x.CancellationToken.Equals(cancellationTokenSource.Token)));

            foreach (var invocation in queueInvocations)
            {
                Assert.AreEqual(RefreshPriority.Normal, invocation.Priority);
                AssertRefreshOptions(invocation.Options, invocation.ItemId == missingPeopleMovie.Id || invocation.ItemId == stalePeopleSeries.Id || invocation.ItemId == driftedCurrentMovie.Id || invocation.ItemId == currentNoSnapshotMovie.Id);
            }

            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 开始全库搜索缺失元数据条目", messageContains: ["[MetaShark] 开始全库搜索缺失元数据条目"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = missingProviderMovie.Id,
                    ["ItemName"] = "Movie Missing Provider",
                    ["Reason"] = CandidateReason.MissingProviderIds.ToString(),
                    ["DelaySeconds"] = 5,
                },
                originalFormatContains: "[MetaShark] 已排队缺失元数据刷新",
                messageContains: ["[MetaShark] 已排队缺失元数据刷新", "reason=MissingProviderIds"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = missingPeopleMovie.Id,
                    ["ItemName"] = "Movie Missing People State",
                    ["Reason"] = CandidateReason.MissingPeopleRefreshState.ToString(),
                    ["DelaySeconds"] = 5,
                },
                originalFormatContains: "[MetaShark] 已排队缺失元数据刷新",
                messageContains: ["[MetaShark] 已排队缺失元数据刷新", "reason=MissingPeopleRefreshState"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = missingOverviewSeries.Id,
                    ["ItemName"] = "Series Missing Overview",
                    ["Reason"] = CandidateReason.MissingOverview.ToString(),
                    ["DelaySeconds"] = 5,
                },
                originalFormatContains: "[MetaShark] 已排队缺失元数据刷新",
                messageContains: ["[MetaShark] 已排队缺失元数据刷新", "reason=MissingOverview"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = stalePeopleSeries.Id,
                    ["ItemName"] = "Series Stale People State",
                    ["Reason"] = CandidateReason.MissingPeopleRefreshState.ToString(),
                    ["DelaySeconds"] = 5,
                },
                originalFormatContains: "[MetaShark] 已排队缺失元数据刷新",
                messageContains: ["[MetaShark] 已排队缺失元数据刷新", "reason=MissingPeopleRefreshState"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = defaultTitleEpisode.Id,
                    ["ItemName"] = "第 1 集",
                    ["Reason"] = CandidateReason.DefaultEpisodeTitle.ToString(),
                    ["DelaySeconds"] = 5,
                },
                originalFormatContains: "[MetaShark] 已排队缺失元数据刷新",
                messageContains: ["[MetaShark] 已排队缺失元数据刷新", "reason=DefaultEpisodeTitle"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = driftedCurrentMovie.Id,
                    ["ItemName"] = "Movie Drifted Current People State",
                    ["Reason"] = CandidateReason.MissingPeopleRefreshState.ToString(),
                    ["DelaySeconds"] = 5,
                },
                originalFormatContains: "[MetaShark] 已排队缺失元数据刷新",
                messageContains: ["[MetaShark] 已排队缺失元数据刷新", "reason=MissingPeopleRefreshState"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = currentNoSnapshotMovie.Id,
                    ["ItemName"] = "Movie Current People State Without Snapshot",
                    ["Reason"] = CandidateReason.MissingPeopleRefreshState.ToString(),
                    ["DelaySeconds"] = 5,
                },
                originalFormatContains: "[MetaShark] 已排队缺失元数据刷新",
                messageContains: ["[MetaShark] 已排队缺失元数据刷新", "reason=MissingPeopleRefreshState"]);
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenCandidatesSpanEnabledAndDisabledLibraries_ShouldQueueOnlyEnabledOrdinaryItems()
        {
            var enabledMovie = CreateItem<Movie>("Enabled Movie Missing Provider", includeProviderIds: false, includeOverview: true, includePrimaryImage: true);
            var disabledSeries = CreateItem<Series>("Disabled Series Missing Overview", includeProviderIds: true, includeOverview: false, includePrimaryImage: true);
            var enabledEpisode = CreateItem<Episode>("第 1 集", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var queueInvocations = new List<QueueRefreshInvocation>();
            var delayInvocations = new List<DelayInvocation>();
            var progress = new ProgressRecorder();

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { enabledMovie, disabledSeries, enabledEpisode });

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) =>
                    queueInvocations.Add(new QueueRefreshInvocation
                    {
                        ItemId = itemId,
                        Options = options,
                        Priority = priority,
                    }));

            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    return Task.CompletedTask;
                },
                metadataAllowed: item => item.Id != disabledSeries.Id);

            await service.RunFullLibrarySearchAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(
                new[] { enabledMovie.Id, enabledEpisode.Id },
                queueInvocations.Select(x => x.ItemId).ToArray());
            Assert.AreEqual(2, delayInvocations.Count);
            Assert.AreEqual(2, progress.Values.Count);
            Assert.AreEqual(100d, progress.Values[^1], 0.000001d);
            Assert.IsTrue(delayInvocations.All(x => x.Delay == TimeSpan.FromSeconds(5)));
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenBoxSetCandidatesSpanMixedAndDisabledLibraries_ShouldQueueOnlyMetadataAllowedBoxSets()
        {
            var enabledLinkedMovie = CreateItem<Movie>("Enabled Linked Movie", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var disabledLinkedMovie = CreateItem<Movie>("Disabled Linked Movie", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var mixedBoxSet = CreateLinkedBoxSetCandidate("Mixed BoxSet Candidate", enabledLinkedMovie, disabledLinkedMovie);
            var disabledOnlyBoxSet = CreateLinkedBoxSetCandidate("Disabled Only BoxSet Candidate", disabledLinkedMovie);
            var queueInvocations = new List<QueueRefreshInvocation>();
            var delayInvocations = new List<DelayInvocation>();
            var progress = new ProgressRecorder();
            var itemsById = new Dictionary<Guid, BaseItem>
            {
                [enabledLinkedMovie.Id] = enabledLinkedMovie,
                [disabledLinkedMovie.Id] = disabledLinkedMovie,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { mixedBoxSet, disabledOnlyBoxSet });
            libraryManagerStub
                .Setup(x => x.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid itemId) => itemsById.TryGetValue(itemId, out var item) ? item : null!);

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) =>
                    queueInvocations.Add(new QueueRefreshInvocation
                    {
                        ItemId = itemId,
                        Options = options,
                        Priority = priority,
                    }));

            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    return Task.CompletedTask;
                },
                metadataAllowed: item => item.Id == enabledLinkedMovie.Id);

            await service.RunFullLibrarySearchAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { mixedBoxSet.Id }, queueInvocations.Select(x => x.ItemId).ToArray());
            Assert.AreEqual(1, delayInvocations.Count);
            CollectionAssert.AreEqual(new[] { 100d }, progress.Values.ToArray());
            Assert.AreEqual(RefreshPriority.Normal, queueInvocations[0].Priority);
            AssertRefreshOptions(queueInvocations[0].Options, expectedReplaceAllMetadata: false);
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenAnotherRunIsInFlight_ShouldSkipImmediatelyWithoutQueueingSecondRun()
        {
            var missingProviderMovie = CreateItem<Movie>("Movie Missing Provider", includeProviderIds: false, includeOverview: true, includePrimaryImage: true);
            var firstProgress = new ProgressRecorder();
            var secondProgress = new ProgressRecorder();
            var queueInvocations = new List<QueueRefreshInvocation>();
            var delayInvocations = new List<DelayInvocation>();
            var firstDelayEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstDelay = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { missingProviderMovie });

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) =>
                    queueInvocations.Add(new QueueRefreshInvocation
                    {
                        ItemId = itemId,
                        Options = options,
                        Priority = priority,
                    }));

            var loggerStub = new Mock<ILogger<MissingMetadataSearchService>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    firstDelayEntered.TrySetResult(null);
                    return releaseFirstDelay.Task;
                },
                logger: loggerStub.Object);

            var firstRunTask = service.RunFullLibrarySearchAsync(firstProgress, CancellationToken.None);
            await firstDelayEntered.Task.ConfigureAwait(false);

            try
            {
                var secondRunTask = service.RunFullLibrarySearchAsync(secondProgress, CancellationToken.None);

                Assert.IsTrue(secondRunTask.IsCompleted, "Second invocation should skip immediately instead of waiting for the in-flight run.");
                Assert.IsFalse(firstRunTask.IsCompleted, "First invocation should still be blocked in delay when the second invocation returns.");

                await secondRunTask.ConfigureAwait(false);

                CollectionAssert.AreEqual(new[] { missingProviderMovie.Id }, queueInvocations.Select(x => x.ItemId).ToArray());
                Assert.AreEqual(1, delayInvocations.Count);
                CollectionAssert.AreEqual(new[] { 100d }, secondProgress.Values.ToArray());
                LogAssert.AssertLoggedOnce(
                    loggerStub,
                    LogLevel.Information,
                    expectException: false,
                    originalFormatContains: "[MetaShark] 跳过全库搜索缺失元数据条目",
                    messageContains: ["[MetaShark] 跳过全库搜索缺失元数据条目", "reason=AlreadyRunning"]);
            }
            finally
            {
                releaseFirstDelay.TrySetResult(null);
                await firstRunTask.ConfigureAwait(false);
            }
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenDelayIsCanceled_ShouldPropagateOperationCanceledExceptionAndStopFurtherQueueing()
        {
            var missingProviderMovie = CreateItem<Movie>("Movie Missing Provider", includeProviderIds: false, includeOverview: true, includePrimaryImage: true);
            var missingOverviewSeries = CreateItem<Series>("Series Missing Overview", includeProviderIds: true, includeOverview: false, includePrimaryImage: true);
            var progress = new ProgressRecorder();
            var queueInvocations = new List<QueueRefreshInvocation>();
            var delayInvocations = new List<DelayInvocation>();
            var firstDelayEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationTokenSource = new CancellationTokenSource();

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { missingProviderMovie, missingOverviewSeries });

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) =>
                    queueInvocations.Add(new QueueRefreshInvocation
                    {
                        ItemId = itemId,
                        Options = options,
                        Priority = priority,
                    }));

            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    firstDelayEntered.TrySetResult(null);

                    var delayTaskSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    token.Register(() => delayTaskSource.TrySetCanceled(token));
                    return delayTaskSource.Task;
                });

            var runTask = service.RunFullLibrarySearchAsync(progress, cancellationTokenSource.Token);
            await firstDelayEntered.Task.ConfigureAwait(false);

            cancellationTokenSource.Cancel();

            var exception = await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await runTask.ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsInstanceOfType(exception, typeof(OperationCanceledException));
            CollectionAssert.AreEqual(new[] { missingProviderMovie.Id }, queueInvocations.Select(x => x.ItemId).ToArray());
            Assert.AreEqual(1, delayInvocations.Count);
            Assert.AreEqual(1, progress.Values.Count);
            Assert.AreEqual(50d, progress.Values[0], 0.000001d);
        }

        private static MissingMetadataSearchService CreateService(
            ILibraryManager libraryManager,
            IProviderManager? providerManager = null,
            IFileSystem? fileSystem = null,
            Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
            ILogger<MissingMetadataSearchService>? logger = null,
            IPeopleRefreshStateStore? peopleRefreshStateStore = null,
            Func<BaseItem, bool>? metadataAllowed = null)
        {
            ConfigureLibraryOptions(libraryManager, metadataAllowed);

            return new MissingMetadataSearchService(
                logger ?? Mock.Of<ILogger<MissingMetadataSearchService>>(),
                libraryManager,
                providerManager ?? Mock.Of<IProviderManager>(),
                fileSystem ?? Mock.Of<IFileSystem>(),
                peopleRefreshStateStore ?? new TestPeopleRefreshStateStore(),
                delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken)));
        }

        private static void ConfigureLibraryOptions(ILibraryManager libraryManager, Func<BaseItem, bool>? metadataAllowed)
        {
            Mock.Get(libraryManager)
                .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns<BaseItem>(item => CreateLibraryOptions(item, metadataAllowed?.Invoke(item) ?? true));
        }

        private static LibraryOptions CreateLibraryOptions(BaseItem item, bool metadataAllowed)
        {
            var itemType = item switch
            {
                Movie => nameof(Movie),
                Series => nameof(Series),
                Season => nameof(Season),
                Episode => nameof(Episode),
                _ => null,
            };

            if (itemType == null)
            {
                return new LibraryOptions
                {
                    TypeOptions = Array.Empty<TypeOptions>(),
                };
            }

            return new LibraryOptions
            {
                TypeOptions = new[]
                {
                    new TypeOptions
                    {
                        Type = itemType,
                        MetadataFetchers = metadataAllowed ? new[] { MetaSharkPlugin.PluginName } : Array.Empty<string>(),
                        ImageFetchers = Array.Empty<string>(),
                    },
                },
            };
        }

        private static T CreateItem<T>(
            string name,
            Guid? id = null,
            bool includeProviderIds = true,
            bool includeOverview = true,
            bool includePrimaryImage = true,
            string? peopleRefreshState = null,
            IPeopleRefreshStateStore? peopleRefreshStateStore = null)
            where T : BaseItem, new()
        {
            var item = new T
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
            };

            if (includeProviderIds)
            {
                item.SetProviderId(
                    item is Series ? BaseProvider.MetaSharkTmdbProviderId : MetadataProvider.Tmdb.ToString(),
                    $"{typeof(T).Name}-tmdb-1");
            }

            if (includeOverview)
            {
                item.Overview = $"{typeof(T).Name} overview";
            }

            if (includePrimaryImage)
            {
                item.SetImagePath(ImageType.Primary, $"https://example.com/{typeof(T).Name.ToLowerInvariant()}-primary.jpg");
            }

            if (!string.IsNullOrWhiteSpace(peopleRefreshState))
            {
                ArgumentNullException.ThrowIfNull(peopleRefreshStateStore);
                PeopleRefreshStateTestHelper.SaveState(peopleRefreshStateStore, item, peopleRefreshState);
            }

            return item;
        }

        private static BoxSet CreateLinkedBoxSetCandidate(string name, params BaseItem[] linkedItems)
        {
            var boxSet = CreateItem<BoxSet>(name, includeProviderIds: false, includeOverview: true, includePrimaryImage: true);
            SetLinkedChildren(boxSet, linkedItems);
            return boxSet;
        }

        private static AuthoritativeTrackingMovie CreateAuthoritativeMovie(string name, params PersonInfo[] people)
        {
            var movie = CreateItem<AuthoritativeTrackingMovie>(name, includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            movie.SetSimulatedPeople(people);
            return movie;
        }

        private static AuthoritativeTrackingSeries CreateAuthoritativeSeries(string name, params PersonInfo[] people)
        {
            var series = CreateItem<AuthoritativeTrackingSeries>(name, includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            series.SetSimulatedPeople(people);
            return series;
        }

        private static TmdbAuthoritativePeopleSnapshot CreateAuthoritativePeopleSnapshot(BaseItem item, IEnumerable<PersonInfo> people)
        {
            var tmdbId = item.GetTmdbId();
            Assert.IsFalse(string.IsNullOrWhiteSpace(tmdbId), "测试前提：authoritative item 必须带 TMDb provider id。 ");

            return TmdbAuthoritativePeopleSnapshot.Create(item is Series ? nameof(Series) : nameof(Movie), tmdbId!, people);
        }

        private static PersonInfo CreatePerson(string? tmdbPersonId, string personTypeName, string role, string name)
        {
            var person = new PersonInfo
            {
                Name = name,
                Role = role,
            };

            var typeProperty = typeof(PersonInfo).GetProperty("Type", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(typeProperty, "PersonInfo.Type 未定义。 ");
            Assert.IsTrue(typeProperty!.PropertyType.IsEnum, "PersonInfo.Type 应为枚举类型。 ");
            typeProperty.SetValue(person, Enum.Parse(typeProperty.PropertyType, personTypeName, ignoreCase: false));

            if (!string.IsNullOrWhiteSpace(tmdbPersonId))
            {
                person.SetProviderId(MetadataProvider.Tmdb, tmdbPersonId);
            }

            return person;
        }

        private static void AssertCandidate(BaseItem item, CandidateReason expectedReason, bool expectedCandidate, IPeopleRefreshStateStore? peopleRefreshStateStore = null)
        {
            var peopleRefreshState = peopleRefreshStateStore?.GetState(item.Id);
            var reason = MissingMetadataSearchService.ResolveMissingMetadataCandidateReason(item, peopleRefreshState);
            var isCandidate = MissingMetadataSearchService.IsMissingMetadataSearchCandidate(item, peopleRefreshState);

            Assert.AreEqual(expectedReason, reason);
            Assert.AreEqual(expectedCandidate, isCandidate);
        }

        private static void AssertRefreshOptions(MetadataRefreshOptions options, bool expectedReplaceAllMetadata)
        {
            Assert.IsNotNull(options, "QueueRefresh should receive a MetadataRefreshOptions instance.");
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, options.MetadataRefreshMode);
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, options.ImageRefreshMode);
            Assert.AreEqual(expectedReplaceAllMetadata, options.ReplaceAllMetadata);
            Assert.IsFalse(options.ReplaceAllImages);
        }

        private sealed class ProgressRecorder : IProgress<double>
        {
            public List<double> Values { get; } = new();

            public void Report(double value)
            {
                this.Values.Add(value);
            }
        }

        private sealed class QueueRefreshInvocation
        {
            public Guid ItemId { get; set; }

            public MetadataRefreshOptions Options { get; set; } = null!;

            public RefreshPriority Priority { get; set; }
        }

        private sealed class DelayInvocation
        {
            public TimeSpan Delay { get; set; }

            public CancellationToken CancellationToken { get; set; }
        }

        private static void SetLinkedChildren(BoxSet boxSet, IEnumerable<BaseItem> linkedItems)
        {
            var linkedChildrenProperty = typeof(BoxSet).GetProperty("LinkedChildren", InstanceMemberBindingFlags);
            Assert.IsNotNull(linkedChildrenProperty, "BoxSet.LinkedChildren 属性不存在。 ");

            var linkedChildType = ResolveLinkedChildType(linkedChildrenProperty!.PropertyType);
            Assert.IsNotNull(linkedChildType, "无法解析 BoxSet.LinkedChildren 元素类型。 ");

            var linkedChildren = linkedItems
                .Select(item => CreateLinkedChild(linkedChildType!, item.Id))
                .ToArray();
            var linkedChildrenValue = CreateLinkedChildrenValue(linkedChildrenProperty.PropertyType, linkedChildType!, linkedChildren);
            linkedChildrenProperty.SetValue(boxSet, linkedChildrenValue);
        }

        private static object CreateLinkedChildrenValue(Type propertyType, Type linkedChildType, object[] linkedChildren)
        {
            var arrayType = linkedChildType.MakeArrayType();
            if (propertyType.IsAssignableFrom(arrayType))
            {
                var array = Array.CreateInstance(linkedChildType, linkedChildren.Length);
                for (var i = 0; i < linkedChildren.Length; i++)
                {
                    array.SetValue(linkedChildren[i], i);
                }

                return array;
            }

            var listType = typeof(List<>).MakeGenericType(linkedChildType);
            if (propertyType.IsAssignableFrom(listType))
            {
                var list = (IList)Activator.CreateInstance(listType)!;
                foreach (var linkedChild in linkedChildren)
                {
                    list.Add(linkedChild);
                }

                return list;
            }

            var value = Activator.CreateInstance(propertyType);
            Assert.IsNotNull(value, "无法创建 BoxSet.LinkedChildren 容器实例。 ");

            if (value is IList nonGenericList)
            {
                foreach (var linkedChild in linkedChildren)
                {
                    nonGenericList.Add(linkedChild);
                }

                return value!;
            }

            var addMethod = propertyType.GetMethod("Add", new[] { linkedChildType });
            Assert.IsNotNull(addMethod, "BoxSet.LinkedChildren 容器不支持 Add。 ");
            foreach (var linkedChild in linkedChildren)
            {
                addMethod!.Invoke(value, new[] { linkedChild });
            }

            return value!;
        }

        private static object CreateLinkedChild(Type linkedChildType, Guid itemId)
        {
            var linkedChild = Activator.CreateInstance(linkedChildType);
            Assert.IsNotNull(linkedChild, "无法创建 LinkedChild 实例。 ");

            var itemIdProperty = linkedChildType.GetProperty(nameof(LinkedChild.ItemId), InstanceMemberBindingFlags);
            Assert.IsNotNull(itemIdProperty, "LinkedChild.ItemId 属性不存在。 ");
            itemIdProperty!.SetValue(linkedChild, itemId);
            return linkedChild!;
        }

        private static Type? ResolveLinkedChildType(Type propertyType)
        {
            if (propertyType.IsArray)
            {
                return propertyType.GetElementType();
            }

            if (propertyType.IsGenericType)
            {
                return propertyType.GetGenericArguments().FirstOrDefault();
            }

            return typeof(LinkedChild);
        }
    }
}
