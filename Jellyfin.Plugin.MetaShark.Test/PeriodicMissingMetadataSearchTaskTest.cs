using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.ScheduledTasks;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Model.Tasks;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class PeriodicMissingMetadataSearchTaskTest
    {
        [TestMethod]
        public void MetadataProperties_ShouldMatchContract()
        {
            var task = new PeriodicMissingMetadataSearchTask(Mock.Of<IMissingMetadataSearchService>());

            Assert.AreEqual("MetaSharkPeriodicMissingMetadataSearch", task.Key);
            Assert.AreEqual("定时搜索缺失元数据", task.Name);
            Assert.AreEqual("按计划扫描全库并搜索缺失元数据", task.Description);
            Assert.AreEqual(MetaSharkPlugin.PluginName, task.Category);
        }

        [TestMethod]
        public void GetDefaultTriggers_ShouldReturnSingleDailyMidnightTrigger()
        {
            var task = new PeriodicMissingMetadataSearchTask(Mock.Of<IMissingMetadataSearchService>());
            var triggers = task.GetDefaultTriggers().ToArray();

            Assert.AreEqual(1, triggers.Length);
            Assert.AreEqual(TaskTriggerInfo.TriggerDaily, triggers[0].Type);
            Assert.AreEqual(TimeSpan.FromHours(0).Ticks, triggers[0].TimeOfDayTicks);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShouldDelegateOnceAndPassThroughProgressAndToken()
        {
            var progress = new Progress<double>();
            using var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            IProgress<double>? forwardedProgress = null;
            CancellationToken forwardedToken = default;

            var serviceStub = new Mock<IMissingMetadataSearchService>(MockBehavior.Strict);
            serviceStub
                .Setup(x => x.RunFullLibrarySearchAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .Callback<IProgress<double>, CancellationToken>((receivedProgress, receivedToken) =>
                {
                    forwardedProgress = receivedProgress;
                    forwardedToken = receivedToken;
                })
                .Returns(Task.CompletedTask);

            var task = new PeriodicMissingMetadataSearchTask(serviceStub.Object);

            await task.ExecuteAsync(progress, cancellationToken).ConfigureAwait(false);

            Assert.AreSame(progress, forwardedProgress);
            Assert.AreEqual(cancellationToken, forwardedToken);
            serviceStub.Verify(x => x.RunFullLibrarySearchAsync(progress, cancellationToken), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenTokenAlreadyCanceled_PropagatesCancellation()
        {
            var progress = new Progress<double>();
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();
            var cancellationToken = cancellationTokenSource.Token;
            IProgress<double>? forwardedProgress = null;
            CancellationToken forwardedToken = default;

            var serviceStub = new Mock<IMissingMetadataSearchService>(MockBehavior.Strict);
            serviceStub
                .Setup(x => x.RunFullLibrarySearchAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .Callback<IProgress<double>, CancellationToken>((receivedProgress, receivedToken) =>
                {
                    forwardedProgress = receivedProgress;
                    forwardedToken = receivedToken;
                })
                .Returns<IProgress<double>, CancellationToken>((_, receivedToken) =>
                {
                    receivedToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });

            var task = new PeriodicMissingMetadataSearchTask(serviceStub.Object);

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
                await task.ExecuteAsync(progress, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreSame(progress, forwardedProgress);
            Assert.AreEqual(cancellationToken, forwardedToken);
            serviceStub.Verify(x => x.RunFullLibrarySearchAsync(progress, cancellationToken), Times.Once);
        }
    }
}
