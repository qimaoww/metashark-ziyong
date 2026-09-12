// <copyright file="TmdbEpisodeGroupMapping.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;

    public static class TmdbEpisodeGroupMapping
    {
        private static readonly object EffectiveMappingCacheLock = new object();
        private static string? cachedManualMapping;
        private static string? cachedLlmMapping;
        private static string? cachedEffectiveMapping;

        public static bool TryGetGroupId(string? mapping, string? tmdbSeriesId, out string groupId)
        {
            return EpisodeGroupMapParser.Shared.TryGetGroupId(mapping, tmdbSeriesId, out groupId);
        }

        public static bool TryGetGroupId(string? manualMapping, string? llmMapping, string? tmdbSeriesId, out string groupId)
        {
            if (EpisodeGroupMapParser.Shared.TryGetGroupId(manualMapping, tmdbSeriesId, out groupId))
            {
                return true;
            }

            return EpisodeGroupMapParser.Shared.TryGetGroupId(llmMapping, tmdbSeriesId, out groupId);
        }

        /// <summary>
        /// 计算「手动映射覆盖 LLM 映射」后的有效映射文本。
        /// 同一对输入会被配置刷新、LLM 辅助与 API 路径反复请求，这里做单槽缓存，
        /// 避免每次都重新解析两份映射并合并排序。
        /// </summary>
        /// <param name="manualMapping">手动映射文本。</param>
        /// <param name="llmMapping">LLM 写入的映射文本。</param>
        /// <returns>有效映射文本。</returns>
        public static string GetEffectiveMappingText(string? manualMapping, string? llmMapping)
        {
            var normalizedManual = manualMapping ?? string.Empty;
            var normalizedLlm = llmMapping ?? string.Empty;

            lock (EffectiveMappingCacheLock)
            {
                if (cachedEffectiveMapping != null
                    && string.Equals(cachedManualMapping, normalizedManual, StringComparison.Ordinal)
                    && string.Equals(cachedLlmMapping, normalizedLlm, StringComparison.Ordinal))
                {
                    return cachedEffectiveMapping;
                }
            }

            var effectiveMapping = BuildEffectiveMappingText(normalizedManual, normalizedLlm);
            lock (EffectiveMappingCacheLock)
            {
                cachedManualMapping = normalizedManual;
                cachedLlmMapping = normalizedLlm;
                cachedEffectiveMapping = effectiveMapping;
            }

            return effectiveMapping;
        }

        private static string BuildEffectiveMappingText(string manualMapping, string llmMapping)
        {
            var parser = EpisodeGroupMapParser.Shared;
            var llmSnapshot = parser.ParseSnapshot(llmMapping);
            var manualSnapshot = parser.ParseSnapshot(manualMapping);
            var entries = new Dictionary<string, string>(llmSnapshot.GroupIdsBySeriesId, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in manualSnapshot.GroupIdsBySeriesId)
            {
                entries[entry.Key] = entry.Value;
            }

            return string.Join(
                "\n",
                entries
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key}={entry.Value}"));
        }
    }
}
