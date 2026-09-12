// <copyright file="TmdbEpisodeGroupMapPersistenceService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Configuration;

    public sealed class TmdbEpisodeGroupMapPersistenceService : ITmdbEpisodeGroupMapPersistenceService
    {
        private readonly object syncRoot = new object();
        private readonly EpisodeGroupMapParser parser;
        private readonly bool saveLlmMapping;

        public TmdbEpisodeGroupMapPersistenceService(EpisodeGroupMapParser? parser = null, bool saveLlmMapping = false)
        {
            this.parser = parser ?? EpisodeGroupMapParser.Shared;
            this.saveLlmMapping = saveLlmMapping;
        }

        public Task<TmdbEpisodeGroupMapPersistenceResult> TrySaveAsync(string? expectedOldMapping, string? newMapping, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plugin = MetaSharkPlugin.Instance;
            if (plugin?.Configuration == null)
            {
                return Task.FromResult(TmdbEpisodeGroupMapPersistenceResult.Failed(
                    "PluginConfigurationUnavailable",
                    string.Empty,
                    NormalizeMapping(newMapping),
                    null));
            }

            lock (this.syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // CAS 与落盘必须在插件配置保存锁内基于同一份「最新配置」完成，
                // 否则并发保存（配置页整体替换配置对象）会被本服务的旧快照覆盖。
                TmdbEpisodeGroupMapPersistenceResult? earlyResult = null;
                var currentCanonicalText = string.Empty;
                var newCanonicalText = NormalizeMapping(newMapping);

                var saved = plugin.TryUpdateConfigurationSafely(
                    currentConfiguration =>
                    {
                        var currentSnapshot = this.parser.ParseSnapshot(this.saveLlmMapping ? currentConfiguration.LlmTmdbEpisodeGroupMap : currentConfiguration.TmdbEpisodeGroupMap);
                        var expectedSnapshot = this.parser.ParseSnapshot(expectedOldMapping);
                        var newSnapshot = this.parser.ParseSnapshot(newMapping);
                        currentCanonicalText = currentSnapshot.CanonicalText;
                        newCanonicalText = newSnapshot.CanonicalText;

                        if (!string.Equals(currentSnapshot.CanonicalText, expectedSnapshot.CanonicalText, StringComparison.Ordinal))
                        {
                            earlyResult = TmdbEpisodeGroupMapPersistenceResult.Conflict(expectedSnapshot.CanonicalText, currentSnapshot.CanonicalText);
                            return null;
                        }

                        if (string.Equals(currentSnapshot.CanonicalText, newSnapshot.CanonicalText, StringComparison.Ordinal))
                        {
                            earlyResult = TmdbEpisodeGroupMapPersistenceResult.NoChange(currentSnapshot.CanonicalText);
                            return null;
                        }

                        var updatedConfiguration = MetaSharkPlugin.CloneConfiguration(currentConfiguration);
                        if (this.saveLlmMapping)
                        {
                            updatedConfiguration.LlmTmdbEpisodeGroupMap = newSnapshot.CanonicalText;
                        }
                        else
                        {
                            updatedConfiguration.TmdbEpisodeGroupMap = newSnapshot.CanonicalText;
                        }

                        return updatedConfiguration;
                    },
                    out var saveException);

                if (earlyResult != null)
                {
                    return Task.FromResult(earlyResult);
                }

                if (saved)
                {
                    return Task.FromResult(TmdbEpisodeGroupMapPersistenceResult.SavedResult(currentCanonicalText, newCanonicalText));
                }

                return Task.FromResult(TmdbEpisodeGroupMapPersistenceResult.Failed(
                    "SaveConfigurationFailed",
                    currentCanonicalText,
                    currentCanonicalText,
                    saveException));
            }
        }

        private static string NormalizeMapping(string? mapping)
        {
            return string.IsNullOrWhiteSpace(mapping) ? string.Empty : mapping.Trim();
        }
    }
}
