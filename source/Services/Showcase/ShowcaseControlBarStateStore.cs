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
    /// dies with the widget. Writes are quiet: the plugin settings file is saved without the
    /// settings-saved broadcast or a showcase configuration change, since a search or filter
    /// choice changes no layout. Typing is batched into one save after a short pause.
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

            return PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase?.WidgetInstances?
                .FirstOrDefault(widget => string.Equals(widget?.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
