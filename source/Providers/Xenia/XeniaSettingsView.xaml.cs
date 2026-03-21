using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Xenia
{
    public partial class XeniaSettingsView : ProviderSettingsViewBase
    {
        private XeniaSettings _xeniaSettings;

        public override string ProviderKey => "Xenia";
        public override string TabHeader => ResourceProvider.GetString("LOCPlayAch_Provider_Xenia");
        public override string IconKey => "ProviderIconXenia";

        public new XeniaSettings Settings => _xeniaSettings;

        public XeniaSettingsView()
        {
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _xeniaSettings = settings as XeniaSettings;
            base.Initialize(settings);
        }
    }
}
