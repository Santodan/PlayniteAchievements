using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.ShadPS4
{
    public partial class ShadPS4SettingsView : ProviderSettingsViewBase
    {
        private ShadPS4Settings _shadps4Settings;

        public override string ProviderKey => "ShadPS4";
        public override string TabHeader => ResourceProvider.GetString("LOCPlayAch_Provider_ShadPS4");
        public override string IconKey => "ProviderIconShadPS4";

        public new ShadPS4Settings Settings => _shadps4Settings;

        public ShadPS4SettingsView()
        {
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _shadps4Settings = settings as ShadPS4Settings;
            base.Initialize(settings);
        }
    }
}
