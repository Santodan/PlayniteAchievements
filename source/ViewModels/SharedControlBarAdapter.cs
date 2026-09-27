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
}
