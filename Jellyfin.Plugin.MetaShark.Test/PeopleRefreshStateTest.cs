using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MetaShark.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

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
            var state = PeopleRefreshStateTestHelper.CreateState(series, version: "tmdb-people-strict-zh-cn-v0");

            Assert.IsTrue(PeopleRefreshState.RequiresBackfill(series, state));
            Assert.IsTrue(PeopleRefreshState.IsStale(series, state));
            Assert.IsFalse(PeopleRefreshState.HasCurrentState(series, state));
        }

        [TestMethod]
        public void HasCurrentState_ItemWithCurrentState_ShouldReturnTrue()
        {
            var movie = CreateItem<Movie>(includeTmdb: true);
            var state = PeopleRefreshStateTestHelper.CreateState(movie, PeopleRefreshState.CurrentVersion);

            Assert.IsFalse(PeopleRefreshState.RequiresBackfill(movie, state));
            Assert.IsTrue(PeopleRefreshState.HasCurrentState(movie, state));
            Assert.IsFalse(PeopleRefreshState.IsMissing(movie, state));
            Assert.IsFalse(PeopleRefreshState.IsStale(movie, state));
        }

        [TestMethod]
        public void HasCurrentState_WhenTmdbIdChanges_ShouldReturnFalse()
        {
            var movie = CreateItem<Movie>(includeTmdb: true);
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
            Assert.IsFalse(movie.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false);
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
                item.SetProviderId(MetadataProvider.Tmdb, "123456");
            }

            return item;
        }
    }

    internal static class PeopleRefreshStateTestHelper
    {
        public static PeopleRefreshState CreateState(BaseItem item, string version, DateTimeOffset? updatedAtUtc = null)
        {
            if (!PeopleRefreshState.TryCreateCurrent(item, out var state) || state == null)
            {
                throw new InvalidOperationException("Cannot create people refresh state for the provided item.");
            }

            state.Version = version;
            state.UpdatedAtUtc = updatedAtUtc ?? DateTimeOffset.UtcNow;
            return state;
        }

        public static void SaveState(IPeopleRefreshStateStore stateStore, BaseItem item, string version, DateTimeOffset? updatedAtUtc = null)
        {
            ArgumentNullException.ThrowIfNull(stateStore);
            stateStore.Save(CreateState(item, version, updatedAtUtc));
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
                UpdatedAtUtc = state.UpdatedAtUtc,
            };
        }
    }
}
