using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.ViewModels.Showcase.Widgets;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// Saves showcase control bar state in the widget instance's own options, so it lives and
    /// dies with the widget, and the state of the grids outside the showcase in
    /// <see cref="PersistedSettings.ControlBarStates"/> by grid surface. Writes are quiet: the
    /// plugin settings file is saved without the settings-saved broadcast or a showcase
    /// configuration change, since a search or filter choice changes no layout. Typing is batched
    /// into one save after a short pause.
    /// </summary>
    public sealed class ShowcaseControlBarStateStore : IShowcaseControlBarStateStore
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(ShowcaseControlBarStateStore));
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

        private DispatcherTimer _saveTimer;
        private bool _savePending;

        public static ShowcaseControlBarStateStore Instance { get; } = new ShowcaseControlBarStateStore();

        private ShowcaseControlBarStateStore()
        {
        }

        public string Load(string instanceId, string key)
        {
            var options = FindInstance(instanceId)?.Options;
            return options != null && options.TryGetValue(key, out var state) ? state : null;
        }

        public void Save(string instanceId, string key, string state)
        {
            // Resolved at write time: the persisted settings object is replaced when a settings
            // edit is cancelled, so a captured instance could be an orphan.
            var instance = FindInstance(instanceId);
            if (instance == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (instance.Options == null)
            {
                instance.Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            if (instance.Options.TryGetValue(key, out var current) &&
                string.Equals(current, state, StringComparison.Ordinal))
            {
                return;
            }

            instance.Options[key] = state;
            ScheduleSave();
        }

        /// <summary>The saved state of a grid surface outside the showcase, or null.</summary>
        public string LoadSurface(string surfaceKey)
        {
            var states = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.ControlBarStates;
            return states != null && !string.IsNullOrWhiteSpace(surfaceKey) && states.TryGetValue(surfaceKey, out var state)
                ? state
                : null;
        }

        /// <summary>Saves a grid surface's state; a null or empty state removes it.</summary>
        public void SaveSurface(string surfaceKey, string state)
        {
            // Resolved at write time, like the widget instances.
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            if (persisted == null || string.IsNullOrWhiteSpace(surfaceKey))
            {
                return;
            }

            var states = persisted.ControlBarStates;
            if (string.IsNullOrEmpty(state))
            {
                if (states.Remove(surfaceKey))
                {
                    ScheduleSave();
                }

                return;
            }

            if (states.TryGetValue(surfaceKey, out var current) &&
                string.Equals(current, state, StringComparison.Ordinal))
            {
                return;
            }

            states[surfaceKey] = state;
            ScheduleSave();
        }

        /// <summary>
        /// Restores an achievement grid's Unlocked, Locked and Hidden toggles from the state saved
        /// under <paramref name="surfaceKey"/> and saves them there on every later change.
        /// </summary>
        public void PersistUnlockToggles(
            PlayniteAchievements.ViewModels.AchievementGridControlBarAdapter adapter,
            string surfaceKey)
        {
            adapter?.PersistUnlockToggles(LoadSurface(surfaceKey), state => SaveSurface(surfaceKey, state));
        }

        /// <summary>
        /// Restores a grid's Compare pick from the state saved for <paramref name="surfaceKey"/>
        /// and saves it there each time the user picks or clears one. <paramref name="persist"/>
        /// is the compare controller's PersistSelection.
        /// </summary>
        public void PersistCompareSelection(Action<string, Action<string>> persist, string surfaceKey)
        {
            persist?.Invoke(
                LoadCompareSelection(surfaceKey),
                state => SaveCompareSelection(surfaceKey, state));
        }

        /// <summary>The Compare pick saved for a grid surface, or null.</summary>
        public string LoadCompareSelection(string surfaceKey) => LoadSurface(surfaceKey + ".Compare");

        /// <summary>Saves a grid surface's Compare pick; null removes it.</summary>
        public void SaveCompareSelection(string surfaceKey, string state) =>
            SaveSurface(surfaceKey + ".Compare", state);

        /// <summary>Writes any batched change now.</summary>
        public void Flush()
        {
            _saveTimer?.Stop();
            if (!_savePending)
            {
                return;
            }

            _savePending = false;
            var plugin = PlayniteAchievementsPlugin.Instance;
            if (plugin?.Settings == null)
            {
                return;
            }

            try
            {
                plugin.SavePluginSettings(plugin.Settings);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to save showcase control bar state.");
            }
        }

        private void ScheduleSave()
        {
            _savePending = true;
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SaveDelay };
                _saveTimer.Tick += (_, __) => Flush();
            }

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private static ShowcaseWidgetInstanceSettings FindInstance(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return null;
            }

            // Start page widgets carry control bars too; their instances live in StartPageInstances.
            // So do the overview mini-showcase's timeline and calendar, and the Overview header's
            // two score cards, which are stored outside every widget list.
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            var showcase = persisted?.Showcase;
            return (showcase?.WidgetInstances ?? Enumerable.Empty<ShowcaseWidgetInstanceSettings>())
                .Concat(showcase?.StartPageInstances?.Values ?? Enumerable.Empty<ShowcaseWidgetInstanceSettings>())
                .Concat(persisted?.OverviewMiniShowcase?.WidgetInstances ?? Enumerable.Empty<ShowcaseWidgetInstanceSettings>())
                .Concat(new[] { persisted?.OverviewScoreCard1, persisted?.OverviewScoreCard2 })
                .FirstOrDefault(widget => string.Equals(widget?.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
