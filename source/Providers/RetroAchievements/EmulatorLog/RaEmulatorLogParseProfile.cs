using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.RetroAchievements.EmulatorLog
{
    /// <summary>
    /// The regular expressions used to interpret a RetroAchievements-capable emulator log.
    /// rcheevos-based emulators (RetroArch, Dolphin, PCSX2, PPSSPP, and modern DuckStation)
    /// share the "Awarding achievement" unlock line; DuckStation additionally emits its own
    /// game-load and mode lines, so its profile extends the shared rcheevos patterns.
    /// </summary>
    internal sealed class RaEmulatorLogParseProfile
    {
        private const RegexOptions Options =
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private RaEmulatorLogParseProfile(
            Regex award,
            Regex[] gameLoaded,
            Regex[] hardcoreEnabled,
            Regex hardcoreDisabled,
            Regex gameUnloaded)
        {
            Award = award;
            GameLoaded = gameLoaded;
            HardcoreEnabled = hardcoreEnabled;
            HardcoreDisabled = hardcoreDisabled;
            GameUnloaded = gameUnloaded;
        }

        /// <summary>Matches an unlock line; capture group 1 is the RetroAchievements achievement id.</summary>
        public Regex Award { get; }

        /// <summary>Matches a session start line; capture group 1 is the RetroAchievements game id.</summary>
        public Regex[] GameLoaded { get; }

        /// <summary>Matches a line that indicates hardcore mode is active for the session.</summary>
        public Regex[] HardcoreEnabled { get; }

        /// <summary>Matches a line that indicates hardcore mode is inactive (softcore) for the session.</summary>
        public Regex HardcoreDisabled { get; }

        /// <summary>Matches a session end line; capture group 1 is the RetroAchievements game id.</summary>
        public Regex GameUnloaded { get; }

        // rcheevos client (RetroArch / Dolphin / PCSX2 / PPSSPP): "Game 1234 loaded, Hardcore enabled",
        // "Awarding achievement 56789", "Unloading game 1234".
        public static readonly RaEmulatorLogParseProfile Rcheevos = new RaEmulatorLogParseProfile(
            award: new Regex(@"Awarding achievement (\d+)", Options),
            gameLoaded: new[] { new Regex(@"Game (\d+) loaded", Options) },
            hardcoreEnabled: new[] { new Regex(@"Hardcore enabled", Options) },
            hardcoreDisabled: new Regex(@"Hardcore disabled", Options),
            gameUnloaded: new Regex(@"Unloading game (\d+)", Options));

        // DuckStation shares the rcheevos unlock line but also emits "Identified game: 1234" on load.
        // The award pattern is best-effort and should be confirmed against a live DuckStation log;
        // an unmatched line simply defers to the on-close full refresh.
        public static readonly RaEmulatorLogParseProfile DuckStation = new RaEmulatorLogParseProfile(
            award: new Regex(@"Awarding achievement (\d+)", Options),
            gameLoaded: new[]
            {
                new Regex(@"Game (\d+) loaded", Options),
                new Regex(@"Identified game: (\d+)", Options)
            },
            hardcoreEnabled: new[] { new Regex(@"Hardcore enabled", Options) },
            hardcoreDisabled: new Regex(@"Hardcore disabled", Options),
            gameUnloaded: new Regex(@"Unloading game (\d+)", Options));
    }
}
