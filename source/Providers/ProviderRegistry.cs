using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Epic;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.GOG;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.PSN;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Providers.Xbox;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// Central registry for provider enabled state at runtime.
    /// Decouples the "enabled" concept from provider authentication checks,
    /// allowing IsAuthenticated to only validate credentials.
    /// Also manages provider settings view registration for dynamic UI building.
    /// </summary>
    public class ProviderRegistry
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, Func<CancellationToken, Task>> _authPrimers = new Dictionary<string, Func<CancellationToken, Task>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Func<IProviderSettingsView>> _settingsViewFactories = new Dictionary<string, Func<IProviderSettingsView>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Preferred display order for provider tabs.
        /// </summary>
        private static readonly string[] PreferredOrder =
        {
            "Steam",
            "RetroAchievements",
            "GOG",
            "Epic",
            "PSN",
            "Xbox",
            "Exophase",
            "ShadPS4",
            "RPCS3",
            "Xenia",
            "Manual"
        };

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
            { "Xenia", true },
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
            _enabledState["Xenia"] = settings.XeniaEnabled;
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
            settings.XeniaEnabled = IsProviderEnabled("Xenia");
            settings.ManualEnabled = IsProviderEnabled("Manual");
            settings.ExophaseEnabled = IsProviderEnabled("Exophase");
        }

        // ===================== SETTINGS VIEW REGISTRATION =====================

        /// <summary>
        /// Registers a settings view factory for a provider.
        /// </summary>
        /// <param name="providerKey">The provider's unique key.</param>
        /// <param name="viewFactory">Factory function that creates the settings view.</param>
        public void RegisterSettingsView(string providerKey, Func<IProviderSettingsView> viewFactory)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                throw new ArgumentNullException(nameof(providerKey));
            }

            _settingsViewFactories[providerKey] = viewFactory ?? throw new ArgumentNullException(nameof(viewFactory));
        }

        /// <summary>
        /// Gets all registered provider keys that have settings views, in display order.
        /// </summary>
        public IEnumerable<string> GetSettingsViewProviderKeys()
        {
            return PreferredOrder
                .Where(k => _settingsViewFactories.ContainsKey(k))
                .Concat(_settingsViewFactories.Keys.Except(PreferredOrder, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Creates the settings view for a provider.
        /// </summary>
        /// <param name="providerKey">The provider's unique key.</param>
        /// <returns>The settings view, or null if not registered.</returns>
        public IProviderSettingsView CreateSettingsView(string providerKey)
        {
            return _settingsViewFactories.TryGetValue(providerKey, out var factory)
                ? factory()
                : null;
        }

        /// <summary>
        /// Checks if a provider has a registered settings view.
        /// </summary>
        public bool HasSettingsView(string providerKey)
        {
            return _settingsViewFactories.ContainsKey(providerKey);
        }

        // ===================== PROVIDER CREATION =====================

        /// <summary>
        /// Creates all data providers and registers their auth primers and settings views.
        /// This is the single entry point for provider initialization.
        /// </summary>
        public List<IDataProvider> CreateProviders(
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath,
            SteamSessionManager steamSessionManager,
            GogSessionManager gogSessionManager,
            EpicSessionManager epicSessionManager,
            PsnSessionManager psnSessionManager,
            XboxSessionManager xboxSessionManager,
            ExophaseSessionManager exophaseSessionManager,
            out ManualAchievementsProvider manualProvider)
        {
            manualProvider = new ManualAchievementsProvider(
                _logger,
                settings,
                pluginUserDataPath,
                playniteApi,
                exophaseSessionManager);

            var providers = new List<IDataProvider>
            {
                manualProvider,
                new ExophaseDataProvider(_logger, settings, playniteApi, exophaseSessionManager),
                new SteamDataProvider(_logger, settings, playniteApi, steamSessionManager, pluginUserDataPath),
                new GogDataProvider(_logger, settings, playniteApi, pluginUserDataPath, gogSessionManager),
                new EpicDataProvider(_logger, settings, playniteApi, epicSessionManager),
                new PsnDataProvider(_logger, settings, psnSessionManager),
                new XboxDataProvider(_logger, settings, xboxSessionManager),
                new RetroAchievementsDataProvider(_logger, settings, playniteApi, pluginUserDataPath),
                new ShadPS4DataProvider(_logger, settings, playniteApi),
                new Rpcs3DataProvider(_logger, settings, playniteApi),
                new XeniaDataProvider(_logger, settings, playniteApi, pluginUserDataPath)
            };

            // Register auth primers
            RegisterAuthPrimer("Steam", steamSessionManager.PrimeAuthenticationStateAsync);
            RegisterAuthPrimer("GOG", gogSessionManager.PrimeAuthenticationStateAsync);
            RegisterAuthPrimer("Epic", epicSessionManager.PrimeAuthenticationStateAsync);
            RegisterAuthPrimer("PSN", psnSessionManager.PrimeAuthenticationStateAsync);
            RegisterAuthPrimer("Xbox", xboxSessionManager.PrimeAuthenticationStateAsync);
            RegisterAuthPrimer("Exophase", exophaseSessionManager.PrimeAuthenticationStateAsync);

            // Register settings views
            RegisterSettingsViews(
                steamSessionManager,
                gogSessionManager,
                epicSessionManager,
                psnSessionManager,
                xboxSessionManager,
                exophaseSessionManager);

            return providers;
        }

        /// <summary>
        /// Registers settings views for providers that have been migrated to the modular system.
        /// </summary>
        private void RegisterSettingsViews(
            SteamSessionManager steamSessionManager,
            GogSessionManager gogSessionManager,
            EpicSessionManager epicSessionManager,
            PsnSessionManager psnSessionManager,
            XboxSessionManager xboxSessionManager,
            ExophaseSessionManager exophaseSessionManager)
        {
            // Steam settings view (migrated)
            RegisterSettingsView("Steam", () => new SteamSettingsView(steamSessionManager));

            // RetroAchievements settings view (migrated)
            RegisterSettingsView("RetroAchievements", () => new RetroAchievementsSettingsView());

            // GOG settings view (migrated)
            RegisterSettingsView("GOG", () => new GogSettingsView(gogSessionManager));

            // Epic settings view (migrated)
            RegisterSettingsView("Epic", () => new EpicSettingsView(epicSessionManager));

            // PSN settings view (migrated)
            RegisterSettingsView("PSN", () => new PsnSettingsView(psnSessionManager));

            // Xbox settings view (migrated)
            RegisterSettingsView("Xbox", () => new XboxSettingsView(xboxSessionManager));

            // Exophase settings view (migrated)
            RegisterSettingsView("Exophase", () => new ExophaseSettingsView(exophaseSessionManager));

            // ShadPS4 settings view (migrated)
            RegisterSettingsView("ShadPS4", () => new ShadPS4SettingsView());

            // RPCS3 settings view (migrated)
            RegisterSettingsView("RPCS3", () => new Rpcs3SettingsView());

            // Xenia settings view (migrated)
            RegisterSettingsView("Xenia", () => new XeniaSettingsView());

            // Manual settings view (migrated)
            RegisterSettingsView("Manual", () => new ManualSettingsView());
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