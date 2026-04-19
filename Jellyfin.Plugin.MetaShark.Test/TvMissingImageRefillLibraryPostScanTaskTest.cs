using Jellyfin.Plugin.MetaShark.ScheduledTasks;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Jellyfin.Plugin.MetaShark.Workers;
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
            var serviceStub = new Mock<ITvMissingImageRefillService>();
            var loggerStub = new Mock<ILogger<TvMissingImageRefillLibraryPostScanTask>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var task = new TvMissingImageRefillLibraryPostScanTask(loggerStub.Object, serviceStub.Object);

            await task.Run(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);

            serviceStub.Verify(x => x.QueueMissingImagesForFullLibraryScan(CancellationToken.None), Times.Once);
            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 开始电视缺图回填媒体库扫描后任务", messageContains: ["[MetaShark] 开始电视缺图回填媒体库扫描后任务"]);
            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 电视缺图回填媒体库扫描后任务执行完成", messageContains: ["[MetaShark] 电视缺图回填媒体库扫描后任务执行完成"]);
        }
    }
}
