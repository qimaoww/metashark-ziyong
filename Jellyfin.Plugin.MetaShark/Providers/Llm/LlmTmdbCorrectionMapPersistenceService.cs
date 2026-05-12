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
                if (!currentConfiguration.EnableLlmTmdbCorrectionPersistence)
                {
                    var disabledMapping = this.parser.GetCanonicalText(currentConfiguration.LlmTmdbCorrectionMap);
                    return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.Failed("LlmTmdbCorrectionPersistenceDisabled", disabledMapping, disabledMapping, null));
                }

                var currentSnapshot = this.parser.ParseSnapshot(currentConfiguration.LlmTmdbCorrectionMap);
                var newMapping = this.parser.UpsertDoubanCorrection(currentSnapshot.CanonicalText, mediaType, doubanId, tmdbId);
                var newSnapshot = this.parser.ParseSnapshot(newMapping);
                if (string.Equals(currentSnapshot.CanonicalText, newSnapshot.CanonicalText, StringComparison.Ordinal))
                {
                    return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.NoChange(currentSnapshot.CanonicalText));
                }

                var previousConfiguration = CloneConfiguration(currentConfiguration);
                var updatedConfiguration = CloneConfiguration(currentConfiguration);
                updatedConfiguration.LlmTmdbCorrectionMap = newSnapshot.CanonicalText;
                if (plugin.TrySaveConfigurationSafely(updatedConfiguration, previousConfiguration, out var saveException))
                {
                    return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.SavedResult(currentSnapshot.CanonicalText, newSnapshot.CanonicalText));
                }

                return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.Failed("SaveConfigurationFailed", currentSnapshot.CanonicalText, currentSnapshot.CanonicalText, saveException));
            }
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
