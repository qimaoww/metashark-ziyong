using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Providers.Compatibility;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class EpisodeRefreshTitleGuardTest
    {
        internal const string BookwormEmbeddedTitle = "小書痴的下剋上 為了成為圖書管理員不擇手段！領主的養女";
        internal const string HellModeEmbeddedTitle = "地獄模式 ～喜歡挑戰特殊成就的玩家在廢設定的異世界成為無雙～ 2nd Season";

        [DataTestMethod]
        [DataRow("达穆尔的请求", BookwormEmbeddedTitle)]
        [DataRow("第 1 集", HellModeEmbeddedTitle)]
        [DataRow("自定义标题（保留空格）  ", BookwormEmbeddedTitle)]
        [DataRow("使用者的繁體單集標題", HellModeEmbeddedTitle)]
        [DataRow("A manually edited episode title", BookwormEmbeddedTitle)]
        public async Task SearchMissing_RestoresExactPreProbeTitleWithoutLanguageHeuristics(string originalTitle, string embeddedTitle)
        {
            var guard = CreateGuard();
            var capture = new EpisodeTitleSnapshotProvider(guard);
            var restore = new EpisodeTitleRestoreProvider(guard);
            var item = CreateEpisode(originalTitle);
            var options = CreateOptions();

            Assert.IsInstanceOfType(capture, typeof(IPreRefreshProvider));
            Assert.IsInstanceOfType(restore, typeof(IPreRefreshProvider));
            Assert.IsTrue(capture.Order < 100, "必须先于宿主 ProbeProvider 保存标题。");
            Assert.IsTrue(restore.Order > 100, "必须在 ProbeProvider 之后、远程刮削之前恢复标题。");
            Assert.AreEqual(ItemUpdateType.None, await capture.FetchAsync(item, options, CancellationToken.None));
            item.Name = embeddedTitle;
            Assert.AreEqual(ItemUpdateType.MetadataImport, await restore.FetchAsync(item, options, CancellationToken.None));

            Assert.AreEqual(originalTitle, item.Name);
        }

        [DataTestMethod]
        [DataRow(MetadataRefreshMode.FullRefresh, true)]
        [DataRow(MetadataRefreshMode.Default, false)]
        [DataRow(MetadataRefreshMode.Default, true)]
        [DataRow(MetadataRefreshMode.ValidationOnly, false)]
        [DataRow(MetadataRefreshMode.None, false)]
        public void OtherRefreshModes_AreUnchanged(MetadataRefreshMode mode, bool replaceAll)
        {
            var guard = CreateGuard();
            var item = CreateEpisode("达穆尔的请求");
            var options = CreateOptions(mode, replaceAll);

            guard.Capture(item, options);
            item.Name = BookwormEmbeddedTitle;

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual(BookwormEmbeddedTitle, item.Name);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void IdentifyOrRemoveOldMetadata_AreUnchanged(bool removeOldMetadata)
        {
            var guard = CreateGuard();
            var item = CreateEpisode("已有标题");
            var options = CreateOptions();
            options.RemoveOldMetadata = removeOldMetadata;
            options.SearchResult = removeOldMetadata ? null : new RemoteSearchResult { Name = "识别结果" };

            guard.Capture(item, options);
            item.Name = "新识别的标题";

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual("新识别的标题", item.Name);
        }

        [DataTestMethod]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(false, false)]
        public void EmbeddedTitlesOrMetaSharkMetadataDisabled_DoesNotIntervene(bool embeddedTitles, bool metadataEnabled)
        {
            var guard = CreateGuard(embeddedTitles, metadataEnabled);
            var item = CreateEpisode("已有标题");
            var options = CreateOptions();

            guard.Capture(item, options);
            item.Name = "其他来源的标题";

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual("其他来源的标题", item.Name);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("  ")]
        public void EmptyTitle_RemainsAvailableForNormalMetadataImport(string? originalTitle)
        {
            var guard = CreateGuard();
            var item = CreateEpisode(originalTitle);
            var options = CreateOptions();

            guard.Capture(item, options);
            item.Name = "内嵌单集标题";

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual("内嵌单集标题", item.Name);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void LockedItemOrName_DoesNotCaptureOrChangeLocks(bool wholeItem)
        {
            var guard = CreateGuard();
            var item = CreateEpisode("锁定标题");
            item.IsLocked = wholeItem;
            item.LockedFields = wholeItem ? [] : [MetadataField.Name];
            var lockedFields = item.LockedFields;
            var options = CreateOptions();

            guard.Capture(item, options);
            item.Name = "锁定后的其他编辑";

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual("锁定后的其他编辑", item.Name);
            Assert.AreEqual(wholeItem, item.IsLocked);
            Assert.AreSame(lockedFields, item.LockedFields);
        }

        [TestMethod]
        public void NameLockedAfterCapture_IsNotRestored()
        {
            var guard = CreateGuard();
            var item = CreateEpisode("旧标题");
            var options = CreateOptions();

            guard.Capture(item, options);
            item.Name = "新锁定标题";
            item.LockedFields = [MetadataField.Name];

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual("新锁定标题", item.Name);
        }

        [TestMethod]
        public void Extras_AreNotChanged()
        {
            var guard = CreateGuard();
            var item = CreateEpisode("花絮");
            item.ExtraType = ExtraType.Trailer;
            var options = CreateOptions();

            guard.Capture(item, options);
            item.Name = "内嵌花絮标题";

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual("内嵌花絮标题", item.Name);
        }

        [TestMethod]
        public void VirtualMissingEpisode_IsNotChanged()
        {
            var guard = CreateGuard();
            var item = CreateEpisode("缺失单集");
            item.Path = null;
            var options = CreateOptions();

            guard.Capture(item, options);
            item.Name = "其他来源标题";

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual("其他来源标题", item.Name);
        }

        [TestMethod]
        public void Restore_ChangesOnlyNameAndDoesNotRepeatAfterLaterEdit()
        {
            var guard = CreateGuard();
            var item = CreateEpisode("达穆尔的请求");
            var options = CreateOptions();
            item.LockedFields = [MetadataField.Overview];

            guard.Capture(item, options);
            item.Name = BookwormEmbeddedTitle;
            item.Overview = "本轮保留的简介";
            item.ParentIndexNumber = 4;
            item.IndexNumber = 18;
            item.PreferredMetadataLanguage = "zh-CN";
            item.ProviderIds["Example"] = "unchanged";
            var lockedFields = item.LockedFields;

            Assert.AreEqual(ItemUpdateType.MetadataImport, guard.Restore(item, options));
            Assert.AreEqual("达穆尔的请求", item.Name);
            Assert.AreEqual("本轮保留的简介", item.Overview);
            Assert.AreEqual(4, item.ParentIndexNumber);
            Assert.AreEqual(18, item.IndexNumber);
            Assert.AreEqual("zh-CN", item.PreferredMetadataLanguage);
            Assert.AreEqual("unchanged", item.ProviderIds["Example"]);
            Assert.AreSame(lockedFields, item.LockedFields);

            item.Name = "用户之后编辑的标题";
            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual("用户之后编辑的标题", item.Name);
        }

        [TestMethod]
        public void SameTitle_DoesNotRequestAnUpdate()
        {
            var guard = CreateGuard();
            var item = CreateEpisode("达穆尔的请求");
            var options = CreateOptions();

            guard.Capture(item, options);

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
        }

        [TestMethod]
        public void Snapshots_AreScopedToActualItemAndRefreshNotOnlyItemId()
        {
            var guard = CreateGuard();
            var item = CreateEpisode("达穆尔的请求");
            var options = CreateOptions();
            guard.Capture(item, options);
            item.Name = BookwormEmbeddedTitle;

            var anotherInstance = CreateEpisode("另一个实例");
            anotherInstance.Id = item.Id;
            Assert.AreEqual(ItemUpdateType.None, guard.Restore(anotherInstance, options));
            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, new MetadataRefreshOptions(options)));
            Assert.AreEqual(BookwormEmbeddedTitle, item.Name);

            Assert.AreEqual(ItemUpdateType.MetadataImport, guard.Restore(item, options));
            Assert.AreEqual("达穆尔的请求", item.Name);
        }

        [TestMethod]
        public void AbortedRefreshSnapshot_DoesNotLeakIntoNextRefresh()
        {
            var guard = CreateGuard();
            var item = CreateEpisode("上一次标题");
            guard.Capture(item, CreateOptions());

            item.Name = "之后手动修正的标题";
            var nextOptions = CreateOptions();
            guard.Capture(item, nextOptions);
            item.Name = BookwormEmbeddedTitle;

            Assert.AreEqual(ItemUpdateType.MetadataImport, guard.Restore(item, nextOptions));
            Assert.AreEqual("之后手动修正的标题", item.Name);
        }

        [TestMethod]
        public void RecursiveConcurrentRefresh_DoesNotMixEpisodeSnapshots()
        {
            var guard = CreateGuard();
            var sharedOptions = CreateOptions();
            var episodes = Enumerable.Range(1, 64).Select(index => CreateEpisode($"单集标题 {index}")).ToArray();

            Parallel.ForEach(episodes, item =>
            {
                var originalTitle = item.Name;
                guard.Capture(item, sharedOptions);
                item.Name = HellModeEmbeddedTitle;
                Assert.AreEqual(ItemUpdateType.MetadataImport, guard.Restore(item, sharedOptions));
                Assert.AreEqual(originalTitle, item.Name);
            });
        }

        [TestMethod]
        public void RefreshChangedToOverwriteAfterCapture_DoesNotRestoreOldTitle()
        {
            var guard = CreateGuard();
            var item = CreateEpisode("旧标题");
            var options = CreateOptions();

            guard.Capture(item, options);
            options.ReplaceAllMetadata = true;
            item.Name = "覆盖结果";

            Assert.AreEqual(ItemUpdateType.None, guard.Restore(item, options));
            Assert.AreEqual("覆盖结果", item.Name);
        }

        internal static EpisodeRefreshTitleGuard CreateGuard(
            bool embeddedTitles = true,
            bool metadataEnabled = true,
            Mock<ILibraryManager>? libraryManager = null)
        {
            libraryManager ??= new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetLibraryOptions(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>()))
                .Returns(CreateLibraryOptions(embeddedTitles, metadataEnabled));
            return new EpisodeRefreshTitleGuard(
                libraryManager.Object,
                new MetaSharkOrdinaryItemLibraryCapabilityResolver(libraryManager.Object),
                NullLogger<EpisodeRefreshTitleGuard>.Instance);
        }

        internal static LibraryOptions CreateLibraryOptions(bool embeddedTitles = true, bool metadataEnabled = true)
        {
            return new LibraryOptions
            {
                EnableEmbeddedTitles = embeddedTitles,
                TypeOptions =
                [
                    new TypeOptions
                    {
                        Type = nameof(Episode),
                        MetadataFetchers = metadataEnabled ? ["MetaShark"] : ["TheMovieDb"],
                        ImageFetchers = ["MetaShark"],
                    },
                ],
            };
        }

        internal static Episode CreateEpisode(string? name)
        {
            return new LocalEpisode
            {
                Id = Guid.NewGuid(),
                Name = name,
                Path = "/test/bookworm/Season 4/bookworm.S04E18.mp4",
                ParentIndexNumber = 4,
                IndexNumber = 18,
            };
        }

        internal static MetadataRefreshOptions CreateOptions(MetadataRefreshMode mode = MetadataRefreshMode.FullRefresh, bool replaceAll = false)
        {
            return new MetadataRefreshOptions(Mock.Of<IDirectoryService>())
            {
                MetadataRefreshMode = mode,
                ReplaceAllMetadata = replaceAll,
            };
        }

        private sealed class LocalEpisode : Episode
        {
            public override LocationType LocationType => string.IsNullOrEmpty(this.Path) ? LocationType.Virtual : LocationType.FileSystem;
        }
    }
}
