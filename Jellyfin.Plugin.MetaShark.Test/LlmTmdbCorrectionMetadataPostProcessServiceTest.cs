using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmTmdbCorrectionMetadataPostProcessServiceTest
    {
        [TestMethod]
        public async Task TryApplyAsync_MetadataDownloadWithQueuedCorrectionSnapshot_OverwritesStaleDoubanAfterJellyfinImport()
        {
            var store = new InMemoryLlmTmdbCorrectionMetadataStore();
            var item = new TrackingSeries
            {
                Id = Guid.NewGuid(),
                Name = "Re：从零开始的休息时间",
                Overview = "旧豆瓣简介",
                Path = "/dongman/动画/Re：从零开始的异世界生活 (2016)",
                ProviderIds = new Dictionary<string, string>
                {
                    [Providers.BaseProvider.DoubanProviderId] = "26862290",
                    [MetaSharkPlugin.ProviderId] = "Tmdb_65942",
                    [MetadataProvider.Tmdb.ToString()] = "65942",
                    [MetadataProvider.Imdb.ToString()] = "tt5705718",
                    [MetadataProvider.Tvdb.ToString()] = "305089",
                },
            };
            store.Save(new LlmTmdbCorrectionMetadataSnapshot
            {
                ItemId = item.Id,
                ItemPath = item.Path,
                TmdbId = "65942",
                Name = "Re：从零开始的异世界生活",
                OriginalTitle = "Re:ゼロから始める異世界生活",
                Overview = "主线简介",
                ProductionYear = 2016,
                PremiereDate = new DateTime(2016, 4, 4),
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "65942",
                    [MetaSharkPlugin.ProviderId] = "Tmdb_65942",
                    [MetadataProvider.Imdb.ToString()] = "tt5705718",
                    [MetadataProvider.Tvdb.ToString()] = "305089",
                },
            });
            var service = new LlmTmdbCorrectionMetadataPostProcessService(store, NullLogger<LlmTmdbCorrectionMetadataPostProcessService>.Instance);

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = item,
                    UpdateReason = ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload,
                },
                LlmTmdbCorrectionMetadataPostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("Re：从零开始的异世界生活", item.Name);
            Assert.AreEqual("Re:ゼロから始める異世界生活", item.OriginalTitle);
            Assert.AreEqual("主线简介", item.Overview);
            Assert.AreEqual(2016, item.ProductionYear);
            Assert.AreEqual(new DateTime(2016, 4, 4), item.PremiereDate);
            Assert.AreEqual("65942", item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("Tmdb_65942", item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("tt5705718", item.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("305089", item.GetProviderId(MetadataProvider.Tvdb));
            Assert.IsFalse(item.ProviderIds.ContainsKey(Providers.BaseProvider.DoubanProviderId));
            Assert.AreEqual(1, item.MetadataChangedCallCount);
            Assert.AreEqual(1, item.UpdateToRepositoryCallCount);
            Assert.AreEqual(ItemUpdateType.MetadataEdit, item.LastUpdateReason);
            Assert.IsNull(store.Peek(item.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_MetadataEditWithQueuedCorrectionSnapshot_DoesNotConsumeOrLoop()
        {
            var store = new InMemoryLlmTmdbCorrectionMetadataStore();
            var item = new TrackingSeries
            {
                Id = Guid.NewGuid(),
                Name = "Re：从零开始的休息时间",
                Path = "/dongman/动画/Re：从零开始的异世界生活 (2016)",
            };
            store.Save(new LlmTmdbCorrectionMetadataSnapshot
            {
                ItemId = item.Id,
                ItemPath = item.Path,
                TmdbId = "65942",
                Name = "Re：从零开始的异世界生活",
            });
            var service = new LlmTmdbCorrectionMetadataPostProcessService(store, NullLogger<LlmTmdbCorrectionMetadataPostProcessService>.Instance);

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = item,
                    UpdateReason = ItemUpdateType.MetadataEdit,
                },
                LlmTmdbCorrectionMetadataPostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("Re：从零开始的休息时间", item.Name);
            Assert.AreEqual(0, item.UpdateToRepositoryCallCount);
            Assert.IsNotNull(store.Peek(item.Id));
        }

        private sealed class TrackingSeries : Series
        {
            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public ItemUpdateType? LastUpdateReason { get; private set; }

            public override ItemUpdateType OnMetadataChanged()
            {
                this.MetadataChangedCallCount++;
                return ItemUpdateType.MetadataEdit;
            }

            public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            {
                this.UpdateToRepositoryCallCount++;
                this.LastUpdateReason = updateReason;
                return Task.CompletedTask;
            }
        }
    }
}
