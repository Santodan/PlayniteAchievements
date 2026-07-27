using System;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.StartPage;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public sealed class StartPageScoreCardWidgetViewModel : StartPageWidgetViewModelBase
    {
        private readonly StartPageWidgetKind _widgetKind;

        public StartPageScoreCardWidgetViewModel(
            StartPageWidgetKind widgetKind,
            StartPageDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
            : base(dataCoordinator, settings, logger)
        {
            if (widgetKind != StartPageWidgetKind.CollectionScoreCard &&
                widgetKind != StartPageWidgetKind.PrestigeScoreCard)
            {
                throw new ArgumentOutOfRangeException(nameof(widgetKind));
            }

            _widgetKind = widgetKind;
            ScoreCard = new ScoreCardViewModel(widgetKind == StartPageWidgetKind.CollectionScoreCard
                ? ScoreCardType.Collection
                : ScoreCardType.Prestige);
        }

        public ScoreCardViewModel ScoreCard { get; }

        protected override void ApplySnapshot(OverviewDataSnapshot snapshot)
        {
            var useUniformRarityBadges = PersistedSettings?.UseUniformRarityBadges ?? false;
            if (_widgetKind == StartPageWidgetKind.CollectionScoreCard)
            {
                ScoreCard.Apply(
                    snapshot?.CollectorScore ?? 0,
                    snapshot?.CollectorLevel ?? 0,
                    snapshot?.CollectorLevelProgress ?? 0,
                    snapshot?.CollectorRank,
                    useUniformRarityBadges);
                return;
            }

            ScoreCard.Apply(
                snapshot?.PrestigeScore ?? 0,
                snapshot?.PrestigeLevel ?? 0,
                snapshot?.PrestigeLevelProgress ?? 0,
                snapshot?.PrestigeRank,
                useUniformRarityBadges);
        }

        protected override void OnPersistedSettingsChanged(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName) ||
                RarityAppearanceHelper.IsAppearanceSettingPropertyName(propertyName))
            {
                ScoreCard.RefreshBadgeStyle(PersistedSettings?.UseUniformRarityBadges ?? false);
            }
        }

        protected override bool ShouldRefreshForPersistedSettingsChanged(string propertyName)
        {
            if (RarityAppearanceHelper.IsAppearanceSettingPropertyName(propertyName))
            {
                return false;
            }

            return base.ShouldRefreshForPersistedSettingsChanged(propertyName);
        }
    }
}
