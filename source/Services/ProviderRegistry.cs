using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services
{
    /// <summary>
    /// Central registry for provider enabled state at runtime.
    /// Decouples the "enabled" concept from provider authentication checks,
    /// allowing IsAuthenticated to only validate credentials.
    /// </summary>
    public class ProviderRegistry
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, Func<CancellationToken, Task>> _authPrimers = new Dictionary<string, Func<CancellationToken, Task>>(StringComparer.OrdinalIgnoreCase);

        public ProviderRegistry(ILogger logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the localized display name for a provider key.
        /// Maps ProviderKey to the correct localization resource key.
        /// </summary>
        /// <param name="providerKey">The stable provider key (e.g., "PSN", "Steam").</param>
        /// <returns>Localized display name, or the key itself if no localization found.</returns>
        public static string GetLocalizedName(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
                return "Unknown";

            var locKey = GetLocalizationKey(providerKey);
            var value = ResourceProvider.GetString(locKey);
            return string.IsNullOrWhiteSpace(value) ? providerKey : value;
        }

        private static string GetLocalizationKey(string providerKey)
        {
            return $"LOCPlayAch_Provider_{providerKey}";
        }

        /// <summary>
        /// Registers an authentication priming function for a provider.
        /// Call this during plugin initialization for providers with web-based auth.
        /// </summary>
        public void RegisterAuthPrimer(string providerKey, Func<CancellationToken, Task> primeAsync)
        {
            if (!string.IsNullOrWhiteSpace(providerKey) && primeAsync != null)
            {
                _authPrimers[providerKey] = primeAsync;
            }
        }

        /// <summary>
        /// Primes authentication state for all enabled providers that have registered primers.
        /// </summary>
        public async Task PrimeEnabledProvidersAsync()
        {
            foreach (var kvp in _authPrimers)
            {
                if (!IsProviderEnabled(kvp.Key))
                {
                    continue;
                }

                try
                {
                    await kvp.Value(default);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to prime {kvp.Key} authentication state");
                }
            }
        }

        private readonly Dictionary<string, bool> _enabledState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            { "Steam", true },
            { "Epic", true },
            { "GOG", true },
            { "PSN", true },
            { "RetroAchievements", true },
            { "Xbox", true },
            { "ShadPS4", true },
            { "RPCS3", true },
            { "Manual", true },
            { "Exophase", true }
        };

        /// <summary>
        /// Event raised when a provider's enabled state changes.
        /// </summary>
        public event EventHandler<ProviderEnabledChangedEventArgs> ProviderEnabledChanged;

        /// <summary>
        /// Checks if a provider is enabled at runtime.
        /// </summary>
        /// <param name="providerKey">The provider key (e.g., "Steam", "Epic", "GOG", "RetroAchievements").</param>
        /// <returns>True if the provider is enabled, false otherwise. Defaults to true for unknown providers.</returns>
        public bool IsProviderEnabled(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return true;
            }

            return _enabledState.TryGetValue(providerKey, out var enabled) && enabled;
        }

        /// <summary>
        /// Sets the enabled state for a provider and raises the change event.
        /// </summary>
        /// <param name="providerKey">The provider key.</param>
        /// <param name="enabled">The enabled state.</param>
        public void SetProviderEnabled(string providerKey, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return;
            }

            var previousState = IsProviderEnabled(providerKey);
            _enabledState[providerKey] = enabled;

            if (previousState != enabled)
            {
                ProviderEnabledChanged?.Invoke(this, new ProviderEnabledChangedEventArgs(providerKey, enabled));
            }
        }

        /// <summary>
        /// Synchronizes the runtime enabled state from persisted settings.
        /// Call this after settings are loaded or saved.
        /// </summary>
        /// <param name="settings">The persisted settings containing provider enable flags.</param>
        public void SyncFromSettings(PersistedSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            _enabledState["Steam"] = settings.SteamEnabled;
            _enabledState["Epic"] = settings.EpicEnabled;
            _enabledState["GOG"] = settings.GogEnabled;
            _enabledState["PSN"] = settings.PsnEnabled;
            _enabledState["RetroAchievements"] = settings.RetroAchievementsEnabled;
            _enabledState["Xbox"] = settings.XboxEnabled;
            _enabledState["ShadPS4"] = settings.ShadPS4Enabled;
            _enabledState["RPCS3"] = settings.Rpcs3Enabled;
            _enabledState["Manual"] = settings.ManualEnabled;
            _enabledState["Exophase"] = settings.ExophaseEnabled;
        }

        /// <summary>
        /// Writes the runtime enabled state back to persisted settings.
        /// Call this when toggling providers from the landing page.
        /// </summary>
        /// <param name="settings">The persisted settings to update.</param>
        public void SyncToSettings(PersistedSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            settings.SteamEnabled = IsProviderEnabled("Steam");
            settings.EpicEnabled = IsProviderEnabled("Epic");
            settings.GogEnabled = IsProviderEnabled("GOG");
            settings.PsnEnabled = IsProviderEnabled("PSN");
            settings.RetroAchievementsEnabled = IsProviderEnabled("RetroAchievements");
            settings.XboxEnabled = IsProviderEnabled("Xbox");
            settings.ShadPS4Enabled = IsProviderEnabled("ShadPS4");
            settings.Rpcs3Enabled = IsProviderEnabled("RPCS3");
            settings.ManualEnabled = IsProviderEnabled("Manual");
            settings.ExophaseEnabled = IsProviderEnabled("Exophase");
        }
    }

    /// <summary>
    /// Event arguments for provider enabled state changes.
    /// </summary>
    public class ProviderEnabledChangedEventArgs : EventArgs
    {
        public string ProviderKey { get; }
        public bool IsEnabled { get; }

        public ProviderEnabledChangedEventArgs(string providerKey, bool isEnabled)
        {
            ProviderKey = providerKey;
            IsEnabled = isEnabled;
        }
    }
}
