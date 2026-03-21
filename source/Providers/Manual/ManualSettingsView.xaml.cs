using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Manual
{
    public partial class ManualSettingsView : ProviderSettingsViewBase
    {
        private ManualSettings _manualSettings;

        public override string ProviderKey => "Manual";
        public override string TabHeader => ResourceProvider.GetString("LOCPlayAch_Provider_Manual");
        public override string IconKey => "ProviderIconManual";

        public new ManualSettings Settings => _manualSettings;

        public ManualSettingsView()
        {
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _manualSettings = settings as ManualSettings;
            base.Initialize(settings);
        }
    }
}
