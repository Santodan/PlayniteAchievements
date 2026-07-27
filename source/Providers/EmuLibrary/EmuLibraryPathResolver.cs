using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.IO;

namespace PlayniteAchievements.Providers.EmuLibrary
{
    /// <summary>
    /// Resolves the original source paths of EmuLibrary games from their serialized game ids.
    /// EmuLibrary games only receive rom/install paths on install, so this allows providers
    /// to locate uninstalled games through the mapping source (e.g. a network share).
    /// </summary>
    internal static class EmuLibraryPathResolver
    {
        /// <summary>
        /// Resolves the game's source path: the base directory for multi-file games,
        /// or the source file for single-file games.
        /// </summary>
        public static bool TryResolveSourcePath(IPlayniteAPI playniteApi, Game game, out string path)
        {
            return TryResolveSourcePath(
                playniteApi?.Paths?.ExtensionsDataPath,
                playniteApi?.Paths?.ApplicationPath,
                game,
                out path);
        }

        /// <summary>
        /// Resolves the full path of the game's primary source file.
        /// </summary>
        public static bool TryResolveSourceFilePath(IPlayniteAPI playniteApi, Game game, out string filePath)
        {
            return TryResolveSourceFilePath(
                playniteApi?.Paths?.ExtensionsDataPath,
                playniteApi?.Paths?.ApplicationPath,
                game,
                out filePath);
        }

        /// <summary>
        /// Resolves the full path of the game's source base directory (multi-file games only).
        /// </summary>
        public static bool TryResolveSourceDirectory(IPlayniteAPI playniteApi, Game game, out string directoryPath)
        {
            return TryResolveSourceDirectory(
                playniteApi?.Paths?.ExtensionsDataPath,
                playniteApi?.Paths?.ApplicationPath,
                game,
                out directoryPath);
        }

        internal static bool TryResolveSourcePath(string extensionsDataPath, string applicationPath, Game game, out string path)
        {
            return TryResolveSourceDirectory(extensionsDataPath, applicationPath, game, out path) ||
                   TryResolveSourceFilePath(extensionsDataPath, applicationPath, game, out path);
        }

        internal static bool TryResolveSourceFilePath(string extensionsDataPath, string applicationPath, Game game, out string filePath)
        {
            filePath = null;

            if (EmuLibraryGameIdDecoder.TryDecodeSingleFile(game, out var mappingId, out var sourcePath))
            {
                return TryCombineWithSourceRoot(extensionsDataPath, applicationPath, mappingId, sourcePath, out filePath);
            }

            if (EmuLibraryGameIdDecoder.TryDecodeMultiFile(game, out mappingId, out var sourceFilePath, out _) &&
                !string.IsNullOrWhiteSpace(sourceFilePath))
            {
                return TryCombineWithSourceRoot(extensionsDataPath, applicationPath, mappingId, sourceFilePath, out filePath);
            }

            return false;
        }

        internal static bool TryResolveSourceDirectory(string extensionsDataPath, string applicationPath, Game game, out string directoryPath)
        {
            directoryPath = null;

            return EmuLibraryGameIdDecoder.TryDecodeMultiFile(game, out var mappingId, out _, out var sourceBaseDir) &&
                   !string.IsNullOrWhiteSpace(sourceBaseDir) &&
                   TryCombineWithSourceRoot(extensionsDataPath, applicationPath, mappingId, sourceBaseDir, out directoryPath);
        }

        private static bool TryCombineWithSourceRoot(
            string extensionsDataPath,
            string applicationPath,
            Guid mappingId,
            string relativePath,
            out string fullPath)
        {
            fullPath = null;

            if (!EmuLibrarySettingsReader.TryResolveSourceRoot(
                extensionsDataPath,
                applicationPath,
                mappingId,
                out var sourceRoot))
            {
                return false;
            }

            try
            {
                fullPath = Path.Combine(sourceRoot, relativePath);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
