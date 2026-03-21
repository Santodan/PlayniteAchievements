using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.PSN
{
    /// <summary>
    /// PlayStation Network provider settings. Authentication is handled via session manager.
    /// </summary>
    public class PsnSettings : ProviderSettingsBase
    {
        private string _npsso;

        /// <inheritdoc />
        public override string ProviderKey => "PSN";

        /// <summary>
        /// NPSSO cookie value for PlayStation Network authentication.
        /// Can be obtained from https://ca.account.sony.com/api/v1/ssocookie
        /// </summary>
        public string Npsso
        {
            get => _npsso;
            set => SetValue(ref _npsso, value ?? string.Empty);
        }

        /// <inheritdoc />
        public override IProviderSettings Clone()
        {
            return new PsnSettings
            {
                IsEnabled = IsEnabled,
                Npsso = Npsso
            };
        }

        /// <inheritdoc />
        public override void CopyFrom(IProviderSettings source)
        {
            if (source is PsnSettings other)
            {
                IsEnabled = other.IsEnabled;
                Npsso = other.Npsso;
            }
        }
    }
}
