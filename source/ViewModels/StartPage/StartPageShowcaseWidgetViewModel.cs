using System;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.Services.StartPage;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public sealed class StartPageShowcaseWidgetViewModel : StartPageWidgetViewModelBase
    {
        private readonly ShowcaseWidgetInstanceSettings _instance;
        private OverviewDataSnapshot _latestSnapshot;
        private ShowcaseWidgetProjection _projection;

        public StartPageShowcaseWidgetViewModel(
            ShowcaseWidgetInstanceSettings instance,
            StartPageDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
            : base(dataCoordinator, settings, logger)
        {
            _instance = instance ?? throw new ArgumentNullException(nameof(instance));
            ShowcaseConfigurationEvents.Changed += ShowcaseConfigurationEvents_Changed;
        }

        public ShowcaseWidgetProjection Projection
        {
            get => _projection;
            private set => SetValue(ref _projection, value);
        }

        protected override void ApplySnapshot(OverviewDataSnapshot snapshot)
        {
            _latestSnapshot = snapshot ?? new OverviewDataSnapshot();
            Projection = ShowcaseWidgetProjectionService.Build(
                _latestSnapshot,
                PersistedSettings?.Showcase,
                _instance,
                gridOptions: PersistedSettings?.GridOptions);
        }

        public override void Dispose()
        {
            ShowcaseConfigurationEvents.Changed -= ShowcaseConfigurationEvents_Changed;
            base.Dispose();
        }

        private void ShowcaseConfigurationEvents_Changed(object sender, EventArgs e)
        {
            if (_latestSnapshot != null)
            {
                ApplySnapshot(_latestSnapshot);
            }
        }
    }
}
