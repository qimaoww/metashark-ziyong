// <copyright file="TmdbEpisodeGroupMapPersistenceService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Configuration;

    public sealed class TmdbEpisodeGroupMapPersistenceService : ITmdbEpisodeGroupMapPersistenceService
    {
        private readonly object syncRoot = new object();
        private readonly EpisodeGroupMapParser parser;

        public TmdbEpisodeGroupMapPersistenceService(EpisodeGroupMapParser? parser = null)
        {
            this.parser = parser ?? EpisodeGroupMapParser.Shared;
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

                var currentConfiguration = plugin.Configuration;
                var currentSnapshot = this.parser.ParseSnapshot(currentConfiguration.TmdbEpisodeGroupMap);
                var expectedSnapshot = this.parser.ParseSnapshot(expectedOldMapping);
                var newSnapshot = this.parser.ParseSnapshot(newMapping);

                if (!string.Equals(currentSnapshot.CanonicalText, expectedSnapshot.CanonicalText, StringComparison.Ordinal))
                {
                    return Task.FromResult(TmdbEpisodeGroupMapPersistenceResult.Conflict(expectedSnapshot.CanonicalText, currentSnapshot.CanonicalText));
                }

                if (string.Equals(currentSnapshot.CanonicalText, newSnapshot.CanonicalText, StringComparison.Ordinal))
                {
                    return Task.FromResult(TmdbEpisodeGroupMapPersistenceResult.NoChange(currentSnapshot.CanonicalText));
                }

                var previousConfiguration = CloneConfiguration(currentConfiguration);
                var updatedConfiguration = CloneConfiguration(currentConfiguration);
                updatedConfiguration.TmdbEpisodeGroupMap = newSnapshot.CanonicalText;

                if (plugin.TrySaveConfigurationSafely(updatedConfiguration, previousConfiguration, out var saveException))
                {
                    return Task.FromResult(TmdbEpisodeGroupMapPersistenceResult.SavedResult(currentSnapshot.CanonicalText, newSnapshot.CanonicalText));
                }

                return Task.FromResult(TmdbEpisodeGroupMapPersistenceResult.Failed(
                    "SaveConfigurationFailed",
                    currentSnapshot.CanonicalText,
                    currentSnapshot.CanonicalText,
                    saveException));
            }
        }

        private static string NormalizeMapping(string? mapping)
        {
            return string.IsNullOrWhiteSpace(mapping) ? string.Empty : mapping.Trim();
        }

        private static PluginConfiguration CloneConfiguration(PluginConfiguration source)
        {
            ArgumentNullException.ThrowIfNull(source);

            var clone = new PluginConfiguration();
            foreach (var property in typeof(PluginConfiguration)
                         .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(static property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0))
            {
                property.SetValue(clone, property.GetValue(source));
            }

            return clone;
        }
    }
}
