using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.RetroAchievements.EmulatorLog
{
    /// <summary>
    /// Settings-row view model for one supported emulator. Displays the auto-detected default log path
    /// and lets the user set an override, which is written straight back into the provider settings
    /// override dictionary (reassigned so the three-way settings merge persists the change).
    /// </summary>
    internal sealed class RaEmulatorLogRowViewModel : PlayniteAchievements.Common.ObservableObject
    {
        private readonly RetroAchievementsSettings _settings;
        private readonly string _key;
        private string _overridePath;

        public RaEmulatorLogRowViewModel(
            RetroAchievementsSettings settings,
            string key,
            string displayName,
            string defaultPathDisplay)
        {
            _settings = settings;
            _key = key;
            DisplayName = displayName;
            DefaultPathDisplay = defaultPathDisplay;

            if (settings?.EmulatorLogPathOverrides != null &&
                settings.EmulatorLogPathOverrides.TryGetValue(key, out var existing))
            {
                _overridePath = existing;
            }
        }

        public string DisplayName { get; }

        public string DefaultPathDisplay { get; }

        public string OverridePath
        {
            get => _overridePath;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (SetValueAndReturn(ref _overridePath, normalized))
                {
                    Persist(normalized);
                }
            }
        }

        private void Persist(string normalized)
        {
            if (_settings == null)
            {
                return;
            }

            var updated = new Dictionary<string, string>(
                _settings.EmulatorLogPathOverrides ??
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(normalized))
            {
                updated.Remove(_key);
            }
            else
            {
                updated[_key] = normalized;
            }

            _settings.EmulatorLogPathOverrides = updated;
        }
    }
}
