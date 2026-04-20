using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Steam
{
    public enum SteamExistingGameImportBehavior
    {
        OverwriteExisting = 0,
        SkipExisting = 1
    }

    /// <summary>
    /// Steam-specific provider settings.
    /// </summary>
    public class SteamSettings : ProviderSettingsBase
    {
        private string _steamUserId;
        private string _steamApiKey;
        private bool _includeFamilySharedGames = true;
        private string _importedGameMetadataSourceId = string.Empty;
        private SteamExistingGameImportBehavior _existingGameImportBehavior = SteamExistingGameImportBehavior.OverwriteExisting;

        /// <inheritdoc />
        public override string ProviderKey => "Steam";

        /// <summary>
        /// Gets or sets the last successfully probed Steam user ID.
        /// This is derived auth state, not user-editable configuration.
        /// </summary>
        public string SteamUserId
        {
            get => _steamUserId;
            set => SetValue(ref _steamUserId, value);
        }

        /// <summary>
        /// Gets or sets the Steam Web API key.
        /// </summary>
        public string SteamApiKey
        {
            get => _steamApiKey;
            set => SetValue(ref _steamApiKey, value);
        }

        public bool IncludeFamilySharedGames
        {
            get => _includeFamilySharedGames;
            set => SetValue(ref _includeFamilySharedGames, value);
        }

        public string ImportedGameMetadataSourceId
        {
            get => _importedGameMetadataSourceId;
            set => SetValue(ref _importedGameMetadataSourceId, value ?? string.Empty);
        }

        public SteamExistingGameImportBehavior ExistingGameImportBehavior
        {
            get => _existingGameImportBehavior;
            set => SetValue(ref _existingGameImportBehavior, value);
        }
    }
}
