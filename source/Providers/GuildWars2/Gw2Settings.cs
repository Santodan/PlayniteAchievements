using Newtonsoft.Json;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Guild Wars 2 provider settings. The API has no OAuth flow: the player creates a personal
    /// access token at account.arena.net and pastes it here, the same way the RetroAchievements and
    /// Riot keys are supplied. The plugin never ships one.
    /// </summary>
    public class Gw2Settings : ProviderSettingsBase
    {
        private string _apiKey;
        private string _accountName;
        private string _accountId;

        /// <inheritdoc />
        public override string ProviderKey => "GW2";

        /// <summary>
        /// The player's own API key. Stored in the plugin settings file the same way the Riot and
        /// RetroAchievements web API keys are.
        /// </summary>
        public string ApiKey
        {
            get => _apiKey;
            set => SetValue(ref _apiKey, value);
        }

        /// <summary>
        /// Account name in Player.1234 form, resolved by a Check and cached so the settings card can
        /// name the account without a network call. Cleared whenever the key changes.
        /// </summary>
        public string AccountName
        {
            get => _accountName;
            set => SetValue(ref _accountName, value);
        }

        /// <summary>
        /// The account GUID. Stable across display-name changes, so it is what identifies the
        /// account rather than the name shown on the card.
        /// </summary>
        public string AccountId
        {
            get => _accountId;
            set => SetValue(ref _accountId, value);
        }

        /// <summary>True when a refresh has a key to send.</summary>
        [JsonIgnore]
        public bool HasCredentials => !string.IsNullOrWhiteSpace(ApiKey);

        /// <summary>True once a Check has confirmed the key against the API.</summary>
        [JsonIgnore]
        public bool IsVerified => HasCredentials && !string.IsNullOrWhiteSpace(AccountId);
    }
}
