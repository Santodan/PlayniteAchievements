using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>Where a widget instance's control bar state is saved between Playnite sessions.</summary>
    public interface IShowcaseControlBarStateStore
    {
        /// <summary>The saved state under <paramref name="key"/> for the instance, or null.</summary>
        string Load(string instanceId, string key);

        void Save(string instanceId, string key, string state);
    }

    /// <summary>
    /// Control bar state per showcase widget instance. A widget swaps view models when its mode
    /// or source changes and when the dashboard is rebuilt; keeping the adapter here, keyed by
    /// instance ID, carries the search text and filter selections across every one of them, and
    /// across the bar being hidden. With a <see cref="Store"/> set, an adapter starts from the
    /// instance's saved state and saves every filter change, so the state also survives a
    /// restart. One adapter per type, so the Mosaic's achievement and game modes each keep their
    /// own filters.
    /// </summary>
    public static class ShowcaseControlBarStates
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Dictionary<Type, Entry>> ByInstance =
            new Dictionary<string, Dictionary<Type, Entry>>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public static IShowcaseControlBarStateStore Store { get; set; }

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
                if (!ByInstance.TryGetValue(instanceId, out var entries))
                {
                    entries = new Dictionary<Type, Entry>();
                    ByInstance[instanceId] = entries;
                }

                if (!entries.TryGetValue(typeof(TAdapter), out var entry))
                {
                    var adapter = new TAdapter();
                    adapter.RestoreState(Load(instanceId, adapter.StateKey));
                    entry = new Entry(adapter, () => Save(instanceId, adapter));
                    adapter.AddFilterListener(entry.SaveListener);
                    entries[typeof(TAdapter)] = entry;
                }

                return (TAdapter)entry.Adapter;
            }
        }

        private static ControlBarFilterState Load(string instanceId, string key)
        {
            var raw = Store?.Load(instanceId, key);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<ControlBarFilterState>(raw, JsonSettings);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void Save(string instanceId, SharedControlBarAdapter adapter)
        {
            Store?.Save(
                instanceId,
                adapter.StateKey,
                JsonConvert.SerializeObject(adapter.CaptureState(), JsonSettings));
        }

        // The adapter holds its listeners weakly, so the entry keeps the save listener alive.
        private sealed class Entry
        {
            public Entry(SharedControlBarAdapter adapter, Action saveListener)
            {
                Adapter = adapter;
                SaveListener = saveListener;
            }

            public SharedControlBarAdapter Adapter { get; }

            public Action SaveListener { get; }
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
