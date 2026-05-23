// <copyright file="JsonStateFile.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    internal static class JsonStateFile
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        public static Dictionary<Guid, TState> LoadOrReset<TState>(
            string path,
            Action<string, Exception?> logLoadFailed)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(path))
            {
                return new Dictionary<Guid, TState>();
            }

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<Guid, TState>>(json, SerializerOptions)
                    ?? new Dictionary<Guid, TState>();
            }
            catch (IOException ex)
            {
                return Reset<TState>(path, logLoadFailed, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Reset<TState>(path, logLoadFailed, ex);
            }
            catch (JsonException ex)
            {
                return Reset<TState>(path, logLoadFailed, ex);
            }
        }

        public static void Write<TState>(string path, Dictionary<Guid, TState>? states)
        {
            var json = JsonSerializer.Serialize(states, SerializerOptions);
            File.WriteAllText(path, json);
        }

        public static void Update<TState>(
            string path,
            Dictionary<Guid, TState> states,
            Func<Dictionary<Guid, TState>, bool> update)
        {
            ArgumentNullException.ThrowIfNull(states);
            ArgumentNullException.ThrowIfNull(update);

            if (update(states))
            {
                Write(path, states);
            }
        }

        private static Dictionary<Guid, TState> Reset<TState>(
            string path,
            Action<string, Exception?> logLoadFailed,
            Exception exception)
        {
            logLoadFailed(path, exception);
            var states = new Dictionary<Guid, TState>();
            Write(path, states);
            return states;
        }
    }
}
