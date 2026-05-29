using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TmdbPerson = TMDbLib.Objects.People.Person;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class PersonNameResolverTest
    {
        [TestMethod]
        public async Task Scope_CachesResolvedNameWithinRequestAndDoesNotLeakAcrossScopes()
        {
            var tmdbApi = new TmdbApi(LoggerFactory.Create(_ => { }));
            var resolver = new PersonNameResolver(tmdbApi, Mock.Of<ILibraryManager>());
            var personTmdbId = 1201637;

            SeedTmdbPerson(tmdbApi, personTmdbId, "第一次请求名");
            var firstScope = resolver.CreateScope();
            var firstName = await firstScope.ResolveSimplifiedChineseOnlyItemPersonNameAsync("raw", personTmdbId, CancellationToken.None).ConfigureAwait(false);

            SeedTmdbPerson(tmdbApi, personTmdbId, "第二次请求名");
            var cachedFirstName = await firstScope.ResolveSimplifiedChineseOnlyItemPersonNameAsync("raw", personTmdbId, CancellationToken.None).ConfigureAwait(false);
            var secondScope = resolver.CreateScope();
            var secondName = await secondScope.ResolveSimplifiedChineseOnlyItemPersonNameAsync("raw", personTmdbId, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("第一次请求名", firstName);
            Assert.AreEqual("第一次请求名", cachedFirstName, "同一 request scope 内应复用已解析人物名。");
            Assert.AreEqual("第二次请求名", secondName, "新的 request scope 应重新读取底层 API 结果。");
        }

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task Scope_LockedExistingLibraryPersonName_DoesNotRenameOrPersist(bool itemLocked, bool nameLocked)
        {
            var tmdbApi = new TmdbApi(LoggerFactory.Create(_ => { }));
            var personTmdbId = 1201637;
            SeedTmdbPerson(tmdbApi, personTmdbId, "三瓶由布子");
            var existingPerson = new TrackingPerson
            {
                Name = "三瓶 由布子",
                IsLocked = itemLocked,
            };
            if (nameLocked)
            {
                existingPerson.LockedFields = new[] { MetadataField.Name };
            }

            existingPerson.SetProviderId(MetadataProvider.Tmdb, personTmdbId.ToString(CultureInfo.InvariantCulture));
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { existingPerson });
            var resolver = new PersonNameResolver(tmdbApi, libraryManagerStub.Object);

            var resolvedName = await resolver
                .CreateScope()
                .ResolveItemPersonNameAsync("三瓶 由布子", personTmdbId, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual("三瓶由布子", resolvedName, "当前 metadata 仍应使用本轮解析到的 TMDb 中文名。");
            Assert.AreEqual("三瓶 由布子", existingPerson.Name, "已锁定的库内 Person 实体名不能被插件直接覆盖。");
            Assert.AreEqual(0, existingPerson.MetadataChangedCallCount);
            Assert.AreEqual(0, existingPerson.UpdateToRepositoryCallCount);
        }

        private static void SeedTmdbPerson(TmdbApi tmdbApi, int tmdbId, string name)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                GetTmdbPersonCacheKey(tmdbId, "zh-CN", null),
                new TmdbPerson
                {
                    Id = tmdbId,
                    Name = name,
                },
                TimeSpan.FromMinutes(5));
        }

        private static IMemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未找到");
            return (IMemoryCache)memoryCacheField!.GetValue(tmdbApi)!;
        }

        private static string GetTmdbPersonCacheKey(int tmdbId, string language, string? countryCode)
        {
            var cacheKeyMethod = typeof(TmdbApi).GetMethod("GetPersonCacheKey", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheKeyMethod, "TmdbApi.GetPersonCacheKey 未定义");
            var cacheKey = cacheKeyMethod!.Invoke(null, new object?[] { tmdbId, language, countryCode }) as string;
            Assert.IsFalse(string.IsNullOrEmpty(cacheKey), "TmdbApi.GetPersonCacheKey 返回了无效缓存键");
            return cacheKey!;
        }

        private sealed class TrackingPerson : Person
        {
            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public override ItemUpdateType OnMetadataChanged()
            {
                this.MetadataChangedCallCount++;
                return ItemUpdateType.MetadataEdit;
            }

            public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            {
                this.UpdateToRepositoryCallCount++;
                return Task.CompletedTask;
            }
        }
    }
}
