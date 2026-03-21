using System;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    /// <summary>
    /// Settings view for the RetroAchievements provider.
    /// </summary>
    public partial class RetroAchievementsSettingsView : ProviderSettingsViewBase
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(RetroAchievementsSettingsView));
        private RetroAchievementsSettings _raSettings;

        public override string ProviderKey => "RetroAchievements";
        public override string TabHeader => ResourceProvider.GetString("LOCPlayAch_Provider_RetroAchievements");
        public override string IconKey => "ProviderIconRetroAchievements";

        public new RetroAchievementsSettings Settings => _raSettings;

        public RetroAchievementsSettingsView()
        {
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _raSettings = settings as RetroAchievementsSettings;
            base.Initialize(settings);
        }
    }
}
