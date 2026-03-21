using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.RPCS3
{
    public partial class Rpcs3SettingsView : ProviderSettingsViewBase
    {
        private Rpcs3Settings _rpcs3Settings;

        public override string ProviderKey => "RPCS3";
        public override string TabHeader => ResourceProvider.GetString("LOCPlayAch_Provider_RPCS3");
        public override string IconKey => "ProviderIconRPCS3";

        public new Rpcs3Settings Settings => _rpcs3Settings;

        public Rpcs3SettingsView()
        {
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _rpcs3Settings = settings as Rpcs3Settings;
            base.Initialize(settings);
        }
    }
}
