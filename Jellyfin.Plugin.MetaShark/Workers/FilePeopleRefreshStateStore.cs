// <copyright file="FilePeopleRefreshStateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using Jellyfin.Plugin.MetaShark.Core;
    using Microsoft.Extensions.Logging;

    public sealed class FilePeopleRefreshStateStore : IPeopleRefreshStateStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        private static readonly Action<ILogger, string, Exception?> LogStateLoadFailed =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(EnsureLoaded)), "[MetaShark] 人物刷新状态加载失败，已重置状态. path={Path}.");

        private readonly object syncRoot = new object();
        private readonly ILogger<FilePeopleRefreshStateStore> logger;
        private readonly string stateFilePath;
        private Dictionary<Guid, PeopleRefreshState>? states;

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
                this.states![state.ItemId] = Clone(state);
                this.Persist();
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
                if (this.states!.Remove(itemId))
                {
                    this.Persist();
                }
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
                UpdatedAtUtc = state.UpdatedAtUtc,
            };
        }

        private void EnsureLoaded()
        {
            if (this.states != null)
            {
                return;
            }

            var directory = Path.GetDirectoryName(this.stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(this.stateFilePath))
            {
                this.states = new Dictionary<Guid, PeopleRefreshState>();
                return;
            }

            try
            {
                var json = File.ReadAllText(this.stateFilePath);
                this.states = JsonSerializer.Deserialize<Dictionary<Guid, PeopleRefreshState>>(json, SerializerOptions)
                    ?? new Dictionary<Guid, PeopleRefreshState>();
            }
            catch (IOException ex)
            {
                LogStateLoadFailed(this.logger, this.stateFilePath, ex);
                this.states = new Dictionary<Guid, PeopleRefreshState>();
                this.Persist();
            }
            catch (UnauthorizedAccessException ex)
            {
                LogStateLoadFailed(this.logger, this.stateFilePath, ex);
                this.states = new Dictionary<Guid, PeopleRefreshState>();
                this.Persist();
            }
            catch (JsonException ex)
            {
                LogStateLoadFailed(this.logger, this.stateFilePath, ex);
                this.states = new Dictionary<Guid, PeopleRefreshState>();
                this.Persist();
            }
        }

        private void Persist()
        {
            var json = JsonSerializer.Serialize(this.states, SerializerOptions);
            File.WriteAllText(this.stateFilePath, json);
        }
    }
}
