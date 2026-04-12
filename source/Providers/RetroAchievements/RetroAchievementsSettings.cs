using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    /// <summary>
    /// RetroAchievements provider settings.
    /// </summary>
    public class RetroAchievementsSettings : ProviderSettingsBase
    {
        private string _raUsername;
        private string _raWebApiKey;
        private string _raRarityStats = "casual";
        private string _raPointsMode = "points";
        private int _hashIndexMaxAgeDays = 30;
        private bool _enableArchiveScanning = true;
        private bool _enableDiscHashing = true;
        private bool _enableRaNameFallback = true;
        private bool _enableFuzzyNameMatching = true;
        private bool _enableRaSubsetScanning = true;
        private Dictionary<Guid, int> _raGameIdOverrides = new Dictionary<Guid, int>();

        /// <inheritdoc />
        public override string ProviderKey => "RetroAchievements";

        /// <summary>
        /// Gets or sets the RetroAchievements username.
        /// </summary>
        public string RaUsername
        {
            get => _raUsername;
            set => SetValue(ref _raUsername, value);
        }

        /// <summary>
        /// Gets or sets the RetroAchievements web API key.
        /// </summary>
        public string RaWebApiKey
        {
            get => _raWebApiKey;
            set => SetValue(ref _raWebApiKey, value);
        }

        /// <summary>
        /// RetroAchievements rarity stats mode: "casual", "hardcore", or "combined".
        /// </summary>
        public string RaRarityStats
        {
            get => _raRarityStats;
            set
            {
                var mode = (value ?? string.Empty).Trim();
                if (string.Equals(mode, "hardcore", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "combined", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "casual", StringComparison.OrdinalIgnoreCase))
                {
                    SetValue(ref _raRarityStats, mode.ToLowerInvariant());
                }
                else
                {
                    SetValue(ref _raRarityStats, "casual");
                }
            }
        }

        /// <summary>
        /// Determines which points value to display for RetroAchievements:
        /// "points" for standard points, "scaled" for TrueRatio (weighted by rarity).
        /// </summary>
        public string RaPointsMode
        {
            get => _raPointsMode;
            set
            {
                var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
                SetValue(ref _raPointsMode,
                    (mode == "scaled" || mode == "points") ? mode : "points");
            }
        }

        /// <summary>
        /// Maximum age in days for hash index entries before they're considered stale.
        /// </summary>
        public int HashIndexMaxAgeDays
        {
            get => _hashIndexMaxAgeDays;
            set => SetValue(ref _hashIndexMaxAgeDays, Math.Max(1, value));
        }

        /// <summary>
        /// Enable scanning for achievements inside archive files (zip, 7z, etc.).
        /// </summary>
        public bool EnableArchiveScanning
        {
            get => _enableArchiveScanning;
            set => SetValue(ref _enableArchiveScanning, value);
        }

        /// <summary>
        /// Enable hashing of disc-based games (ISO, CUE/BIN, etc.).
        /// </summary>
        public bool EnableDiscHashing
        {
            get => _enableDiscHashing;
            set => SetValue(ref _enableDiscHashing, value);
        }

        /// <summary>
        /// Enable name-based fallback for RetroAchievements when hash matching fails.
        /// </summary>
        public bool EnableRaNameFallback
        {
            get => _enableRaNameFallback;
            set => SetValue(ref _enableRaNameFallback, value);
        }

        /// <summary>
        /// Enable fuzzy title matching for RetroAchievements name fallback.
        /// When disabled, name fallback only uses exact normalized title matches.
        /// </summary>
        public bool EnableFuzzyNameMatching
        {
            get => _enableFuzzyNameMatching;
            set => SetValue(ref _enableFuzzyNameMatching, value);
        }

        /// <summary>
        /// Enable scanning for RetroAchievements subset achievement sets alongside the base game.
        /// When enabled, subset achievements are merged into the same game entry with distinct categories.
        /// </summary>
        public bool EnableRaSubsetScanning
        {
            get => _enableRaSubsetScanning;
            set => SetValue(ref _enableRaSubsetScanning, value);
        }

        /// <summary>
        /// Manual overrides for RetroAchievements game IDs.
        /// Key is Playnite Game ID, value is RetroAchievements game ID.
        /// Used when automatic hash-based or name-based matching fails.
        /// </summary>
        [JsonIgnore]
        public Dictionary<Guid, int> RaGameIdOverrides
        {
            get => _raGameIdOverrides;
            set => SetValue(ref _raGameIdOverrides, value ?? new Dictionary<Guid, int>());
        }
    }
}
