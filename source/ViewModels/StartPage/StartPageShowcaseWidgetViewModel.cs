using System;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public sealed class StartPageShowcaseWidgetViewModel : StartPageWidgetViewModelBase
    {
        // Resolved per projection rather than held: a settings cancel replaces Persisted with a
        // deep clone, and a held instance would keep projecting the orphaned copy while the
        // widget's settings editor writes to the live one.
        private readonly Func<ShowcaseWidgetInstanceSettings> _resolveInstance;
        private OverviewDataSnapshot _latestSnapshot;
        private ShowcaseWidgetProjection _projection;

        public StartPageShowcaseWidgetViewModel(
            Func<ShowcaseWidgetInstanceSettings> resolveInstance,
            Services.Widgets.WidgetDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
            : base(dataCoordinator, settings, logger)
        {
            _resolveInstance = resolveInstance ?? throw new ArgumentNullException(nameof(resolveInstance));
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
                _resolveInstance(),
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
