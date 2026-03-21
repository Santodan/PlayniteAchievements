using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.GOG
{
    /// <summary>
    /// GOG provider settings. Authentication is handled via session manager.
    /// </summary>
    public class GogSettings : ProviderSettingsBase
    {
        private string _userId;

        /// <inheritdoc />
        public override string ProviderKey => "GOG";

        /// <summary>
        /// GOG user ID.
        /// </summary>
        public string UserId
        {
            get => _userId;
            set => SetValue(ref _userId, value);
        }

        /// <inheritdoc />
        public override IProviderSettings Clone()
        {
            return new GogSettings
            {
                IsEnabled = IsEnabled,
                UserId = UserId
            };
        }

        /// <inheritdoc />
        public override void CopyFrom(IProviderSettings source)
        {
            if (source is GogSettings other)
            {
                IsEnabled = other.IsEnabled;
                UserId = other.UserId;
            }
        }
    }
}
