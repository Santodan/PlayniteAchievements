using System;
using System.Collections.Generic;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Base for control bar adapters whose filter state outlives the view models showing it: a
    /// showcase widget keeps one adapter across every view model it swaps through. Listeners are
    /// held weakly, so a discarded view model is not kept alive by the adapter; each listener's
    /// owner keeps the delegate itself alive for as long as it wants the notifications.
    /// </summary>
    public abstract class SharedControlBarAdapter : PlayniteAchievements.Common.ObservableObject
    {
        private readonly List<WeakReference<Action>> _filterListeners = new List<WeakReference<Action>>();

        public abstract GridControlBarViewModel ControlBar { get; }

        /// <summary>Names this adapter's saved state among a widget's options; one per adapter type.</summary>
        public abstract string StateKey { get; }

        /// <summary>The current search text and filter selections, in a form that can be saved.</summary>
        public abstract ControlBarFilterState CaptureState();

        /// <summary>
        /// Restores saved state without raising a filter change. Platform selections wait for the
        /// next options rebuild, since the dropdown's groups are built from the games.
        /// </summary>
        public abstract void RestoreState(ControlBarFilterState state);

        /// <summary>Restored platform selections not yet folded into a rebuild of the groups.</summary>
        protected Dictionary<string, List<string>> PendingPlatformSelections { get; set; }

        protected Dictionary<string, List<string>> CapturePlatformSelections(IEnumerable<ProviderFilterGroup> groups)
        {
            var result = PendingPlatformSelections != null
                ? new Dictionary<string, List<string>>(PendingPlatformSelections, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups ?? new List<ProviderFilterGroup>())
            {
                if (group == null || string.IsNullOrWhiteSpace(group.ProviderKey))
                {
                    continue;
                }

                var selected = new List<string>(group.SelectedPlatformNames);
                if (selected.Count > 0)
                {
                    result[group.ProviderKey] = selected;
                }
                else
                {
                    result.Remove(group.ProviderKey);
                }
            }

            return result;
        }

        /// <summary>Adds a filter-change listener. The caller must hold a strong reference to it.</summary>
        public void AddFilterListener(Action listener)
        {
            if (listener == null)
            {
                return;
            }

            _filterListeners.RemoveAll(entry => !entry.TryGetTarget(out var alive) || alive == listener);
            _filterListeners.Add(new WeakReference<Action>(listener));
        }

        public void RemoveFilterListener(Action listener)
        {
            _filterListeners.RemoveAll(entry => !entry.TryGetTarget(out var alive) || alive == listener);
        }

        protected void RaiseFilterChanged()
        {
            var live = new List<Action>(_filterListeners.Count);
            _filterListeners.RemoveAll(entry =>
            {
                if (entry.TryGetTarget(out var listener))
                {
                    live.Add(listener);
                    return false;
                }

                return true;
            });

            foreach (var listener in live)
            {
                listener();
            }
        }
    }

    /// <summary>
    /// A control bar's saved state. Platforms map a provider key to its selected platform names.
    /// Progress and Activity hold option positions rather than labels, so the saved choice
    /// survives a change of Playnite language.
    /// </summary>
    public sealed class ControlBarFilterState
    {
        public string SearchText { get; set; }

        public Dictionary<string, List<string>> Platforms { get; set; }

        public List<int> Progress { get; set; }

        public List<int> Activity { get; set; }
    }
}
