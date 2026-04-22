// <copyright file="FilePersonImageRefillStateStore.cs" company="PlaceholderCompany">
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

    public sealed class FilePersonImageRefillStateStore : IPersonImageRefillStateStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        private static readonly Action<ILogger, string, Exception?> LogStateLoadFailed =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(EnsureLoaded)), "[MetaShark] 人物缺图回填状态加载失败，已重置状态. path={Path}.");

        private readonly object syncRoot = new object();
        private readonly ILogger<FilePersonImageRefillStateStore> logger;
        private readonly string stateFilePath;
        private Dictionary<Guid, PersonImageRefillState>? states;

        public FilePersonImageRefillStateStore(string stateFilePath, ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);

            this.stateFilePath = stateFilePath;
            this.logger = loggerFactory.CreateLogger<FilePersonImageRefillStateStore>();
        }

        public PersonImageRefillState? GetState(Guid personId)
        {
            if (personId == Guid.Empty)
            {
                return null;
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                return this.states != null && this.states.TryGetValue(personId, out var state)
                    ? Clone(state)
                    : null;
            }
        }

        public void Save(PersonImageRefillState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            if (state.PersonId == Guid.Empty)
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.states![state.PersonId] = Clone(state);
                this.Persist();
            }
        }

        public void Remove(Guid personId)
        {
            if (personId == Guid.Empty)
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                if (this.states!.Remove(personId))
                {
                    this.Persist();
                }
            }
        }

        private static PersonImageRefillState Clone(PersonImageRefillState state)
        {
            return new PersonImageRefillState
            {
                PersonId = state.PersonId,
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
                this.states = new Dictionary<Guid, PersonImageRefillState>();
                return;
            }

            try
            {
                var json = File.ReadAllText(this.stateFilePath);
                this.states = JsonSerializer.Deserialize<Dictionary<Guid, PersonImageRefillState>>(json, SerializerOptions)
                    ?? new Dictionary<Guid, PersonImageRefillState>();
            }
            catch (IOException ex)
            {
                LogStateLoadFailed(this.logger, this.stateFilePath, ex);
                this.states = new Dictionary<Guid, PersonImageRefillState>();
                this.Persist();
            }
            catch (UnauthorizedAccessException ex)
            {
                LogStateLoadFailed(this.logger, this.stateFilePath, ex);
                this.states = new Dictionary<Guid, PersonImageRefillState>();
                this.Persist();
            }
            catch (JsonException ex)
            {
                LogStateLoadFailed(this.logger, this.stateFilePath, ex);
                this.states = new Dictionary<Guid, PersonImageRefillState>();
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
