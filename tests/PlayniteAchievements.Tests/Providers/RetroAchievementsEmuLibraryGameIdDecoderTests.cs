using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.RetroAchievements;
using ProtoBuf;
using System;
using System.IO;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class RetroAchievementsEmuLibraryGameIdDecoderTests
    {
        [TestMethod]
        public void TryDecodeSingleFile_ValidPayload_ReturnsDecodedValues()
        {
            var expectedMappingId = Guid.NewGuid();
            var expectedSourcePath = @"NES\\Super Mario Bros.nes";

            var game = new Game
            {
                PluginId = EmuLibraryGameIdDecoder.EmuLibraryPluginId,
                GameId = BuildGameId(new EmuLibrarySingleFileGameInfo
                {
                    MappingId = expectedMappingId,
                    SourcePath = expectedSourcePath
                })
            };

            var decoded = EmuLibraryGameIdDecoder.TryDecodeSingleFile(game, out var mappingId, out var sourcePath);

            Assert.IsTrue(decoded);
            Assert.AreEqual(expectedMappingId, mappingId);
            Assert.AreEqual(expectedSourcePath, sourcePath);
        }

        [TestMethod]
        public void TryDecodeSingleFile_NonEmuLibraryPlugin_ReturnsFalse()
        {
            var game = new Game
            {
                PluginId = Guid.NewGuid(),
                GameId = BuildGameId(new EmuLibrarySingleFileGameInfo
                {
                    MappingId = Guid.NewGuid(),
                    SourcePath = @"SNES\\A Link to the Past.sfc"
                })
            };

            Assert.IsFalse(EmuLibraryGameIdDecoder.TryDecodeSingleFile(game, out _, out _));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow(" ")]
        [DataRow("abc")]
        [DataRow("!1AAAA")]
        [DataRow("!0%%%%")]
        public void TryDecodeSingleFile_InvalidPayload_ReturnsFalse(string gameId)
        {
            var game = new Game
            {
                PluginId = EmuLibraryGameIdDecoder.EmuLibraryPluginId,
                GameId = gameId
            };

            Assert.IsFalse(EmuLibraryGameIdDecoder.TryDecodeSingleFile(game, out _, out _));
        }

        [TestMethod]
        public void TryDecodeSingleFile_MultiFilePayload_ReturnsFalse()
        {
            var game = new Game
            {
                PluginId = EmuLibraryGameIdDecoder.EmuLibraryPluginId,
                GameId = BuildGameId(new EmuLibraryMultiFileGameInfo
                {
                    MappingId = Guid.NewGuid(),
                    SourceBaseDir = "Chrono Trigger",
                    SourceFilePath = @"Chrono Trigger\\disc1.chd"
                })
            };

            Assert.IsFalse(EmuLibraryGameIdDecoder.TryDecodeSingleFile(game, out _, out _));
        }

        private static string BuildGameId(EmuLibraryGameInfoBase gameInfo)
        {
            using (var memoryStream = new MemoryStream())
            {
                Serializer.Serialize(memoryStream, gameInfo);
                return "!0" + Convert.ToBase64String(memoryStream.ToArray());
            }
        }
    }
}
