using Jellyfin.Plugin.MetaShark.ScheduledTasks;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Jellyfin.Plugin.MetaShark.Workers;
using System.Collections.Generic;
using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Moq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TvMissingImageRefillLibraryPostScanTaskTest
    {
        [TestMethod]
        public async Task Run_CallsSharedRefillService()
        {
            var summary = new TvMissingImageRefillScanSummary(candidateCount: 7, queuedCount: 5, skippedCount: 2, skippedReasons: null);
            var serviceStub = new Mock<ITvMissingImageRefillService>();
            serviceStub
                .Setup(x => x.QueueMissingImagesForFullLibraryScan(CancellationToken.None))
                .Returns(summary);

            var loggerStub = new Mock<ILogger<TvMissingImageRefillLibraryPostScanTask>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var task = new TvMissingImageRefillLibraryPostScanTask(loggerStub.Object, serviceStub.Object);

            await task.Run(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);

            serviceStub.Verify(x => x.QueueMissingImagesForFullLibraryScan(CancellationToken.None), Times.Once);
            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 开始电视缺图回填媒体库扫描后任务，准备排队缺图回填", messageContains: ["[MetaShark] 开始电视缺图回填媒体库扫描后任务，准备排队缺图回填"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["CandidateCount"] = summary.CandidateCount,
                    ["QueuedCount"] = summary.QueuedCount,
                    ["SkippedCount"] = summary.SkippedCount,
                    ["RefillContinuesAsync"] = true,
                },
                originalFormatContains: "[MetaShark] 电视缺图回填媒体库扫描后任务已完成排队，后台补图异步继续",
                messageContains: ["[MetaShark] 电视缺图回填媒体库扫描后任务已完成排队，后台补图异步继续", "candidateCount=7", "queuedCount=5", "skippedCount=2", "refillContinuesAsync=True"]);
            AssertNoLegacyFinishMessage(loggerStub);
        }

        private static void AssertNoLegacyFinishMessage(Mock<ILogger<TvMissingImageRefillLibraryPostScanTask>> loggerStub)
        {
            Assert.IsFalse(
                loggerStub.Invocations
                    .Where(invocation => string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal) && invocation.Arguments.Count == 5)
                    .Select(invocation => invocation.Arguments[2]?.ToString() ?? string.Empty)
                    .Any(message => message.Contains("电视缺图回填媒体库扫描后任务执行完成", StringComparison.Ordinal)),
                "发现旧的 post-scan 结尾文案仍然存在.");
        }
    }
}
