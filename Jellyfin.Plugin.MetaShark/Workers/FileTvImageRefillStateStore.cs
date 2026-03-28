// <copyright file="FileTvImageRefillStateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using Jellyfin.Plugin.MetaShark.Model;
    using Microsoft.Extensions.Logging;

    public sealed class FileTvImageRefillStateStore : ITvImageRefillStateStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        private static readonly Action<ILogger, string, Exception?> LogStateLoadFailed =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(EnsureLoaded)), "Failed to load TV image refill state from {Path}. Resetting state.");

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

            var directory = Path.GetDirectoryName(this.stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(this.stateFilePath))
            {
                this.states = new Dictionary<Guid, TvImageRefillState>();
                return;
            }

            try
            {
                var json = File.ReadAllText(this.stateFilePath);
                this.states = JsonSerializer.Deserialize<Dictionary<Guid, TvImageRefillState>>(json, SerializerOptions)
                    ?? new Dictionary<Guid, TvImageRefillState>();
            }
            catch (IOException ex)
            {
                LogStateLoadFailed(this.logger, this.stateFilePath, ex);
                this.states = new Dictionary<Guid, TvImageRefillState>();
                this.Persist();
            }
            catch (UnauthorizedAccessException ex)
            {
                LogStateLoadFailed(this.logger, this.stateFilePath, ex);
                this.states = new Dictionary<Guid, TvImageRefillState>();
                this.Persist();
            }
            catch (JsonException ex)
            {
                LogStateLoadFailed(this.logger, this.stateFilePath, ex);
                this.states = new Dictionary<Guid, TvImageRefillState>();
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
