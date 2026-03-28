using Jellyfin.Plugin.MetaShark.ScheduledTasks;
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
            var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
            var task = new TvMissingImageRefillLibraryPostScanTask(loggerFactory.CreateLogger<TvMissingImageRefillLibraryPostScanTask>(), serviceStub.Object);

            await task.Run(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);

            serviceStub.Verify(x => x.QueueMissingImagesForFullLibraryScan(CancellationToken.None), Times.Once);
        }
    }
}
