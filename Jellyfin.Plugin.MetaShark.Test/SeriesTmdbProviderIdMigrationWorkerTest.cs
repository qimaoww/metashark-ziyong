using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class SeriesTmdbProviderIdMigrationWorkerTest
    {
        [TestMethod]
        public async Task StartAsync_ShouldReturnBeforeStartupScanCompletes()
        {
            using var scanEntered = new ManualResetEventSlim();
            using var releaseScan = new ManualResetEventSlim();
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback(() => scanEntered.Set())
                .Returns(() =>
                {
                    releaseScan.Wait(TimeSpan.FromSeconds(5));
                    return new List<BaseItem>();
                });

            var worker = CreateWorker(libraryManagerStub.Object);

            var startTask = worker.StartAsync(CancellationToken.None);
            var completedTask = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);

            Assert.AreSame(startTask, completedTask);
            Assert.IsTrue(startTask.IsCompletedSuccessfully);
            Assert.IsTrue(scanEntered.Wait(TimeSpan.FromSeconds(1)), "启动扫描应在后台执行。 ");

            releaseScan.Set();
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task StopAsync_WhenStartupScanRunning_ShouldCancelBeforeMigratingItems()
        {
            using var scanEntered = new ManualResetEventSlim();
            using var releaseScan = new ManualResetEventSlim();
            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = "Series",
            };
            series.SetProviderId(MetadataProvider.Tmdb, "123456");

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback(() => scanEntered.Set())
                .Returns(() =>
                {
                    releaseScan.Wait(TimeSpan.FromSeconds(5));
                    return new List<BaseItem> { series };
                });

            var worker = CreateWorker(libraryManagerStub.Object);
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(scanEntered.Wait(TimeSpan.FromSeconds(1)), "测试前提：启动扫描必须已进入。 ");

            var stopTask = worker.StopAsync(CancellationToken.None);
            releaseScan.Set();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            Assert.AreEqual("123456", series.GetProviderId(MetadataProvider.Tmdb));
        }

        private static SeriesTmdbProviderIdMigrationWorker CreateWorker(ILibraryManager libraryManager)
        {
            var service = new SeriesTmdbProviderIdMigrationService(
                libraryManager,
                Mock.Of<ILogger<SeriesTmdbProviderIdMigrationService>>());
            return new SeriesTmdbProviderIdMigrationWorker(
                libraryManager,
                service,
                Mock.Of<ILogger<SeriesTmdbProviderIdMigrationWorker>>());
        }
    }
}
