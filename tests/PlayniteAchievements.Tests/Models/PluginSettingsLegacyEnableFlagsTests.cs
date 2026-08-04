using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class PluginSettingsLegacyEnableFlagsTests
    {
        private static readonly (string CompatFlag, Func<PlayniteAchievementsSettings, bool> ReadFlag, Action<PersistedSettings, bool> SetPersisted)[] FlagMap =
        {
            (nameof(PlayniteAchievementsSettings.EnableIntegrationCompact), s => s.EnableIntegrationCompact, (p, v) => p.EnableAchievementCompactListControl = v),
            (nameof(PlayniteAchievementsSettings.EnableIntegrationButton), s => s.EnableIntegrationButton, (p, v) => p.EnableAchievementButtonControl = v),
            (nameof(PlayniteAchievementsSettings.EnableIntegrationViewItem), s => s.EnableIntegrationViewItem, (p, v) => p.EnableAchievementViewItemControl = v),
            (nameof(PlayniteAchievementsSettings.EnableIntegrationCompactUnlocked), s => s.EnableIntegrationCompactUnlocked, (p, v) => p.EnableAchievementCompactUnlockedListControl = v),
            (nameof(PlayniteAchievementsSettings.EnableIntegrationCompactLocked), s => s.EnableIntegrationCompactLocked, (p, v) => p.EnableAchievementCompactLockedListControl = v),
            (nameof(PlayniteAchievementsSettings.EnableIntegrationList), s => s.EnableIntegrationList, (p, v) => p.EnableAchievementDataGridControl = v),
            (nameof(PlayniteAchievementsSettings.EnableIntegrationUserStats), s => s.EnableIntegrationUserStats, (p, v) => p.EnableAchievementStatsControl = v),
            (nameof(PlayniteAchievementsSettings.EnableIntegrationChart), s => s.EnableIntegrationChart, (p, v) => p.EnableAchievementBarChartControl = v),
        };

        [TestMethod]
        public void CompatFlags_DefaultTrue()
        {
            var settings = new PlayniteAchievementsSettings();

            foreach (var entry in FlagMap)
            {
                Assert.IsTrue(entry.ReadFlag(settings), entry.CompatFlag);
            }
        }

        [TestMethod]
        public void CompatFlags_MirrorPersistedToggles()
        {
            var settings = new PlayniteAchievementsSettings();

            foreach (var entry in FlagMap)
            {
                entry.SetPersisted(settings.Persisted, false);
                Assert.IsFalse(entry.ReadFlag(settings), entry.CompatFlag);

                entry.SetPersisted(settings.Persisted, true);
                Assert.IsTrue(entry.ReadFlag(settings), entry.CompatFlag);
            }
        }

        [TestMethod]
        public void CompatFlags_PersistedToggleRaisesCompatPropertyChanged()
        {
            var settings = new PlayniteAchievementsSettings();
            var raised = new List<string>();
            settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            foreach (var entry in FlagMap)
            {
                raised.Clear();
                entry.SetPersisted(settings.Persisted, false);
                Assert.IsTrue(raised.Contains(entry.CompatFlag), entry.CompatFlag);
            }
        }

        [TestMethod]
        public void CompatFlags_CopyPersistedFromRaisesCompatPropertyChanged()
        {
            var settings = new PlayniteAchievementsSettings();
            var other = new PlayniteAchievementsSettings();
            other.Persisted.EnableAchievementButtonControl = false;

            var raised = new List<string>();
            settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            settings.CopyPersistedFrom(other);

            Assert.IsFalse(settings.EnableIntegrationButton);
            foreach (var compatFlag in FlagMap.Select(entry => entry.CompatFlag))
            {
                Assert.IsTrue(raised.Contains(compatFlag), compatFlag);
            }
        }
    }
}
