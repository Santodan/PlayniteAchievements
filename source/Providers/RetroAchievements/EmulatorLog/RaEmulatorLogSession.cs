using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.RetroAchievements.EmulatorLog
{
    /// <summary>
    /// Per-game live-tracking state carried as the opaque <see cref="InGameProgressRegistration.State"/>
    /// for an emulator-log source. Resolved once at game start so repeated reads only tail newly
    /// appended log bytes instead of re-parsing the whole file.
    /// </summary>
    internal sealed class RaEmulatorLogSession
    {
        public RaEmulatorLogSession(
            string logPath,
            RaEmulatorLogParseProfile profile,
            IReadOnlyCollection<string> schemaAchievementIds)
        {
            LogPath = logPath;
            Profile = profile;
            SchemaAchievementIds = new HashSet<string>(
                schemaAchievementIds ?? Array.Empty<string>(),
                StringComparer.Ordinal);
        }

        public string LogPath { get; }

        public RaEmulatorLogParseProfile Profile { get; }

        /// <summary>Achievement ids (RetroAchievements ids as strings) present in this game's cached schema.</summary>
        public HashSet<string> SchemaAchievementIds { get; }

        /// <summary>Byte offset already consumed from the log; reset to 0 when the emulator rewrites the file.</summary>
        public long ConsumedOffset { get; set; }

        /// <summary>Last observed hardcore-mode state for the running session (softcore until proven otherwise).</summary>
        public bool Hardcore { get; set; }
    }
}
