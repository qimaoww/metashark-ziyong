using Jellyfin.Plugin.MetaShark.Test.Logging;
using Jellyfin.Plugin.MetaShark.Workers;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class FileTvImageRefillStateStoreTest
    {
        [TestMethod]
        public void GetState_WhenStateFileIsInvalid_LogsUnifiedWarningAndResetsState()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-tv-image-refill-state-{Guid.NewGuid():N}");
            var stateFilePath = Path.Combine(tempRoot, "state", "tv-image-refill-state.json");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath)!);
                File.WriteAllText(stateFilePath, "{ invalid json");

                var loggerStub = new Mock<ILogger<FileTvImageRefillStateStore>>();
                loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

                var loggerFactoryStub = new Mock<ILoggerFactory>();
                loggerFactoryStub.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(loggerStub.Object);

                var store = new FileTvImageRefillStateStore(stateFilePath, loggerFactoryStub.Object);

                var state = store.GetState(Guid.NewGuid());

                Assert.IsNull(state);
                Assert.IsTrue(File.Exists(stateFilePath), "无效状态文件应被重置并重新写回空状态。");
                Assert.AreEqual("{}", File.ReadAllText(stateFilePath).Trim());
                LogAssert.AssertLoggedOnce(
                    loggerStub,
                    LogLevel.Warning,
                    expectException: true,
                    stateContains: new Dictionary<string, object?>
                    {
                        ["Path"] = stateFilePath,
                    },
                    originalFormatContains: "[MetaShark] 电视缺图回填状态加载失败，已重置状态",
                    messageContains: ["[MetaShark] 电视缺图回填状态加载失败", "已重置状态", $"path={stateFilePath}"]);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }
    }
}
