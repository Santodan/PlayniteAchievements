using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Exophase provider settings. Authentication is handled via session manager.
    /// </summary>
    public class ExophaseSettings : ProviderSettingsBase
    {
        private string _userId;

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

        /// <inheritdoc />
        public override IProviderSettings Clone()
        {
            return new ExophaseSettings
            {
                IsEnabled = IsEnabled,
                UserId = UserId
            };
        }

        /// <inheritdoc />
        public override void CopyFrom(IProviderSettings source)
        {
            if (source is ExophaseSettings other)
            {
                IsEnabled = other.IsEnabled;
                UserId = other.UserId;
            }
        }
    }
}
