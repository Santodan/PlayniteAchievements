using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.EmuLibrary;
using ProtoBuf;
using System;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class EmuLibraryPathResolverTests
    {
        [TestMethod]
        public void TryResolveSourcePath_MultiFileGame_ReturnsSourceBaseDirectory()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var extensionsDataPath = Path.Combine(tempRoot, "ExtensionsData");
                var sourceRoot = Path.Combine(tempRoot, "roms", "PS4");
                var mappingId = Guid.NewGuid();
                WriteConfig(extensionsDataPath, mappingId, sourceRoot);

                var game = BuildEmuLibraryGame(new EmuLibraryMultiFileGameInfo
                {
                    MappingId = mappingId,
                    SourceBaseDir = "CUSA22222",
                    SourceFilePath = @"CUSA22222\eboot.bin"
                });

                var resolved = EmuLibraryPathResolver.TryResolveSourcePath(
                    extensionsDataPath,
                    applicationPath: null,
                    game,
                    out var path);

                Assert.IsTrue(resolved);
                Assert.AreEqual(Path.Combine(sourceRoot, "CUSA22222"), path);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [TestMethod]
        public void TryResolveSourceFilePath_MultiFileGame_ReturnsPrimaryFile()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var extensionsDataPath = Path.Combine(tempRoot, "ExtensionsData");
                var sourceRoot = Path.Combine(tempRoot, "roms", "PS3");
                var mappingId = Guid.NewGuid();
                WriteConfig(extensionsDataPath, mappingId, sourceRoot);

                var game = BuildEmuLibraryGame(new EmuLibraryMultiFileGameInfo
                {
                    MappingId = mappingId,
                    SourceBaseDir = "Sly Collection",
                    SourceFilePath = @"Sly Collection\PS3_GAME\USRDIR\EBOOT.BIN"
                });

                var resolved = EmuLibraryPathResolver.TryResolveSourceFilePath(
                    extensionsDataPath,
                    applicationPath: null,
                    game,
                    out var filePath);

                Assert.IsTrue(resolved);
                Assert.AreEqual(
                    Path.Combine(sourceRoot, @"Sly Collection\PS3_GAME\USRDIR\EBOOT.BIN"),
                    filePath);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [TestMethod]
        public void TryResolveSourcePath_SingleFileGame_ReturnsSourceFile()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var extensionsDataPath = Path.Combine(tempRoot, "ExtensionsData");
                var sourceRoot = Path.Combine(tempRoot, "roms", "Xbox360");
                var mappingId = Guid.NewGuid();
                WriteConfig(extensionsDataPath, mappingId, sourceRoot);

                var game = BuildEmuLibraryGame(new EmuLibrarySingleFileGameInfo
                {
                    MappingId = mappingId,
                    SourcePath = "Halo 3.iso"
                });

                var resolved = EmuLibraryPathResolver.TryResolveSourcePath(
                    extensionsDataPath,
                    applicationPath: null,
                    game,
                    out var path);

                Assert.IsTrue(resolved);
                Assert.AreEqual(Path.Combine(sourceRoot, "Halo 3.iso"), path);

                // Single-file games have no source directory.
                Assert.IsFalse(EmuLibraryPathResolver.TryResolveSourceDirectory(
                    extensionsDataPath,
                    applicationPath: null,
                    game,
                    out _));
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [TestMethod]
        public void TryResolveSourcePath_MappingMissing_ReturnsFalse()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var extensionsDataPath = Path.Combine(tempRoot, "ExtensionsData");
                WriteConfig(extensionsDataPath, Guid.NewGuid(), Path.Combine(tempRoot, "roms"));

                var game = BuildEmuLibraryGame(new EmuLibrarySingleFileGameInfo
                {
                    MappingId = Guid.NewGuid(),
                    SourcePath = "Halo 3.iso"
                });

                Assert.IsFalse(EmuLibraryPathResolver.TryResolveSourcePath(
                    extensionsDataPath,
                    applicationPath: null,
                    game,
                    out _));
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [TestMethod]
        public void TryResolveSourcePath_NonEmuLibraryGame_ReturnsFalse()
        {
            var game = new Game
            {
                PluginId = Guid.NewGuid(),
                GameId = "not-an-emulibrary-id"
            };

            Assert.IsFalse(EmuLibraryPathResolver.TryResolveSourcePath(
                extensionsDataPath: null,
                applicationPath: null,
                game,
                out _));
        }

        internal static Game BuildEmuLibraryGame(EmuLibraryGameInfoBase gameInfo)
        {
            using (var memoryStream = new MemoryStream())
            {
                Serializer.Serialize(memoryStream, gameInfo);
                return new Game
                {
                    PluginId = EmuLibraryGameIdDecoder.EmuLibraryPluginId,
                    GameId = "!0" + Convert.ToBase64String(memoryStream.ToArray())
                };
            }
        }

        internal static void WriteConfig(string extensionsDataPath, Guid mappingId, string sourceRoot)
        {
            var pluginDataPath = Path.Combine(extensionsDataPath, EmuLibraryGameIdDecoder.EmuLibraryPluginId.ToString());
            Directory.CreateDirectory(pluginDataPath);

            var json = JsonConvert.SerializeObject(new
            {
                Mappings = new[]
                {
                    new { MappingId = mappingId, SourcePath = sourceRoot }
                }
            }, Formatting.Indented);

            File.WriteAllText(Path.Combine(pluginDataPath, "config.json"), json, Encoding.UTF8);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayAch_EmuLibPathTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectoryIfExists(string directoryPath)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
