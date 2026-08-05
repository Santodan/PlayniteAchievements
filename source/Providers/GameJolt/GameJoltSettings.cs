using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.GameJolt
{
    /// <summary>
    /// GameJolt provider settings. Authentication is cookie-based and handled by the session manager;
    /// the persisted <see cref="UserId"/> is the logged-in GameJolt username used to build the
    /// per-user trophy endpoint.
    /// </summary>
    public sealed class GameJoltSettings : ProviderSettingsBase
    {
        private string _userId;

        /// <inheritdoc />
        public override string ProviderKey => "GameJolt";

        /// <summary>
        /// The logged-in GameJolt username (without the leading '@').
        /// </summary>
        public string UserId
        {
            get => _userId;
            set => SetValue(ref _userId, value);
        }
    }
}
