using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test.Logging
{
    [TestClass]
    public class LogAssertTests
    {
        [TestMethod]
        public void AssertLoggedOnce_ShouldMatchMessageContainsAndNoException()
        {
            var loggerStub = new Mock<ILogger>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            loggerStub.Object.Log(
                LogLevel.Information,
                new EventId(1, "test"),
                "hello world",
                null,
                (state, exception) => state?.ToString() ?? string.Empty);

            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, messageContains: ["hello", "world"]);
        }

        [TestMethod]
        public void AssertLoggedOnce_ShouldMatchExceptionPresent()
        {
            var loggerStub = new Mock<ILogger>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var exception = new InvalidOperationException("boom");

            loggerStub.Object.Log(
                LogLevel.Warning,
                new EventId(2, "test"),
                "warning payload",
                exception,
                (state, ex) => $"{state}:{ex?.Message}");

            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Warning, expectException: true, messageContains: ["warning payload", "boom"]);
        }

        [TestMethod]
        public void AssertLoggedOnce_ShouldMatchStructuredStateAndOriginalFormat()
        {
            var loggerStub = new Mock<ILogger<LogAssertTests>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var state = new List<KeyValuePair<string, object?>>
            {
                new("ItemId", Guid.Parse("11111111-1111-1111-1111-111111111111")),
                new("ItemPath", "/library/tv/series-a"),
                new("{OriginalFormat}", "Queued item {ItemId} from {ItemPath}"),
            };

            loggerStub.Object.Log(
                LogLevel.Debug,
                new EventId(3, "test"),
                state,
                null,
                (s, ex) => string.Format(CultureInfo.InvariantCulture, "Queued item {0} from {1}", s[0].Value, s[1].Value));

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ["ItemPath"] = "/library/tv/series-a",
                },
                originalFormatContains: "Queued item {ItemId} from {ItemPath}",
                messageContains: ["Queued item", "/library/tv/series-a"]);
        }
    }
}
