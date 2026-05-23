// <copyright file="FileTvImageRefillStateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Model;
    using Microsoft.Extensions.Logging;

    public sealed class FileTvImageRefillStateStore : ITvImageRefillStateStore
    {
        private static readonly Action<ILogger, string, Exception?> LogStateLoadFailed =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(EnsureLoaded)), "[MetaShark] 电视缺图回填状态加载失败，已重置状态. path={Path}.");

        private readonly object syncRoot = new object();
        private readonly ILogger<FileTvImageRefillStateStore> logger;
        private readonly string stateFilePath;
        private Dictionary<Guid, TvImageRefillState>? states;

        public FileTvImageRefillStateStore(string stateFilePath, ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);

            this.stateFilePath = stateFilePath;
            this.logger = loggerFactory.CreateLogger<FileTvImageRefillStateStore>();
        }

        public TvImageRefillState? GetState(Guid itemId)
        {
            if (itemId == Guid.Empty)
            {
                return null;
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                return this.states != null && this.states.TryGetValue(itemId, out var state)
                    ? Clone(state)
                    : null;
            }
        }

        public void Save(TvImageRefillState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            if (state.ItemId == Guid.Empty)
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                JsonStateFile.Update(
                    this.stateFilePath,
                    this.states!,
                    states =>
                    {
                        states[state.ItemId] = Clone(state);
                        return true;
                    });
            }
        }

        public void Remove(Guid itemId)
        {
            if (itemId == Guid.Empty)
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                JsonStateFile.Update(this.stateFilePath, this.states!, states => states.Remove(itemId));
            }
        }

        private static TvImageRefillState Clone(TvImageRefillState state)
        {
            return new TvImageRefillState
            {
                ItemId = state.ItemId,
                Fingerprint = state.Fingerprint,
                Status = state.Status,
                AttemptCount = state.AttemptCount,
                LastReason = state.LastReason,
                NextRetryAtUtc = state.NextRetryAtUtc,
                UpdatedAtUtc = state.UpdatedAtUtc,
            };
        }

        private void EnsureLoaded()
        {
            if (this.states != null)
            {
                return;
            }

            this.states = JsonStateFile.LoadOrReset<TvImageRefillState>(
                this.stateFilePath,
                (path, exception) => LogStateLoadFailed(this.logger, path, exception));
        }
    }
}
