// <copyright file="JsonStateFile.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
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
                // IO/权限问题通常是瞬时故障，原文件可能仍然完好，不能直接覆盖为空状态。
                logLoadFailed(path, ex);
                return new Dictionary<Guid, TState>();
            }
            catch (UnauthorizedAccessException ex)
            {
                logLoadFailed(path, ex);
                return new Dictionary<Guid, TState>();
            }
            catch (JsonException ex)
            {
                // 确认文件内容已损坏时才重写为空状态。
                return Reset<TState>(path, logLoadFailed, ex);
            }
        }

        public static void Write<TState>(string path, Dictionary<Guid, TState>? states)
        {
            var json = JsonSerializer.Serialize(states, SerializerOptions);
            var tempPath = CreateTempPath(path);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                TryDeleteTempFile(tempPath);
                throw;
            }
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

        private static string CreateTempPath(string path)
        {
            return string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        }

        private static void TryDeleteTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
