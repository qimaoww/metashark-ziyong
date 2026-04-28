using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class PeopleRefreshStateTest
    {
        [TestMethod]
        public void RequiresBackfill_MovieWithoutState_ShouldReturnTrue()
        {
            var movie = CreateItem<Movie>(includeTmdb: true);

            Assert.IsTrue(PeopleRefreshState.RequiresBackfill(movie, state: null));
            Assert.IsTrue(PeopleRefreshState.IsMissing(movie, state: null));
            Assert.IsFalse(PeopleRefreshState.HasCurrentState(movie, state: null));
        }

        [TestMethod]
        public void RequiresBackfill_SeriesWithExpiredState_ShouldReturnTrue()
        {
            var series = CreateItem<Series>(includeTmdb: true);
            var state = PeopleRefreshStateTestHelper.CreateState(series, version: "tmdb-people-strict-zh-cn-v1");

            Assert.IsTrue(PeopleRefreshState.RequiresBackfill(series, state));
            Assert.IsTrue(PeopleRefreshState.IsStale(series, state));
            Assert.IsFalse(PeopleRefreshState.HasCurrentState(series, state));
        }

        [TestMethod]
        public void HasCurrentState_ItemWithCurrentState_ShouldReturnTrue()
        {
            var movie = CreateAuthoritativeMovie(includeTmdb: true);
            var state = PeopleRefreshStateTestHelper.CreateState(movie, PeopleRefreshState.CurrentVersion);

            Assert.IsFalse(PeopleRefreshState.RequiresBackfill(movie, state));
            Assert.IsTrue(PeopleRefreshState.HasCurrentState(movie, state));
            Assert.IsFalse(PeopleRefreshState.IsMissing(movie, state));
            Assert.IsFalse(PeopleRefreshState.IsStale(movie, state));
            Assert.IsNotNull(state.AuthoritativePeopleSnapshot);
            Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.AuthoritativeEmpty, PeopleRefreshState.GetCurrentAuthoritativePeopleStatus(movie, state));
        }

        [TestMethod]
        public void HasCurrentState_WhenCurrentVersionStateHasNoSnapshot_ShouldReturnFalse()
        {
            var movie = CreateAuthoritativeMovie(includeTmdb: true);
            var state = PeopleRefreshStateTestHelper.CreateLegacyStateWithoutSnapshot(movie, PeopleRefreshState.CurrentVersion);

            Assert.IsTrue(PeopleRefreshState.RequiresBackfill(movie, state));
            Assert.IsFalse(PeopleRefreshState.HasCurrentState(movie, state));
            Assert.IsFalse(PeopleRefreshState.IsMissing(movie, state));
            Assert.IsTrue(PeopleRefreshState.IsStale(movie, state));
        }

        [TestMethod]
        public void HasCurrentState_WhenLegacyVersionSnapshotStillMatchesAuthoritativeItem_ShouldReturnTrue()
        {
            var movie = CreateAuthoritativeMovie(
                includeTmdb: true,
                CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"),
                CreatePerson("2001", "Director", "Director", "TMDb Director A"));
            var snapshot = TmdbAuthoritativePeopleSnapshot.Create(nameof(Movie), movie.GetProviderId(MetadataProvider.Tmdb)!, movie.GetSimulatedPeople());
            var state = PeopleRefreshStateTestHelper.CreateState(movie, "tmdb-people-strict-zh-cn-v1", authoritativePeopleSnapshot: snapshot);

            Assert.IsFalse(PeopleRefreshState.RequiresBackfill(movie, state));
            Assert.IsTrue(PeopleRefreshState.HasCurrentState(movie, state));
            Assert.IsFalse(PeopleRefreshState.IsMissing(movie, state));
            Assert.IsFalse(PeopleRefreshState.IsStale(movie, state));
            Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.Authoritative, PeopleRefreshState.GetCurrentAuthoritativePeopleStatus(movie, state));
        }

        [TestMethod]
        public void HasCurrentState_WhenCurrentVersionSnapshotNoLongerMatchesItem_ShouldReturnFalse()
        {
            var movie = CreateAuthoritativeMovie(
                includeTmdb: true,
                CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"),
                CreatePerson("2001", "Director", "Director", "TMDb Director A"));
            var snapshot = TmdbAuthoritativePeopleSnapshot.Create(nameof(Movie), movie.GetProviderId(MetadataProvider.Tmdb)!, movie.GetSimulatedPeople());
            var state = PeopleRefreshStateTestHelper.CreateState(movie, PeopleRefreshState.CurrentVersion, authoritativePeopleSnapshot: snapshot);

            movie.SetSimulatedPeople(new[]
            {
                CreatePerson("1001", "Actor", "角色A-错误", "当前条目演员名"),
                CreatePerson("2001", "Director", "Director", "当前条目导演名"),
            });

            Assert.IsTrue(PeopleRefreshState.RequiresBackfill(movie, state));
            Assert.IsFalse(PeopleRefreshState.HasCurrentState(movie, state));
            Assert.IsFalse(PeopleRefreshState.IsMissing(movie, state));
            Assert.IsTrue(PeopleRefreshState.IsStale(movie, state));
            Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.NonAuthoritative, PeopleRefreshState.GetCurrentAuthoritativePeopleStatus(movie, state));
        }

        [TestMethod]
        public void HasCurrentState_WhenTmdbIdChanges_ShouldReturnFalse()
        {
            var movie = CreateAuthoritativeMovie(includeTmdb: true);
            var state = PeopleRefreshStateTestHelper.CreateState(movie, PeopleRefreshState.CurrentVersion);
            movie.SetProviderId(MetadataProvider.Tmdb, "654321");

            Assert.IsTrue(PeopleRefreshState.RequiresBackfill(movie, state));
            Assert.IsTrue(PeopleRefreshState.IsStale(movie, state));
            Assert.IsFalse(PeopleRefreshState.HasCurrentState(movie, state));
        }

        [TestMethod]
        public void RequiresBackfill_NonTmdbMovie_ShouldReturnFalse()
        {
            var movie = CreateItem<Movie>(includeTmdb: false);

            Assert.IsFalse(PeopleRefreshState.RequiresBackfill(movie, state: null));
            Assert.IsFalse(PeopleRefreshState.IsInScope(movie));
            Assert.IsFalse(PeopleRefreshState.HasCurrentState(movie, state: null));
        }

        [TestMethod]
        public void RequiresBackfill_NonMovieOrSeriesItem_ShouldReturnFalse()
        {
            var episode = CreateItem<Episode>(includeTmdb: true);

            Assert.IsFalse(PeopleRefreshState.RequiresBackfill(episode, state: null));
            Assert.IsFalse(PeopleRefreshState.IsInScope(episode));
            Assert.IsFalse(PeopleRefreshState.HasCurrentState(episode, state: null));
        }

        [TestMethod]
        public void TryCreateCurrent_ShouldCaptureItemIdentityWithoutMutatingProviderIds()
        {
            var movie = CreateItem<Movie>(includeTmdb: true);
            movie.SetProviderId(MetadataProvider.Imdb, "tt1234567");
            movie.SetProviderId(MetaSharkPlugin.ProviderId, "legacy-meta-shark-value");

            var created = PeopleRefreshState.TryCreateCurrent(movie, out var state);

            Assert.IsTrue(created);
            Assert.IsNotNull(state);
            Assert.AreEqual(movie.Id, state!.ItemId);
            Assert.AreEqual(nameof(Movie), state.ItemType);
            Assert.AreEqual("123456", state.TmdbId);
            Assert.AreEqual(PeopleRefreshState.CurrentVersion, state.Version);
            Assert.AreNotEqual(default, state.UpdatedAtUtc);
            Assert.AreEqual("123456", movie.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("tt1234567", movie.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("legacy-meta-shark-value", movie.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.IsNull(state.AuthoritativePeopleSnapshot);
            Assert.IsFalse(movie.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false);
        }

        [TestMethod]
        public void CreateState_CurrentVersionWithoutExplicitSnapshot_ShouldCaptureAuthoritativeSnapshotWhenPossible()
        {
            var movie = CreateAuthoritativeMovie(
                includeTmdb: true,
                CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"));

            var state = PeopleRefreshStateTestHelper.CreateState(movie, PeopleRefreshState.CurrentVersion);

            Assert.IsNotNull(state.AuthoritativePeopleSnapshot);
            Assert.IsTrue(state.AuthoritativePeopleSnapshot!.MatchesIdentity(movie));
            Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.Authoritative, PeopleRefreshState.GetCurrentAuthoritativePeopleStatus(movie, state));
        }

        [TestMethod]
        public void TryCreateCurrent_WithAuthoritativeSnapshot_ShouldCaptureSnapshotWithoutMutatingProviderIds()
        {
            var movie = CreateAuthoritativeMovie(
                includeTmdb: true,
                CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"),
                CreatePerson("2001", "Director", "Director", "TMDb Director A"));
            movie.SetProviderId(MetadataProvider.Imdb, "tt1234567");

            var snapshot = TmdbAuthoritativePeopleSnapshot.Create(nameof(Movie), "123456", movie.GetSimulatedPeople());

            var created = PeopleRefreshState.TryCreateCurrent(movie, snapshot, out var state);

            Assert.IsTrue(created);
            Assert.IsNotNull(state);
            Assert.IsNotNull(state!.AuthoritativePeopleSnapshot);
            Assert.IsTrue(state.AuthoritativePeopleSnapshot!.SetEquals(snapshot));
            Assert.AreEqual("123456", movie.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("tt1234567", movie.GetProviderId(MetadataProvider.Imdb));
            Assert.IsFalse(ReferenceEquals(snapshot, state.AuthoritativePeopleSnapshot), "state 内应保存快照副本，避免外部后续修改污染 state。 ");
        }

        [TestMethod]
        public void FilePeopleRefreshStateStore_SaveAndLoadShouldPreserveAuthoritativeSnapshot()
        {
            var movie = CreateAuthoritativeMovie(
                includeTmdb: true,
                CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"),
                CreatePerson("2001", "Director", "Director", "TMDb Director A"));
            var snapshot = TmdbAuthoritativePeopleSnapshot.Create(nameof(Movie), "123456", movie.GetSimulatedPeople());
            var state = PeopleRefreshStateTestHelper.CreateState(movie, PeopleRefreshState.CurrentVersion, authoritativePeopleSnapshot: snapshot);
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-people-refresh-state-{Guid.NewGuid():N}");
            var stateFilePath = Path.Combine(tempRoot, "people-refresh-state.json");
            using var loggerFactory = LoggerFactory.Create(static _ => { });

            try
            {
                var store = new FilePeopleRefreshStateStore(stateFilePath, loggerFactory);
                store.Save(state);

                var roundTrippedState = store.GetState(movie.Id);

                Assert.IsNotNull(roundTrippedState);
                Assert.IsNotNull(roundTrippedState!.AuthoritativePeopleSnapshot);
                Assert.IsTrue(roundTrippedState.AuthoritativePeopleSnapshot!.SetEquals(snapshot));
                Assert.AreEqual(
                    CurrentItemAuthoritativePeopleStatus.Authoritative,
                    CurrentItemAuthoritativePeopleChecker.Check(movie, roundTrippedState),
                    "真实文件 store roundtrip 后仍应能保留 authoritative snapshot 并判定为 authoritative。 ");
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [TestMethod]
        public void CurrentItemAuthoritativePeopleChecker_ExactFingerprintMatchShouldReturnAuthoritativeEvenWhenNamesDiffer()
        {
            var movie = CreateAuthoritativeMovie(
                includeTmdb: true,
                CreatePerson("1001", "Actor", "角色A", "当前条目演员名"),
                CreatePerson("2001", "Director", "Director", "当前条目导演名"));
            var authoritativeSnapshot = TmdbAuthoritativePeopleSnapshot.Create(
                nameof(Movie),
                "123456",
                new[]
                {
                    CreatePerson("1001", "Actor", "角色A", "TMDb authoritative actor name"),
                    CreatePerson("2001", "Director", "Director", "TMDb authoritative director name"),
                });

            var status = CurrentItemAuthoritativePeopleChecker.Check(movie, authoritativeSnapshot);

            Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.Authoritative, status);
            Assert.IsTrue(CurrentItemAuthoritativePeopleChecker.IsAuthoritative(movie, authoritativeSnapshot));
            Assert.IsFalse(CurrentItemAuthoritativePeopleChecker.IsAuthoritativeEmpty(movie, authoritativeSnapshot));
        }

        [TestMethod]
        public void CurrentItemAuthoritativePeopleChecker_SameCountWrongSourceShouldReturnNonAuthoritative()
        {
            var movie = CreateAuthoritativeMovie(
                includeTmdb: true,
                CreatePerson(tmdbPersonId: null, "Actor", "角色A", "TMDb Actor A"),
                CreatePerson(tmdbPersonId: null, "Director", "Director", "TMDb Director A"));
            var authoritativeSnapshot = TmdbAuthoritativePeopleSnapshot.Create(
                nameof(Movie),
                "123456",
                new[]
                {
                    CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"),
                    CreatePerson("2001", "Director", "Director", "TMDb Director A"),
                });

            var status = CurrentItemAuthoritativePeopleChecker.Check(movie, authoritativeSnapshot);

            Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.NonAuthoritative, status);
            Assert.IsFalse(CurrentItemAuthoritativePeopleChecker.IsAuthoritative(movie, authoritativeSnapshot));
        }

        [TestMethod]
        public void CurrentItemAuthoritativePeopleChecker_WrongRoleShouldReturnNonAuthoritative()
        {
            var movie = CreateAuthoritativeMovie(
                includeTmdb: true,
                CreatePerson("1001", "Actor", "角色A-错误", "当前条目演员名"),
                CreatePerson("2001", "Director", "Director", "当前条目导演名"));
            var authoritativeSnapshot = TmdbAuthoritativePeopleSnapshot.Create(
                nameof(Movie),
                "123456",
                new[]
                {
                    CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"),
                    CreatePerson("2001", "Director", "Director", "TMDb Director A"),
                });

            var status = CurrentItemAuthoritativePeopleChecker.Check(movie, authoritativeSnapshot);

            Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.NonAuthoritative, status);
        }

        [TestMethod]
        public void CurrentItemAuthoritativePeopleChecker_ShouldPreferLibraryManagerPeople_WhenGetPeopleLacksTmdbProviderIds()
        {
            var movie = new LibraryManagerPreferredAuthoritativeMovie
            {
                Id = Guid.NewGuid(),
                Name = "LibraryManager Preferred Movie",
            };
            movie.SetProviderId(MetadataProvider.Tmdb, "123456");
            movie.SetSimulatedPeople(new object[]
            {
                new { Type = "Actor", Role = "角色A" },
                new { Type = "Director", Role = "Director" },
            });

            var libraryManagerPeople = new[]
            {
                CreatePerson("1001", "Actor", "角色A", "当前条目演员名"),
                CreatePerson("2001", "Director", "Director", "当前条目导演名"),
            };
            var authoritativeSnapshot = TmdbAuthoritativePeopleSnapshot.Create(
                nameof(Movie),
                "123456",
                new[]
                {
                    CreatePerson("1001", "Actor", "角色A", "TMDb authoritative actor name"),
                    CreatePerson("2001", "Director", "Director", "TMDb authoritative director name"),
                });

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetPeople(It.Is<BaseItem>(item => ReferenceEquals(item, movie))))
                .Returns(() => libraryManagerPeople.ToList());

            var previousLibraryManager = BaseItem.LibraryManager;
            try
            {
                BaseItem.LibraryManager = libraryManagerStub.Object;

                var status = CurrentItemAuthoritativePeopleChecker.Check(movie, authoritativeSnapshot);

                Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.Authoritative, status);
                Assert.IsTrue(CurrentItemAuthoritativePeopleChecker.IsAuthoritative(movie, authoritativeSnapshot));
            }
            finally
            {
                BaseItem.LibraryManager = previousLibraryManager;
            }
        }

        [TestMethod]
        public void CurrentItemAuthoritativePeopleChecker_EmptyAuthoritativeSnapshotWithNoCurrentPeopleShouldReturnAuthoritativeEmpty()
        {
            var series = CreateAuthoritativeSeries(includeTmdb: true);
            var authoritativeSnapshot = TmdbAuthoritativePeopleSnapshot.Create(nameof(Series), "123456", Array.Empty<PersonInfo>());

            var status = CurrentItemAuthoritativePeopleChecker.Check(series, authoritativeSnapshot);

            Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.AuthoritativeEmpty, status);
            Assert.IsTrue(CurrentItemAuthoritativePeopleChecker.IsAuthoritative(series, authoritativeSnapshot));
            Assert.IsTrue(CurrentItemAuthoritativePeopleChecker.IsAuthoritativeEmpty(series, authoritativeSnapshot));
        }

        [TestMethod]
        public void CurrentItemAuthoritativePeopleChecker_EmptyAuthoritativeSnapshotWithDoubanResidueShouldReturnNonAuthoritative()
        {
            var series = CreateAuthoritativeSeries(
                includeTmdb: true,
                CreatePerson(tmdbPersonId: null, "Actor", "角色A", "旧人物A"));
            var authoritativeSnapshot = TmdbAuthoritativePeopleSnapshot.Create(nameof(Series), "123456", Array.Empty<PersonInfo>());

            var status = CurrentItemAuthoritativePeopleChecker.Check(series, authoritativeSnapshot);

            Assert.AreEqual(CurrentItemAuthoritativePeopleStatus.NonAuthoritative, status);
        }

        private static T CreateItem<T>(bool includeTmdb)
            where T : BaseItem, new()
        {
            var item = new T
            {
                Id = Guid.NewGuid(),
                Name = typeof(T).Name,
            };

            if (includeTmdb)
            {
                item.SetProviderId(item is Series ? BaseProvider.MetaSharkTmdbProviderId : MetadataProvider.Tmdb.ToString(), "123456");
            }

            return item;
        }

        private static AuthoritativeTrackingMovie CreateAuthoritativeMovie(bool includeTmdb, params PersonInfo[] people)
        {
            var movie = new AuthoritativeTrackingMovie
            {
                Id = Guid.NewGuid(),
                Name = "Authoritative Movie",
            };

            if (includeTmdb)
            {
                movie.SetProviderId(MetadataProvider.Tmdb, "123456");
            }

            movie.SetSimulatedPeople(people);
            return movie;
        }

        private static AuthoritativeTrackingSeries CreateAuthoritativeSeries(bool includeTmdb, params PersonInfo[] people)
        {
            var series = new AuthoritativeTrackingSeries
            {
                Id = Guid.NewGuid(),
                Name = "Authoritative Series",
            };

            if (includeTmdb)
            {
                series.SetProviderId(BaseProvider.MetaSharkTmdbProviderId, "123456");
            }

            series.SetSimulatedPeople(people);
            return series;
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
    }

    internal static class PeopleRefreshStateTestHelper
    {
        public static PeopleRefreshState CreateState(BaseItem item, string version, DateTimeOffset? updatedAtUtc = null, TmdbAuthoritativePeopleSnapshot? authoritativePeopleSnapshot = null)
        {
            if (authoritativePeopleSnapshot == null
                && string.Equals(version, PeopleRefreshState.CurrentVersion, StringComparison.Ordinal)
                && TmdbAuthoritativePeopleSnapshot.TryCreateFromCurrentItem(item, out var currentSnapshot)
                && currentSnapshot != null)
            {
                authoritativePeopleSnapshot = currentSnapshot;
            }

            var created = authoritativePeopleSnapshot == null
                ? PeopleRefreshState.TryCreateCurrent(item, out var state)
                : PeopleRefreshState.TryCreateCurrent(item, authoritativePeopleSnapshot, out state);

            if (!created || state == null)
            {
                throw new InvalidOperationException("Cannot create people refresh state for the provided item.");
            }

            state.Version = version;
            state.UpdatedAtUtc = updatedAtUtc ?? DateTimeOffset.UtcNow;
            return state;
        }

        public static PeopleRefreshState CreateLegacyStateWithoutSnapshot(BaseItem item, string version, DateTimeOffset? updatedAtUtc = null)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (item is not Movie and not Series)
            {
                throw new InvalidOperationException("Cannot create legacy people refresh state for a non-movie/series item.");
            }

            var tmdbId = item.GetTmdbId();
            if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(tmdbId))
            {
                throw new InvalidOperationException("Cannot create legacy people refresh state without item identity.");
            }

            return new PeopleRefreshState
            {
                ItemId = item.Id,
                ItemType = item is Movie ? nameof(Movie) : nameof(Series),
                TmdbId = tmdbId,
                Version = version,
                AuthoritativePeopleSnapshot = null,
                UpdatedAtUtc = updatedAtUtc ?? DateTimeOffset.UtcNow,
            };
        }

        public static void SaveState(IPeopleRefreshStateStore stateStore, BaseItem item, string version, DateTimeOffset? updatedAtUtc = null, TmdbAuthoritativePeopleSnapshot? authoritativePeopleSnapshot = null)
        {
            ArgumentNullException.ThrowIfNull(stateStore);
            stateStore.Save(CreateState(item, version, updatedAtUtc, authoritativePeopleSnapshot));
        }

        public static void SaveLegacyStateWithoutSnapshot(IPeopleRefreshStateStore stateStore, BaseItem item, string version, DateTimeOffset? updatedAtUtc = null)
        {
            ArgumentNullException.ThrowIfNull(stateStore);
            stateStore.Save(CreateLegacyStateWithoutSnapshot(item, version, updatedAtUtc));
        }
    }

    internal sealed class TestPeopleRefreshStateStore : IPeopleRefreshStateStore
    {
        private readonly Dictionary<Guid, PeopleRefreshState> states = new();

        public int SaveCallCount { get; private set; }

        public int RemoveCallCount { get; private set; }

        public PeopleRefreshState? GetState(Guid itemId)
        {
            return this.states.TryGetValue(itemId, out var state)
                ? Clone(state)
                : null;
        }

        public void Save(PeopleRefreshState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            this.SaveCallCount++;
            this.states[state.ItemId] = Clone(state);
        }

        public void Remove(Guid itemId)
        {
            this.RemoveCallCount++;
            this.states.Remove(itemId);
        }

        private static PeopleRefreshState Clone(PeopleRefreshState state)
        {
            return new PeopleRefreshState
            {
                ItemId = state.ItemId,
                ItemType = state.ItemType,
                TmdbId = state.TmdbId,
                Version = state.Version,
                AuthoritativePeopleSnapshot = state.AuthoritativePeopleSnapshot?.Clone(),
                UpdatedAtUtc = state.UpdatedAtUtc,
            };
        }
    }

    internal sealed class AuthoritativeTrackingMovie : Movie
    {
        private List<PersonInfo> simulatedPeople = new List<PersonInfo>();

        public void SetSimulatedPeople(IEnumerable<PersonInfo> people)
        {
            this.simulatedPeople = people.ToList();
        }

        public IReadOnlyList<PersonInfo> GetSimulatedPeople()
        {
            return this.simulatedPeople.ToList();
        }

        private System.Collections.IEnumerable GetPeople()
        {
            return this.simulatedPeople;
        }
    }

    internal sealed class AuthoritativeTrackingSeries : Series
    {
        private List<PersonInfo> simulatedPeople = new List<PersonInfo>();

        public void SetSimulatedPeople(IEnumerable<PersonInfo> people)
        {
            this.simulatedPeople = people.ToList();
        }

        private System.Collections.IEnumerable GetPeople()
        {
            return this.simulatedPeople;
        }
    }

    internal sealed class LibraryManagerPreferredAuthoritativeMovie : Movie
    {
        private List<object> simulatedPeople = new List<object>();

        public override bool SupportsPeople => true;

        public void SetSimulatedPeople(IEnumerable<object> people)
        {
            this.simulatedPeople = people.ToList();
        }

        private System.Collections.IEnumerable GetPeople()
        {
            return this.simulatedPeople;
        }
    }
}
