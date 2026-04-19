using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Moq.Language.Flow;

namespace Jellyfin.Plugin.MetaShark.Test.Logging
{
    internal static class LogAssert
    {
        public static void AssertLoggedOnce(
            Mock logger,
            LogLevel level,
            bool? expectException = null,
            IReadOnlyDictionary<string, object?>? stateContains = null,
            string? originalFormatContains = null,
            params string[] messageContains)
        {
            AssertLogged(logger, level, true, expectException, stateContains, originalFormatContains, messageContains);
        }

        public static void AssertLoggedAtLeastOnce(
            Mock logger,
            LogLevel level,
            bool? expectException = null,
            IReadOnlyDictionary<string, object?>? stateContains = null,
            string? originalFormatContains = null,
            params string[] messageContains)
        {
            AssertLogged(logger, level, false, expectException, stateContains, originalFormatContains, messageContains);
        }

        private static void AssertLogged(
            Mock logger,
            LogLevel level,
            bool requireExactlyOne,
            bool? expectException,
            IReadOnlyDictionary<string, object?>? stateContains,
            string? originalFormatContains,
            IReadOnlyCollection<string> messageContains)
        {
            var matches = logger.Invocations
                .Where(IsLogInvocation)
                .Select(invocation => new LogInvocation(
                    Level: (LogLevel)invocation.Arguments[0]!,
                    State: invocation.Arguments[2],
                    Exception: invocation.Arguments[3] as Exception,
                    Formatter: invocation.Arguments[4]))
                .Where(entry => Matches(entry, level, expectException, stateContains, originalFormatContains, messageContains))
                .ToList();

            if (requireExactlyOne)
            {
                Assert.AreEqual(1, matches.Count, BuildFailureMessage(level, expectException, stateContains, originalFormatContains, messageContains, matches.Count));
                return;
            }

            Assert.IsTrue(matches.Count > 0, BuildFailureMessage(level, expectException, stateContains, originalFormatContains, messageContains, matches.Count));
        }

        private static bool IsLogInvocation(Moq.IInvocation invocation)
        {
            return string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal)
                && invocation.Arguments.Count == 5;
        }

        private static bool Matches(
            LogInvocation entry,
            LogLevel level,
            bool? expectException,
            IReadOnlyDictionary<string, object?>? stateContains,
            string? originalFormatContains,
            IReadOnlyCollection<string> messageContains)
        {
            if (entry.Level != level)
            {
                return false;
            }

            if (expectException.HasValue && (entry.Exception is null) == expectException.Value)
            {
                return false;
            }

            var message = FormatMessage(entry.Formatter, entry.State, entry.Exception) ?? string.Empty;
            if (messageContains.Count > 0 && messageContains.Any(fragment => !message.Contains(fragment, StringComparison.Ordinal)))
            {
                return false;
            }

            if (stateContains is not null || originalFormatContains is not null)
            {
                if (!TryGetStructuredState(entry.State, out var state))
                {
                    return false;
                }

                if (stateContains is not null)
                {
                    foreach (var expected in stateContains)
                    {
                        var actual = state.FirstOrDefault(kvp => string.Equals(kvp.Key, expected.Key, StringComparison.Ordinal));
                        if (actual.Key is null || !Equals(actual.Value, expected.Value))
                        {
                            return false;
                        }
                    }
                }

                if (originalFormatContains is not null)
                {
                    var originalFormat = state.FirstOrDefault(kvp => string.Equals(kvp.Key, "{OriginalFormat}", StringComparison.Ordinal));
                    if (originalFormat.Key is null)
                    {
                        return false;
                    }

                    if (originalFormat.Value is not string originalFormatText || !originalFormatText.Contains(originalFormatContains, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryGetStructuredState(object? state, out IReadOnlyList<KeyValuePair<string, object?>> structuredState)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> readOnlyList)
            {
                structuredState = readOnlyList;
                return true;
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> enumerable)
            {
                structuredState = enumerable.ToList();
                return true;
            }

            structuredState = Array.Empty<KeyValuePair<string, object?>>();
            return false;
        }

        private static string? FormatMessage(object? formatter, object? state, Exception? exception)
        {
            if (formatter is Delegate delegateFormatter)
            {
                try
                {
                    return delegateFormatter.DynamicInvoke(state, exception) as string;
                }
                catch
                {
                    // Fall back to state.ToString() below.
                }
            }

            return state?.ToString();
        }

        private static string BuildFailureMessage(
            LogLevel level,
            bool? expectException,
            IReadOnlyDictionary<string, object?>? stateContains,
            string? originalFormatContains,
            IReadOnlyCollection<string> messageContains,
            int actualCount)
        {
            return $"找不到符合条件的日志。Level={level}, ExpectException={expectException?.ToString() ?? "Any"}, MessageContains=[{string.Join(", ", messageContains)}], StateContains={(stateContains is null ? "<none>" : string.Join(", ", stateContains.Select(pair => $"{pair.Key}={pair.Value}")))}, OriginalFormatContains={originalFormatContains ?? "<none>"}, MatchedCount={actualCount}.";
        }

        private readonly record struct LogInvocation(LogLevel Level, object? State, Exception? Exception, object? Formatter);
    }
}
