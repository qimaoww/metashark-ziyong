// <copyright file="FilePeopleRefreshStateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Jellyfin.Plugin.MetaShark.Core;
    using Microsoft.Extensions.Logging;

    public sealed class FilePeopleRefreshStateStore : IPeopleRefreshStateStore
    {
        private static readonly Action<ILogger, string, Exception?> LogStateLoadFailed =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(EnsureLoaded)), "[MetaShark] 人物刷新状态加载失败，已重置状态. path={Path}.");

        private static readonly TimeSpan StateLifetime = TimeSpan.FromDays(180);

        private readonly object syncRoot = new object();
        private readonly ILogger<FilePeopleRefreshStateStore> logger;
        private readonly string stateFilePath;
        private Dictionary<Guid, PeopleRefreshState>? states;
        private DateTimeOffset nextSweepAtUtc;

        public FilePeopleRefreshStateStore(string stateFilePath, ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);

            this.stateFilePath = stateFilePath;
            this.logger = loggerFactory.CreateLogger<FilePeopleRefreshStateStore>();
        }

        public PeopleRefreshState? GetState(Guid itemId)
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

        public void Save(PeopleRefreshState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            if (state.ItemId == Guid.Empty)
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
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

        private static PeopleRefreshState Clone(PeopleRefreshState state)
        {
            return new PeopleRefreshState
            {
                ItemId = state.ItemId,
                ItemType = state.ItemType,
                TmdbId = state.TmdbId,
                Version = state.Version,
                AuthoritativePeopleSnapshot = state.AuthoritativePeopleSnapshot?.Clone(),
                UpdatedAtUtc = state.UpdatedAtUtc,
            };
        }

        /// <summary>
        /// 已删除条目或历史版本的状态不会有人再读取，按年龄摊还裁剪，避免状态文件无限增长。
        /// </summary>
        private void RemoveExpiredEntries(DateTimeOffset nowUtc)
        {
            if (nowUtc < this.nextSweepAtUtc)
            {
                return;
            }

            this.nextSweepAtUtc = nowUtc.AddHours(1);

            if (this.states == null || this.states.Count == 0)
            {
                return;
            }

            var expiredIds = this.states
                .Where(pair => pair.Value.UpdatedAtUtc != default && pair.Value.UpdatedAtUtc < nowUtc - StateLifetime)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var expiredId in expiredIds)
            {
                this.states.Remove(expiredId);
            }
        }

        private void EnsureLoaded()
        {
            if (this.states != null)
            {
                return;
            }

            this.states = JsonStateFile.LoadOrReset<PeopleRefreshState>(
                this.stateFilePath,
                (path, exception) => LogStateLoadFailed(this.logger, path, exception));
        }
    }
}
