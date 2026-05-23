using Jellyfin.Plugin.MetaShark.Workers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class JsonStateFileAtomicWriteTest
    {
        [TestMethod]
        public void Write_WhenTargetIsMissing_CreatesValidJsonWithoutTempResidue()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-json-state-create-{Guid.NewGuid():N}");
            var stateFilePath = Path.Combine(tempRoot, "state.json");
            var itemId = Guid.NewGuid();

            try
            {
                Directory.CreateDirectory(tempRoot);

                JsonStateFile.Write(stateFilePath, new Dictionary<Guid, string>
                {
                    [itemId] = "created",
                });

                var states = JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(stateFilePath));

                Assert.IsNotNull(states);
                Assert.AreEqual("created", states![itemId]);
                AssertDirectoryContainsOnly(tempRoot, "state.json");
            }
            finally
            {
                DeleteTempRoot(tempRoot);
            }
        }

        [TestMethod]
        public void Write_WhenTargetExists_ReplacesFileAtomicallyForExistingReaders()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-json-state-atomic-{Guid.NewGuid():N}");
            var stateFilePath = Path.Combine(tempRoot, "state.json");
            var itemId = Guid.NewGuid();

            try
            {
                Directory.CreateDirectory(tempRoot);
                JsonStateFile.Write(stateFilePath, new Dictionary<Guid, string>
                {
                    [itemId] = "before",
                });
                var initialJson = File.ReadAllText(stateFilePath);

                using var heldReader = new FileStream(
                    stateFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                JsonStateFile.Write(stateFilePath, new Dictionary<Guid, string>
                {
                    [itemId] = "after",
                });

                heldReader.Position = 0;
                using var streamReader = new StreamReader(heldReader, leaveOpen: true);
                var heldReaderJson = streamReader.ReadToEnd();
                var freshReaderJson = File.ReadAllText(stateFilePath);
                var freshStates = JsonSerializer.Deserialize<Dictionary<Guid, string>>(freshReaderJson);

                Assert.AreEqual(initialJson, heldReaderJson, "An existing reader should keep seeing the pre-replace file.");
                Assert.AreNotEqual(initialJson, freshReaderJson, "A fresh reader should see the replacement file.");
                Assert.IsNotNull(freshStates);
                Assert.AreEqual("after", freshStates![itemId]);
                AssertDirectoryContainsOnly(tempRoot, "state.json");
            }
            finally
            {
                DeleteTempRoot(tempRoot);
            }
        }

        [TestMethod]
        public void Write_WhenReplaceFails_CleansTempAndThrows()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-json-state-fail-{Guid.NewGuid():N}");
            var stateFilePath = Path.Combine(tempRoot, "state.json");

            try
            {
                Directory.CreateDirectory(stateFilePath);

                Exception? writeException = null;
                try
                {
                    JsonStateFile.Write(stateFilePath, new Dictionary<Guid, string>
                    {
                        [Guid.NewGuid()] = "blocked",
                    });
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    writeException = ex;
                }

                Assert.IsNotNull(writeException, "The write failure should be surfaced to the caller.");
                AssertDirectoryContainsOnly(tempRoot, "state.json");
            }
            finally
            {
                DeleteTempRoot(tempRoot);
            }
        }

        [TestMethod]
        public void LoadOrReset_WhenFileIsCorrupt_RewritesEmptyStateWithoutTempResidue()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-json-state-reset-{Guid.NewGuid():N}");
            var stateFilePath = Path.Combine(tempRoot, "state.json");

            try
            {
                Directory.CreateDirectory(tempRoot);
                File.WriteAllText(stateFilePath, "{ invalid json");

                var states = JsonStateFile.LoadOrReset<string>(
                    stateFilePath,
                    static (_, _) => { });

                Assert.AreEqual(0, states.Count);
                Assert.AreEqual("{}", File.ReadAllText(stateFilePath).Trim());
                AssertDirectoryContainsOnly(tempRoot, "state.json");
            }
            finally
            {
                DeleteTempRoot(tempRoot);
            }
        }

        private static void AssertDirectoryContainsOnly(string directory, string expectedEntryName)
        {
            var entries = Directory.GetFileSystemEntries(directory);
            Assert.AreEqual(1, entries.Length, "JsonStateFile should not leave temp files behind.");
            Assert.AreEqual(expectedEntryName, Path.GetFileName(entries[0]));
        }

        private static void DeleteTempRoot(string tempRoot)
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
