using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal sealed class RetroAchievementsPathResolver
    {
        private readonly IPlayniteAPI _playniteApi;

        public RetroAchievementsPathResolver(IPlayniteAPI playniteApi)
        {
            _playniteApi = playniteApi;
        }

        public IEnumerable<string> ResolveCandidateFilePaths(Game game)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (game?.Roms != null)
            {
                foreach (var rom in game.Roms)
                {
                    var p = ResolvePath(game, rom?.Path);
                    if (TryAddUniqueCandidate(seen, p, out var candidatePath))
                    {
                        yield return candidatePath;
                    }
                }
            }

            if (game?.GameActions != null)
            {
                foreach (var act in game.GameActions)
                {
                    var p = ResolvePath(game, act?.Path, act);
                    if (TryAddUniqueCandidate(seen, p, out var candidatePath) &&
                        !candidatePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return candidatePath;
                    }
                }
            }

            if (TryResolveEmuLibraryCandidateFilePath(game, out var emuLibraryPath) &&
                TryAddUniqueCandidate(seen, emuLibraryPath, out var emuLibraryCandidatePath))
            {
                yield return emuLibraryCandidatePath;
            }
        }

        private bool TryResolveEmuLibraryCandidateFilePath(Game game, out string path)
        {
            path = null;

            if (!EmuLibraryGameIdDecoder.TryDecodeSingleFile(game, out var mappingId, out var sourcePath))
            {
                return false;
            }

            if (!EmuLibrarySettingsReader.TryResolveSourceRoot(
                _playniteApi?.Paths?.ExtensionsDataPath,
                _playniteApi?.Paths?.ApplicationPath,
                mappingId,
                out var sourceRoot))
            {
                return false;
            }

            var fullSourcePath = Path.Combine(sourceRoot, sourcePath);
            var resolvedPath = ResolvePath(game, fullSourcePath);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                return false;
            }

            path = resolvedPath;
            return true;
        }

        private static bool TryAddUniqueCandidate(HashSet<string> seen, string path, out string normalizedPath)
        {
            normalizedPath = (path ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return false;
            }

            return seen.Add(normalizedPath);
        }

        private string ResolvePath(Game game, string path, GameAction sourceAction = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var p = path.Trim().Trim('"');

            try
            {
                var emulatorDir = GetGameEmulator(game, sourceAction)?.InstallDir;
                var installDir = ExpandPathVariables(game, game?.InstallDirectory, emulatorDir);

                p = ExpandPathVariables(game, p, emulatorDir, installDir);

                if (!Path.IsPathRooted(p) && !string.IsNullOrWhiteSpace(installDir))
                {
                    p = Path.Combine(installDir, p);
                }

                return p;
            }
            catch
            {
                return null;
            }
        }

        private string ExpandPathVariables(Game game, string input, string emulatorDir, string installDir = null)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return input;
            }

            var expanded =
                _playniteApi?.ExpandGameVariables(game, input, emulatorDir) ??
                _playniteApi?.ExpandGameVariables(game, input) ??
                input;

            if (!string.IsNullOrWhiteSpace(emulatorDir) &&
                expanded.IndexOf("{EmulatorDir}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                expanded = ReplaceInsensitive(expanded, "{EmulatorDir}", emulatorDir);
            }

            if (!string.IsNullOrWhiteSpace(installDir) &&
                expanded.IndexOf("{InstallDir}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                expanded = ReplaceInsensitive(expanded, "{InstallDir}", installDir);
            }

            return expanded;
        }

        private static string ReplaceInsensitive(string input, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldValue))
            {
                return input;
            }

            var idx = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return input;
            }

            var sb = new StringBuilder(input.Length);
            var start = 0;
            while (idx >= 0)
            {
                sb.Append(input.Substring(start, idx - start));
                sb.Append(newValue ?? string.Empty);
                start = idx + oldValue.Length;
                idx = input.IndexOf(oldValue, start, StringComparison.OrdinalIgnoreCase);
            }

            sb.Append(input.Substring(start));
            return sb.ToString();
        }

        private Emulator GetGameEmulator(Game game, GameAction sourceAction = null)
        {
            if (sourceAction?.Type == GameActionType.Emulator && sourceAction.EmulatorId != Guid.Empty)
            {
                return _playniteApi?.Database?.Emulators?.Get(sourceAction.EmulatorId);
            }

            if (game?.GameActions == null)
            {
                return null;
            }

            var playAction = game.GameActions.FirstOrDefault(a =>
                a?.Type == GameActionType.Emulator &&
                a.IsPlayAction &&
                a.EmulatorId != Guid.Empty);

            if (playAction != null)
            {
                return _playniteApi?.Database?.Emulators?.Get(playAction.EmulatorId);
            }

            foreach (var action in game.GameActions)
            {
                if (action?.Type == GameActionType.Emulator && action.EmulatorId != Guid.Empty)
                {
                    return _playniteApi?.Database?.Emulators?.Get(action.EmulatorId);
                }
            }

            return null;
        }
    }
}
