using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.RetroAchievements.EmulatorLog
{
    /// <summary>
    /// The fixed set of RetroAchievements-capable emulators whose logs the in-game monitor can tail,
    /// plus the logic that maps a Playnite emulator to an entry and resolves its log path. Default
    /// paths are best-effort; the user can override any of them per emulator in provider settings.
    /// </summary>
    internal static class RaEmulatorLogRegistry
    {
        internal sealed class Entry
        {
            public Entry(
                string key,
                string displayName,
                string[] identityTokens,
                RaEmulatorLogParseProfile profile,
                Func<string, IReadOnlyList<string>> defaultPathCandidates)
            {
                Key = key;
                DisplayName = displayName;
                IdentityTokens = identityTokens;
                Profile = profile;
                _defaultPathCandidates = defaultPathCandidates;
            }

            private readonly Func<string, IReadOnlyList<string>> _defaultPathCandidates;

            /// <summary>Stable settings key (also used for the override dictionary).</summary>
            public string Key { get; }

            /// <summary>Human-readable emulator name (proper noun, not localized).</summary>
            public string DisplayName { get; }

            public string[] IdentityTokens { get; }

            public RaEmulatorLogParseProfile Profile { get; }

            /// <summary>
            /// Resolves the best-guess default log path for this emulator, preferring an existing file
            /// among the candidates and otherwise returning the first candidate for display.
            /// </summary>
            public string ResolveDefaultLogPath(string installDir)
            {
                var candidates = _defaultPathCandidates(installDir);
                if (candidates == null || candidates.Count == 0)
                {
                    return null;
                }

                foreach (var candidate in candidates)
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            }

            public bool Matches(Emulator emulator)
            {
                if (emulator == null)
                {
                    return false;
                }

                var haystack = string.Join(
                    "\n",
                    emulator.BuiltInConfigId ?? string.Empty,
                    emulator.Name ?? string.Empty,
                    emulator.InstallDir ?? string.Empty);

                return IdentityTokens.Any(token =>
                    haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        private static string Documents(params string[] parts)
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(root) ? null : Combine(root, parts);
        }

        private static string AppData(params string[] parts)
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrWhiteSpace(root) ? null : Combine(root, parts);
        }

        private static string Install(string installDir, params string[] parts)
        {
            return string.IsNullOrWhiteSpace(installDir) ? null : Combine(installDir, parts);
        }

        private static string Combine(string root, string[] parts)
        {
            var all = new string[parts.Length + 1];
            all[0] = root;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return Path.Combine(all);
        }

        public static readonly IReadOnlyList<Entry> Entries = new[]
        {
            new Entry(
                "retroarch",
                "RetroArch",
                new[] { "retroarch" },
                RaEmulatorLogParseProfile.Rcheevos,
                installDir => new[] { Install(installDir, "Logs", "retroarch.log") }),

            new Entry(
                "dolphin",
                "Dolphin",
                new[] { "dolphin" },
                RaEmulatorLogParseProfile.Rcheevos,
                installDir => new[]
                {
                    Install(installDir, "User", "Logs", "dolphin.log"),
                    Documents("Dolphin Emulator", "Logs", "dolphin.log"),
                    AppData("Dolphin Emulator", "Logs", "dolphin.log")
                }),

            new Entry(
                "pcsx2",
                "PCSX2",
                new[] { "pcsx2" },
                RaEmulatorLogParseProfile.Rcheevos,
                installDir => new[]
                {
                    Install(installDir, "logs", "emulog.txt"),
                    Documents("PCSX2", "Logs", "emulog.txt")
                }),

            new Entry(
                "duckstation",
                "DuckStation",
                new[] { "duckstation" },
                RaEmulatorLogParseProfile.DuckStation,
                installDir => new[]
                {
                    Documents("DuckStation", "RACache", "RALog.txt"),
                    Documents("DuckStation", "duckstation.log"),
                    Install(installDir, "RACache", "RALog.txt")
                }),

            new Entry(
                "ppsspp",
                "PPSSPP",
                new[] { "ppsspp" },
                RaEmulatorLogParseProfile.Rcheevos,
                installDir => new[]
                {
                    Install(installDir, "memstick", "PSP", "SYSTEM", "DUMP", "log.txt"),
                    Documents("PPSSPP", "PSP", "SYSTEM", "DUMP", "log.txt")
                })
        };

        public static Entry FindEntry(Emulator emulator)
        {
            return emulator == null ? null : Entries.FirstOrDefault(entry => entry.Matches(emulator));
        }

        public static Entry FindEntry(string key)
        {
            return string.IsNullOrWhiteSpace(key)
                ? null
                : Entries.FirstOrDefault(entry =>
                    string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Finds the first emulator in the Playnite database that matches the entry, so the settings UI
        /// can display an install-relative default path even without a running game.
        /// </summary>
        public static Emulator FindDatabaseEmulator(IPlayniteAPI playniteApi, Entry entry)
        {
            var emulators = playniteApi?.Database?.Emulators;
            if (emulators == null || entry == null)
            {
                return null;
            }

            return emulators.FirstOrDefault(entry.Matches);
        }

        /// <summary>
        /// Resolves the emulator a game launches through and the log entry that services it.
        /// </summary>
        public static Entry ResolveForGame(IPlayniteAPI playniteApi, Game game, out Emulator emulator)
        {
            emulator = null;
            if (game?.GameActions == null)
            {
                return null;
            }

            foreach (var action in game.GameActions)
            {
                if (action?.Type != GameActionType.Emulator || action.EmulatorId == Guid.Empty)
                {
                    continue;
                }

                var candidate = playniteApi?.Database?.Emulators?.Get(action.EmulatorId);
                var entry = FindEntry(candidate);
                if (entry != null)
                {
                    emulator = candidate;
                    return entry;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves the effective log path for an emulator entry: an explicit user override when present,
        /// otherwise the best-guess default derived from the emulator install directory.
        /// </summary>
        public static string ResolveEffectiveLogPath(
            Entry entry,
            Emulator emulator,
            IReadOnlyDictionary<string, string> overrides)
        {
            if (entry == null)
            {
                return null;
            }

            if (overrides != null &&
                overrides.TryGetValue(entry.Key, out var overridePath) &&
                !string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath.Trim();
            }

            return entry.ResolveDefaultLogPath(emulator?.InstallDir);
        }
    }
}
