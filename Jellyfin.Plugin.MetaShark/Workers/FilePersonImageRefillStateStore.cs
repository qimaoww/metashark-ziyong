// <copyright file="FilePersonImageRefillStateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Model;
    using Microsoft.Extensions.Logging;

    public sealed class FilePersonImageRefillStateStore : IPersonImageRefillStateStore
    {
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
                JsonStateFile.Update(
                    this.stateFilePath,
                    this.states!,
                    states =>
                    {
                        states[state.PersonId] = Clone(state);
                        return true;
                    });
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
                JsonStateFile.Update(this.stateFilePath, this.states!, states => states.Remove(personId));
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

            this.states = JsonStateFile.LoadOrReset<PersonImageRefillState>(
                this.stateFilePath,
                (path, exception) => LogStateLoadFailed(this.logger, path, exception));
        }
    }
}
