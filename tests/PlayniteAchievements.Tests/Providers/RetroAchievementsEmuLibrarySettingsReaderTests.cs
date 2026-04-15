using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Providers.RetroAchievements;
using System;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class RetroAchievementsEmuLibrarySettingsReaderTests
    {
        [TestMethod]
        public void TryResolveSourceRoot_ConfigContainsMapping_ReturnsPath()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var extensionsDataPath = Path.Combine(tempRoot, "ExtensionsData");
                var expectedSourceRoot = Path.Combine(tempRoot, "roms");
                var mappingId = Guid.NewGuid();

                WriteConfig(extensionsDataPath, new
                {
                    Mappings = new[]
                    {
                        new { MappingId = mappingId, SourcePath = expectedSourceRoot }
                    }
                });

                var resolved = EmuLibrarySettingsReader.TryResolveSourceRoot(
                    extensionsDataPath,
                    playniteApplicationPath: null,
                    mappingId,
                    out var sourceRoot);

                Assert.IsTrue(resolved);
                Assert.AreEqual(expectedSourceRoot, sourceRoot);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [TestMethod]
        public void TryResolveSourceRoot_ConfigMissing_ReturnsFalse()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var resolved = EmuLibrarySettingsReader.TryResolveSourceRoot(
                    Path.Combine(tempRoot, "ExtensionsData"),
                    playniteApplicationPath: null,
                    Guid.NewGuid(),
                    out _);

                Assert.IsFalse(resolved);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [TestMethod]
        public void TryResolveSourceRoot_PlayniteVariableInPath_IsExpanded()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var extensionsDataPath = Path.Combine(tempRoot, "ExtensionsData");
                var applicationPath = Path.Combine(tempRoot, "Playnite");
                var mappingId = Guid.NewGuid();
                var sourcePath = Path.Combine(ExpandableVariables.PlayniteDirectory, "roms");

                WriteConfig(extensionsDataPath, new
                {
                    Mappings = new[]
                    {
                        new { MappingId = mappingId, SourcePath = sourcePath }
                    }
                });

                var resolved = EmuLibrarySettingsReader.TryResolveSourceRoot(
                    extensionsDataPath,
                    applicationPath,
                    mappingId,
                    out var sourceRoot);

                Assert.IsTrue(resolved);
                Assert.AreEqual(Path.Combine(applicationPath, "roms"), sourceRoot);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [TestMethod]
        public void TryResolveSourceRoot_InvalidConfigJson_ReturnsFalse()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var extensionsDataPath = Path.Combine(tempRoot, "ExtensionsData");
                var pluginDataPath = Path.Combine(extensionsDataPath, EmuLibraryGameIdDecoder.EmuLibraryPluginId.ToString());
                Directory.CreateDirectory(pluginDataPath);

                File.WriteAllText(Path.Combine(pluginDataPath, "config.json"), "{", Encoding.UTF8);

                var resolved = EmuLibrarySettingsReader.TryResolveSourceRoot(
                    extensionsDataPath,
                    playniteApplicationPath: null,
                    Guid.NewGuid(),
                    out _);

                Assert.IsFalse(resolved);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        private static void WriteConfig(string extensionsDataPath, object settings)
        {
            var pluginDataPath = Path.Combine(extensionsDataPath, EmuLibraryGameIdDecoder.EmuLibraryPluginId.ToString());
            Directory.CreateDirectory(pluginDataPath);

            var configPath = Path.Combine(pluginDataPath, "config.json");
            var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(configPath, json, Encoding.UTF8);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayAch_EmuLibTests_" + Guid.NewGuid().ToString("N"));
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
