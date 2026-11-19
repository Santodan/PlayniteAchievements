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
        private IDisposable _unlockNextRequest;
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
            var current = snapshot ?? new OverviewDataSnapshot();
            _latestSnapshot = current;
            var instance = _resolveInstance();

            // An Unlock Next widget waits for its pool, read once per snapshot when a shown
            // widget first asks; until then it keeps what it shows.
            var maxPerGame = ShowcaseWidgetOptions.GetUnlockNextMaxPerGame(instance);
            if (current.NeedsUnlockNextRead(maxPerGame))
            {
                _unlockNextRequest?.Dispose();
                _unlockNextRequest = current.RequestUnlockNextPool(maxPerGame, () =>
                {
                    _unlockNextRequest = null;
                    if (ReferenceEquals(current, _latestSnapshot))
                    {
                        ApplySnapshot(current);
                    }
                });
                return;
            }

            Projection = ShowcaseWidgetProjectionService.Build(
                current,
                PersistedSettings?.Showcase,
                instance,
                gridOptions: PersistedSettings?.GridOptions);
        }

        public override void Dispose()
        {
            ShowcaseConfigurationEvents.Changed -= ShowcaseConfigurationEvents_Changed;
            _unlockNextRequest?.Dispose();
            _unlockNextRequest = null;
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
