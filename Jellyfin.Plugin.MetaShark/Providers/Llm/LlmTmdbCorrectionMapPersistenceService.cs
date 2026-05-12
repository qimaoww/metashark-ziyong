// <copyright file="LlmTmdbCorrectionMapPersistenceService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Configuration;

    public sealed class LlmTmdbCorrectionMapPersistenceService : ILlmTmdbCorrectionMapPersistenceService
    {
        private readonly object syncRoot = new object();
        private readonly LlmTmdbCorrectionMapParser parser;

        public LlmTmdbCorrectionMapPersistenceService(LlmTmdbCorrectionMapParser? parser = null)
        {
            this.parser = parser ?? LlmTmdbCorrectionMapParser.Shared;
        }

        public Task<LlmTmdbCorrectionMapPersistenceResult> TryUpsertDoubanCorrectionAsync(string mediaType, string doubanId, string tmdbId, CancellationToken cancellationToken)
        {
            return this.TryUpsertDoubanMapAsync(
                mediaType,
                doubanId,
                tmdbId,
                static configuration => configuration.EnableLlmTmdbCorrectionPersistence,
                static configuration => configuration.LlmTmdbCorrectionMap,
                static (configuration, mapping) => configuration.LlmTmdbCorrectionMap = mapping,
                "LlmTmdbCorrectionPersistenceDisabled",
                cancellationToken);
        }

        public Task<LlmTmdbCorrectionMapPersistenceResult> TryUpsertDoubanCompletionAsync(string mediaType, string doubanId, string tmdbId, CancellationToken cancellationToken)
        {
            return this.TryUpsertDoubanMapAsync(
                mediaType,
                doubanId,
                tmdbId,
                static configuration => configuration.EnableLlmTmdbCompletionPersistence,
                static configuration => configuration.LlmTmdbCompletionMap,
                static (configuration, mapping) => configuration.LlmTmdbCompletionMap = mapping,
                "LlmTmdbCompletionPersistenceDisabled",
                cancellationToken);
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

        private Task<LlmTmdbCorrectionMapPersistenceResult> TryUpsertDoubanMapAsync(
            string mediaType,
            string doubanId,
            string tmdbId,
            Func<PluginConfiguration, bool> isEnabled,
            Func<PluginConfiguration, string?> getMapping,
            Action<PluginConfiguration, string> setMapping,
            string disabledReason,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plugin = MetaSharkPlugin.Instance;
            if (plugin?.Configuration == null)
            {
                return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.Failed("PluginConfigurationUnavailable", string.Empty, string.Empty, null));
            }

            lock (this.syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentConfiguration = plugin.Configuration;
                if (!isEnabled(currentConfiguration))
                {
                    var disabledMapping = this.parser.GetCanonicalText(getMapping(currentConfiguration));
                    return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.Failed(disabledReason, disabledMapping, disabledMapping, null));
                }

                var currentSnapshot = this.parser.ParseSnapshot(getMapping(currentConfiguration));
                var newMapping = this.parser.UpsertDoubanCorrection(currentSnapshot.CanonicalText, mediaType, doubanId, tmdbId);
                var newSnapshot = this.parser.ParseSnapshot(newMapping);
                if (string.Equals(currentSnapshot.CanonicalText, newSnapshot.CanonicalText, StringComparison.Ordinal))
                {
                    return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.NoChange(currentSnapshot.CanonicalText));
                }

                var previousConfiguration = CloneConfiguration(currentConfiguration);
                var updatedConfiguration = CloneConfiguration(currentConfiguration);
                setMapping(updatedConfiguration, newSnapshot.CanonicalText);
                if (plugin.TrySaveConfigurationSafely(updatedConfiguration, previousConfiguration, out var saveException))
                {
                    return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.SavedResult(currentSnapshot.CanonicalText, newSnapshot.CanonicalText));
                }

                return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.Failed("SaveConfigurationFailed", currentSnapshot.CanonicalText, currentSnapshot.CanonicalText, saveException));
            }
        }
    }
}
