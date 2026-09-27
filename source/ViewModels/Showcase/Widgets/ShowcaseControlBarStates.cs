using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Control bar state per showcase widget instance for the Playnite session. A widget swaps
    /// view models when its mode or source changes and when the dashboard is rebuilt; keeping
    /// the adapter here, keyed by instance ID, carries the search text and filter selections
    /// across every one of them, and across the bar being hidden. One adapter per type, so the
    /// Mosaic's achievement and game modes each keep their own filters.
    /// </summary>
    public static class ShowcaseControlBarStates
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Dictionary<Type, SharedControlBarAdapter>> ByInstance =
            new Dictionary<string, Dictionary<Type, SharedControlBarAdapter>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The instance's adapter of this type; a fresh unshared one when there is no instance ID.</summary>
        public static TAdapter Get<TAdapter>(string instanceId)
            where TAdapter : SharedControlBarAdapter, new()
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return new TAdapter();
            }

            lock (Sync)
            {
                if (!ByInstance.TryGetValue(instanceId, out var adapters))
                {
                    adapters = new Dictionary<Type, SharedControlBarAdapter>();
                    ByInstance[instanceId] = adapters;
                }

                if (!adapters.TryGetValue(typeof(TAdapter), out var adapter))
                {
                    adapter = new TAdapter();
                    adapters[typeof(TAdapter)] = adapter;
                }

                return (TAdapter)adapter;
            }
        }

        /// <summary>Drops the state of every instance not in <paramref name="liveInstanceIds"/>.</summary>
        public static void RemoveExcept(ISet<string> liveInstanceIds)
        {
            lock (Sync)
            {
                foreach (var stale in ByInstance.Keys
                    .Where(id => liveInstanceIds == null || !liveInstanceIds.Contains(id))
                    .ToList())
                {
                    ByInstance.Remove(stale);
                }
            }
        }
    }

    /// <summary>
    /// A view model's link to its widget instance's shared control bar adapter. It holds the
    /// filter listener strongly (the adapter holds it weakly), so the listener lives exactly as
    /// long as the view model.
    /// </summary>
    public sealed class ShowcaseControlBarSlot<TAdapter>
        where TAdapter : SharedControlBarAdapter, new()
    {
        private readonly Action _onFilterChanged;
        private string _instanceId;

        public ShowcaseControlBarSlot(Action onFilterChanged)
        {
            _onFilterChanged = onFilterChanged;
        }

        public TAdapter Adapter { get; private set; }

        /// <summary>Links to the instance's adapter; true when the adapter changed.</summary>
        public bool Bind(string instanceId)
        {
            if (Adapter != null && string.Equals(_instanceId, instanceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Adapter?.RemoveFilterListener(_onFilterChanged);
            _instanceId = instanceId;
            Adapter = ShowcaseControlBarStates.Get<TAdapter>(instanceId);
            Adapter.AddFilterListener(_onFilterChanged);
            return true;
        }
    }
}
