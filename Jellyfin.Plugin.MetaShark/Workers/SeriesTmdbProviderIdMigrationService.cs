// <copyright file="SeriesTmdbProviderIdMigrationService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Logging;

    public sealed class SeriesTmdbProviderIdMigrationService
    {
        private const string OfficialTmdbProviderId = nameof(MetadataProvider.Tmdb);
        private const long MaxNfoBytes = 4 * 1024 * 1024;

        private static readonly Action<ILogger, int, string, Exception?> LogScanFinished =
            LoggerMessage.Define<int, string>(LogLevel.Information, new EventId(1, nameof(MigrateFullLibraryAsync)), "[MetaShark] 剧集官方 TMDb provider id 迁移扫描完成. migratedCount={MigratedCount} trigger={Trigger}.");

        private static readonly Action<ILogger, string, Guid, string, string, Exception?> LogMigrated =
            LoggerMessage.Define<string, Guid, string, string>(LogLevel.Information, new EventId(2, nameof(MigrateItemAsync)), "[MetaShark] 已迁移剧集官方 TMDb provider id. name={Name} itemId={ItemId} itemPath={ItemPath} trigger={Trigger}.");

        private static readonly Action<ILogger, string, Guid, string, string, string, Exception?> LogNfoCleaned =
            LoggerMessage.Define<string, Guid, string, string, string>(LogLevel.Information, new EventId(3, nameof(MigrateItemAsync)), "[MetaShark] 已清理剧集官方 TMDb NFO 残留. name={Name} itemId={ItemId} itemPath={ItemPath} trigger={Trigger} nfoPath={NfoPath}.");

        private static readonly Action<ILogger, string, Guid, string, string, Exception?> LogNfoCleanupFailed =
            LoggerMessage.Define<string, Guid, string, string>(LogLevel.Warning, new EventId(4, nameof(MigrateItemAsync)), "[MetaShark] 清理剧集官方 TMDb NFO 残留失败. name={Name} itemId={ItemId} itemPath={ItemPath} trigger={Trigger}.");

        private static readonly Regex TmdbIdElementRegex = new Regex(
            "^[ \\t]*<tmdbid\\b[^>]*>.*?</tmdbid>[ \\t]*\\r?\\n?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex TmdbUniqueIdElementRegex = new Regex(
            "^[ \\t]*<uniqueid\\b(?=[^>]*\\btype\\s*=\\s*[\"']tmdb[\"'])[^>]*>.*?</uniqueid>[ \\t]*\\r?\\n?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex TmdbIdAttributeRegex = new Regex(
            "(<id\\b[^>]*?)\\s+TMDB\\s*=\\s*[\"'][^\"']*[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TmdbUrlLineRegex = new Regex(
            "^[ \\t]*(?:<[^>]+>)?\\s*https?://(?:www\\.)?(?:themoviedb\\.org|tmdb\\.org)/(?:tv|series)/[^\\r\\n<]*(?:</[^>]+>)?[ \\t]*\\r?\\n?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private readonly ILibraryManager libraryManager;
        private readonly ILogger<SeriesTmdbProviderIdMigrationService> logger;

        public SeriesTmdbProviderIdMigrationService(ILibraryManager libraryManager, ILogger<SeriesTmdbProviderIdMigrationService> logger)
        {
            this.libraryManager = libraryManager;
            this.logger = logger;
        }

        public async Task<int> MigrateFullLibraryAsync(string triggerName, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
            };

            var migratedCount = 0;
            var items = this.libraryManager.GetItemList(query).OfType<Series>().ToArray();
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await this.MigrateItemAsync(item, triggerName, cancellationToken).ConfigureAwait(false))
                {
                    migratedCount++;
                }
            }

            LogScanFinished(this.logger, migratedCount, triggerName, null);
            return migratedCount;
        }

        public async Task<bool> MigrateItemAsync(BaseItem? item, string triggerName, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);

            if (item is not Series series)
            {
                return false;
            }

            var migrated = false;
            var providerIdsMigrated = TryMigrateProviderIds(series);
            if (providerIdsMigrated)
            {
                await PersistAsync(series, cancellationToken).ConfigureAwait(false);
                LogMigrated(this.logger, series.Name ?? string.Empty, series.Id, series.Path ?? string.Empty, triggerName, null);
                migrated = true;
            }

            try
            {
                var nfoPath = TryCleanupSeriesTmdbNfoResidue(series);
                if (nfoPath != null)
                {
                    LogNfoCleaned(this.logger, series.Name ?? string.Empty, series.Id, series.Path ?? string.Empty, triggerName, nfoPath, null);
                    migrated = true;
                }
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                LogNfoCleanupFailed(this.logger, series.Name ?? string.Empty, series.Id, series.Path ?? string.Empty, triggerName, ex);
            }
#pragma warning restore CA1031

            return migrated;
        }

#pragma warning disable SA1204
        public static bool TryMigrateProviderIds(Series series)
        {
            ArgumentNullException.ThrowIfNull(series);

            if (series.ProviderIds == null)
            {
                return false;
            }

            if (!series.ProviderIds.TryGetValue(OfficialTmdbProviderId, out var officialTmdbId))
            {
                return false;
            }

            var hasValidPrivateTmdbId = TryNormalizePositiveTmdbId(series.GetProviderId(BaseProvider.MetaSharkTmdbProviderId), out _);
            var effectiveTmdbId = ResolveEffectiveTmdbId(series, officialTmdbId);
            if (!string.IsNullOrWhiteSpace(effectiveTmdbId))
            {
                if (!hasValidPrivateTmdbId)
                {
                    series.SetProviderId(BaseProvider.MetaSharkTmdbProviderId, effectiveTmdbId);
                }

                if (string.IsNullOrWhiteSpace(series.GetProviderId(MetaSharkPlugin.ProviderId))
                    && string.IsNullOrWhiteSpace(series.GetProviderId(BaseProvider.DoubanProviderId)))
                {
                    series.SetProviderId(MetaSharkPlugin.ProviderId, $"Tmdb_{effectiveTmdbId}");
                }
            }

            series.ProviderIds.Remove(OfficialTmdbProviderId);
            return true;
        }

        public static bool CanPersist(Series series)
        {
            ArgumentNullException.ThrowIfNull(series);

            var updateMethod = series.GetType().GetMethod(
                nameof(BaseItem.UpdateToRepositoryAsync),
                new[] { typeof(ItemUpdateType), typeof(CancellationToken) });

            return updateMethod?.DeclaringType != typeof(BaseItem) || BaseItem.LibraryManager != null;
        }

        public static Task PersistAsync(Series series, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(series);
            var updateReason = series.OnMetadataChanged();
            if (updateReason == 0)
            {
                updateReason = ItemUpdateType.MetadataEdit;
            }

            return series.UpdateToRepositoryAsync(updateReason, cancellationToken);
        }

        public static string? TryCleanupSeriesTmdbNfoResidue(Series series)
        {
            ArgumentNullException.ThrowIfNull(series);
            var nfoPath = GetSeriesNfoPath(series);
            if (nfoPath == null || !TryCleanupTmdbNfoResidue(nfoPath))
            {
                return null;
            }

            return nfoPath;
        }

        private static string? GetSeriesNfoPath(Series series)
        {
            if (string.IsNullOrWhiteSpace(series.Path) || !Directory.Exists(series.Path))
            {
                return null;
            }

            var seriesDirectory = new DirectoryInfo(series.Path);
            if ((seriesDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            return Path.Combine(series.Path, "tvshow.nfo");
        }

        private static bool TryCleanupTmdbNfoResidue(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var fileInfo = new FileInfo(path);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0
                || fileInfo.Length > MaxNfoBytes)
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            var encoding = reader.CurrentEncoding;
            var updated = TmdbIdElementRegex.Replace(content, string.Empty);
            updated = TmdbUniqueIdElementRegex.Replace(updated, string.Empty);
            updated = TmdbIdAttributeRegex.Replace(updated, "$1");
            updated = TmdbUrlLineRegex.Replace(updated, string.Empty);
            if (string.Equals(content, updated, StringComparison.Ordinal))
            {
                return false;
            }

            WriteAllTextAtomically(path, updated, encoding);
            return true;
        }

        private static void WriteAllTextAtomically(string path, string content, System.Text.Encoding encoding)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                File.WriteAllText(path, content, encoding);
                return;
            }

            var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempPath, content, encoding);
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static string? ResolveEffectiveTmdbId(Series series, string? officialTmdbId)
        {
            var privateTmdbId = series.GetProviderId(BaseProvider.MetaSharkTmdbProviderId);
            if (TryNormalizePositiveTmdbId(privateTmdbId, out var normalizedPrivateTmdbId))
            {
                return normalizedPrivateTmdbId;
            }

            return TryNormalizePositiveTmdbId(officialTmdbId, out var normalizedOfficialTmdbId) ? normalizedOfficialTmdbId : null;
        }

        private static bool TryNormalizePositiveTmdbId(string? value, out string tmdbId)
        {
            tmdbId = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId) || parsedId <= 0)
            {
                return false;
            }

            tmdbId = parsedId.ToString(CultureInfo.InvariantCulture);
            return true;
        }
#pragma warning restore SA1204
    }
}
