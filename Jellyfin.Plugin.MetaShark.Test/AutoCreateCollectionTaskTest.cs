using Jellyfin.Plugin.MetaShark.ScheduledTasks;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class AutoCreateCollectionTaskTest
    {
        [TestMethod]
        public async Task ExecuteAsync_WhenCollectionFeatureDisabled_LogsTaskLifecycleAndSkipMessage()
        {
            EnsureCollectionFeatureDisabled();

            var taskLoggerStub = new Mock<ILogger<AutoCreateCollectionTask>>();
            taskLoggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var boxSetLoggerStub = new Mock<ILogger<BoxSetManager>>();
            boxSetLoggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactoryStub = new Mock<ILoggerFactory>();
            loggerFactoryStub
                .Setup(x => x.CreateLogger(It.Is<string>(name => name.Contains(nameof(AutoCreateCollectionTask), StringComparison.Ordinal))))
                .Returns(taskLoggerStub.Object);
            loggerFactoryStub
                .Setup(x => x.CreateLogger(It.Is<string>(name => name.Contains(nameof(BoxSetManager), StringComparison.Ordinal))))
                .Returns(boxSetLoggerStub.Object);

            var progress = new ProgressRecorder();
            using var task = new AutoCreateCollectionTask(loggerFactoryStub.Object, Mock.Of<ILibraryManager>(), Mock.Of<ICollectionManager>());

            await task.ExecuteAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { 100d }, progress.Values.ToArray());
            LogAssert.AssertLoggedOnce(taskLoggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 开始自动创建合集扫描", messageContains: ["[MetaShark] 开始自动创建合集扫描"]);
            LogAssert.AssertLoggedOnce(taskLoggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 自动创建合集扫描执行完成", messageContains: ["[MetaShark] 自动创建合集扫描执行完成"]);
            LogAssert.AssertLoggedOnce(
                boxSetLoggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>(),
                originalFormatContains: "[MetaShark] 跳过自动创建合集扫描",
                messageContains: ["[MetaShark] 跳过自动创建合集扫描", "reason=FeatureDisabled"]);
        }

        private static void EnsureCollectionFeatureDisabled()
        {
            if (MetaSharkPlugin.Instance?.Configuration == null)
            {
                return;
            }

            MetaSharkPlugin.Instance.Configuration.EnableTmdbCollection = false;
        }

        private sealed class ProgressRecorder : IProgress<double>
        {
            public List<double> Values { get; } = new();

            public void Report(double value)
            {
                this.Values.Add(value);
            }
        }
    }
}
