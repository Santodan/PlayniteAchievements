using Playnite.SDK.Models;
using ProtoBuf;
using System;
using System.IO;

namespace PlayniteAchievements.Providers.EmuLibrary
{
    [ProtoContract]
    [ProtoInclude(10, typeof(EmuLibrarySingleFileGameInfo))]
    [ProtoInclude(11, typeof(EmuLibraryMultiFileGameInfo))]
    [ProtoInclude(14, typeof(EmuLibraryYuzuGameInfo))]
    internal abstract class EmuLibraryGameInfoBase
    {
        [ProtoMember(1)]
        public Guid MappingId { get; set; }
    }

    [ProtoContract]
    internal sealed class EmuLibrarySingleFileGameInfo : EmuLibraryGameInfoBase
    {
        [ProtoMember(1)]
        public string SourcePath { get; set; }
    }

    [ProtoContract]
    internal sealed class EmuLibraryMultiFileGameInfo : EmuLibraryGameInfoBase
    {
        [ProtoMember(1)]
        public string SourceFilePath { get; set; }

        [ProtoMember(2)]
        public string SourceBaseDir { get; set; }
    }

    [ProtoContract]
    internal sealed class EmuLibraryYuzuGameInfo : EmuLibraryGameInfoBase
    {
        [ProtoMember(1)]
        public ulong TitleId { get; set; }
    }

    internal static class EmuLibraryGameIdDecoder
    {
        private const string GameIdPrefix = "!0";
        internal static readonly Guid EmuLibraryPluginId = Guid.Parse("41e49490-0583-4148-94d2-940c7c74f1d9");

        public static bool TryDecodeSingleFile(Game game, out Guid mappingId, out string sourcePath)
        {
            mappingId = Guid.Empty;
            sourcePath = null;

            if (game == null || game.PluginId != EmuLibraryPluginId)
            {
                return false;
            }

            return TryDecodeSingleFile(game.GameId, out mappingId, out sourcePath);
        }

        internal static bool TryDecodeSingleFile(string gameId, out Guid mappingId, out string sourcePath)
        {
            mappingId = Guid.Empty;
            sourcePath = null;

            var gameInfo = Decode(gameId) as EmuLibrarySingleFileGameInfo;
            if (gameInfo == null ||
                gameInfo.MappingId == Guid.Empty ||
                string.IsNullOrWhiteSpace(gameInfo.SourcePath))
            {
                return false;
            }

            mappingId = gameInfo.MappingId;
            sourcePath = gameInfo.SourcePath.Trim();
            return true;
        }

        public static bool TryDecodeMultiFile(Game game, out Guid mappingId, out string sourceFilePath, out string sourceBaseDir)
        {
            mappingId = Guid.Empty;
            sourceFilePath = null;
            sourceBaseDir = null;

            if (game == null || game.PluginId != EmuLibraryPluginId)
            {
                return false;
            }

            return TryDecodeMultiFile(game.GameId, out mappingId, out sourceFilePath, out sourceBaseDir);
        }

        internal static bool TryDecodeMultiFile(string gameId, out Guid mappingId, out string sourceFilePath, out string sourceBaseDir)
        {
            mappingId = Guid.Empty;
            sourceFilePath = null;
            sourceBaseDir = null;

            var gameInfo = Decode(gameId) as EmuLibraryMultiFileGameInfo;
            if (gameInfo == null || gameInfo.MappingId == Guid.Empty)
            {
                return false;
            }

            var trimmedFilePath = gameInfo.SourceFilePath?.Trim();
            var trimmedBaseDir = gameInfo.SourceBaseDir?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedFilePath) && string.IsNullOrWhiteSpace(trimmedBaseDir))
            {
                return false;
            }

            mappingId = gameInfo.MappingId;
            sourceFilePath = string.IsNullOrWhiteSpace(trimmedFilePath) ? null : trimmedFilePath;
            sourceBaseDir = string.IsNullOrWhiteSpace(trimmedBaseDir) ? null : trimmedBaseDir;
            return true;
        }

        private static EmuLibraryGameInfoBase Decode(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId) ||
                gameId.Length <= GameIdPrefix.Length ||
                !gameId.StartsWith(GameIdPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                var serializedData = Convert.FromBase64String(gameId.Substring(GameIdPrefix.Length));
                using (var memoryStream = new MemoryStream(serializedData))
                {
                    return Serializer.Deserialize<EmuLibraryGameInfoBase>(memoryStream);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
