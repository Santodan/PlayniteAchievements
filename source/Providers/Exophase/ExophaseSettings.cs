using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Exophase provider settings. Authentication is handled via session manager.
    /// </summary>
    public class ExophaseSettings : ProviderSettingsBase
    {
        private string _userId;
        private HashSet<string> _managedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<Guid> _includedGames = new HashSet<Guid>();
        private Dictionary<Guid, string> _slugOverrides = new Dictionary<Guid, string>();

        /// <inheritdoc />
        public override string ProviderKey => "Exophase";

        /// <summary>
        /// Exophase user ID (username).
        /// </summary>
        public string UserId
        {
            get => _userId;
            set => SetValue(ref _userId, value);
        }

        /// <summary>
        /// Provider/platform tokens that Exophase should automatically claim.
        /// Games matching these tokens will use Exophase instead of modern providers.
        /// Valid values: "steam", "psn", "xbox", "gog", "epic", "blizzard", "origin", "retro", "android", "apple"
        /// </summary>
        public HashSet<string> ManagedProviders
        {
            get => _managedProviders;
            set => SetValue(ref _managedProviders, value ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Individual game IDs that should use Exophase even if their provider/platform token is not in managed providers.
        /// Allows per-game override for platforms not globally enabled.
        /// </summary>
        [JsonIgnore]
        public HashSet<Guid> IncludedGames
        {
            get => _includedGames;
            set => SetValue(ref _includedGames, value ?? new HashSet<Guid>());
        }

        /// <summary>
        /// Per-game Exophase slug overrides.
        /// Key is Playnite Game ID, value is the Exophase game slug (e.g., "game-name-gog").
        /// When set, this slug is used directly instead of auto-detection.
        /// </summary>
        [JsonIgnore]
        public Dictionary<Guid, string> SlugOverrides
        {
            get => _slugOverrides;
            set => SetValue(ref _slugOverrides, value ?? new Dictionary<Guid, string>());
        }
    }
}
