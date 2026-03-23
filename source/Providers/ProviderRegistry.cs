using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// Central registry for provider management at runtime.
    /// </summary>
    public class ProviderRegistry
    {
        private static readonly string[] ProviderDisplayOrder =
        {
            "Steam", "Epic", "GOG", "PSN", "Xbox", "Exophase",
            "RetroAchievements", "Manual", "Xenia", "RPCS3", "ShadPS4"
        };

        private static ProviderRegistry _instance;
        public static ProviderRegistry Instance => _instance;

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Dictionary<string, ProviderSettingsBase> _settingsCache = new Dictionary<string, ProviderSettingsBase>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IDataProvider> _providersByKey = new Dictionary<string, IDataProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Func<CancellationToken, Task>> _authPrimers = new Dictionary<string, Func<CancellationToken, Task>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Func<ProviderSettingsViewBase>> _settingsViewFactories = new Dictionary<string, Func<ProviderSettingsViewBase>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Type, object> _sessionManagers = new Dictionary<Type, object>();
        private readonly Dictionary<string, bool> _enabledState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private ManualAchievementsProvider _manualProvider;

        public ProviderRegistry(PlayniteAchievementsSettings settings, ILogger logger = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            _instance = this;
        }

        public ManualAchievementsProvider ManualProvider => _manualProvider;

        // ===================== SESSION MANAGERS =====================

        public void RegisterSessionManager<T>(T sessionManager) where T : class
        {
            _sessionManagers[typeof(T)] = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));

            // Auto-register auth primer if session manager has PrimeAuthenticationStateAsync method
            var primeMethod = typeof(T).GetMethod("PrimeAuthenticationStateAsync");
            if (primeMethod != null)
            {
                var providerKey = ExtractProviderKey(typeof(T));
                if (providerKey != null)
                {
                    _authPrimers[providerKey] = ct => (Task)primeMethod.Invoke(sessionManager, new object[] { ct });
                }
            }
        }

        private static string ExtractProviderKey(Type sessionManagerType)
        {
            // Convention: SteamSessionManager -> Steam, GogSessionManager -> GOG, etc.
            var name = sessionManagerType.Name;
            if (name.EndsWith("SessionManager"))
                return name.Substring(0, name.Length - "SessionManager".Length);
            return null;
        }

        // ===================== SETTINGS ACCESS =====================

        public void Save<T>(T settings) where T : ProviderSettingsBase
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _settingsCache[settings.ProviderKey] = settings;
            _enabledState[settings.ProviderKey] = settings.IsEnabled;
            SaveToPersisted(settings);
            _settings._plugin?.SavePluginSettings(_settings);

            if (_providersByKey.TryGetValue(settings.ProviderKey, out var provider))
                provider.ApplySettings(settings);
        }

        public T GetSettings<T>() where T : ProviderSettingsBase, new()
        {
            var temp = new T();
            var key = temp.ProviderKey;

            if (_settingsCache.TryGetValue(key, out var cached) && cached is T typed)
                return typed;

            var settings = LoadFromPersisted<T>(key);
            _settingsCache[key] = settings;
            return settings;
        }

        public static T Settings<T>() where T : ProviderSettingsBase, new()
            => Instance?.GetSettings<T>() ?? new T();

        public static void Write(ProviderSettingsBase settings)
            => Instance?.Save(settings);

        // ===================== PERSISTENCE =====================

        public void PersistAllProviderSettings(bool persistToDisk = true)
        {
            foreach (var providerKey in _providersByKey.Keys.ToList())
            {
                if (!_providersByKey.TryGetValue(providerKey, out var provider)) continue;
                if (!(provider.GetSettings() is ProviderSettingsBase settings)) continue;

                settings.IsEnabled = IsProviderEnabled(providerKey);
                _settingsCache[providerKey] = settings;
                SaveToPersisted(settings);
            }

            if (persistToDisk)
                _settings._plugin?.SavePluginSettings(_settings);
        }

        private T LoadFromPersisted<T>(string providerKey) where T : ProviderSettingsBase, new()
        {
            var settings = new T();
            if (_settings.Persisted?.ProviderSettings?.TryGetValue(providerKey, out var jsonObj) == true && jsonObj != null)
                settings.DeserializeFromJson(jsonObj.ToString());
            return settings;
        }

        private void SaveToPersisted(ProviderSettingsBase settings)
        {
            if (_settings.Persisted?.ProviderSettings != null)
                _settings.Persisted.ProviderSettings[settings.ProviderKey] = JObject.Parse(settings.SerializeToJson());
        }

        // ===================== LOCALIZATION =====================

        public static string GetLocalizedName(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey)) return "Unknown";
            var value = ResourceProvider.GetString($"LOCPlayAch_Provider_{providerKey}");
            return string.IsNullOrWhiteSpace(value) ? providerKey : value;
        }

        // ===================== AUTH PRIMING =====================

        public void RegisterAuthPrimer(string providerKey, Func<CancellationToken, Task> primeAsync)
        {
            if (!string.IsNullOrWhiteSpace(providerKey) && primeAsync != null)
                _authPrimers[providerKey] = primeAsync;
        }

        public async Task PrimeEnabledProvidersAsync()
        {
            foreach (var kvp in _authPrimers)
            {
                if (!IsProviderEnabled(kvp.Key)) continue;
                try { await kvp.Value(default); }
                catch (Exception ex) { _logger?.Warn(ex, $"Failed to prime {kvp.Key} authentication state"); }
            }
        }

        // ===================== ENABLED STATE =====================

        public event EventHandler<ProviderEnabledChangedEventArgs> ProviderEnabledChanged;

        public bool IsProviderEnabled(string providerKey)
            => string.IsNullOrWhiteSpace(providerKey) || !_enabledState.TryGetValue(providerKey, out var enabled) || enabled;

        public void SetProviderEnabled(string providerKey, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(providerKey)) return;

            var previousState = IsProviderEnabled(providerKey);
            _enabledState[providerKey] = enabled;

            if (previousState != enabled)
                ProviderEnabledChanged?.Invoke(this, new ProviderEnabledChangedEventArgs(providerKey, enabled));
        }

        public void SyncFromSettings(PersistedSettings settings)
        {
            if (settings == null) return;

            foreach (var kvp in settings.ProviderSettings)
            {
                if (kvp.Value == null) continue;
                try { _enabledState[kvp.Key] = kvp.Value["IsEnabled"]?.Value<bool>() ?? true; }
                catch { _enabledState[kvp.Key] = true; }
            }
        }

        public void SyncToSettings(PersistedSettings settings)
        {
            if (settings == null) return;

            foreach (var key in _enabledState.Keys.ToList())
            {
                var isEnabled = _enabledState[key];
                if (settings.ProviderSettings.TryGetValue(key, out var jsonObj) && jsonObj != null)
                    jsonObj["IsEnabled"] = isEnabled;
                else
                    settings.ProviderSettings[key] = new JObject { ["IsEnabled"] = isEnabled };
            }
        }

        // ===================== SETTINGS VIEWS =====================

        public IEnumerable<string> GetSettingsViewProviderKeys() => _settingsViewFactories.Keys;

        public ProviderSettingsViewBase CreateSettingsView(string providerKey)
            => _settingsViewFactories.TryGetValue(providerKey, out var factory) ? factory() : null;

        // ===================== PROVIDER CREATION =====================

        public List<IDataProvider> CreateProviders(PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            var providers = new List<IDataProvider>();

            foreach (var providerType in DiscoverProviderTypes())
            {
                try
                {
                    var provider = CreateProviderInstance(providerType, settings, playniteApi, pluginUserDataPath);
                    if (provider != null)
                    {
                        if (provider is ManualAchievementsProvider manual)
                            _manualProvider = manual;
                        providers.Add(provider);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to create provider: {providerType.Name}");
                }
            }

            var sorted = providers
                .OrderBy(p => Array.IndexOf(ProviderDisplayOrder, p.ProviderKey) is var idx && idx >= 0 ? idx : int.MaxValue)
                .ThenBy(p => p.ProviderKey)
                .ToList();

            foreach (var provider in sorted)
                RegisterProviderInternals(provider);

            return sorted;
        }

        private IEnumerable<Type> DiscoverProviderTypes()
            => GetType().Assembly.GetTypes()
                .Where(t => typeof(IDataProvider).IsAssignableFrom(t) && !t.IsAbstract && t.Name.EndsWith("DataProvider"));

        private IDataProvider CreateProviderInstance(Type providerType, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            var ctors = providerType.GetConstructors();
            if (ctors.Length == 0) { _logger?.Warn($"No public constructor for {providerType.Name}"); return null; }

            foreach (var ctor in ctors.OrderByDescending(c => c.GetParameters().Length))
            {
                var args = ResolveConstructorArguments(ctor, settings, playniteApi, pluginUserDataPath);
                if (args != null)
                {
                    try { return (IDataProvider)ctor.Invoke(args); }
                    catch (Exception ex) { _logger?.Error(ex, $"Failed to invoke constructor for {providerType.Name}"); }
                }
            }

            _logger?.Warn($"Could not resolve constructor for {providerType.Name}");
            return null;
        }

        private object[] ResolveConstructorArguments(System.Reflection.ConstructorInfo ctor, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            var parameters = ctor.GetParameters();
            var args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var paramType = param.ParameterType;

                if (paramType == typeof(ILogger)) args[i] = _logger;
                else if (paramType == typeof(PlayniteAchievementsSettings)) args[i] = settings;
                else if (paramType == typeof(IPlayniteAPI)) args[i] = playniteApi;
                else if (paramType == typeof(string) && param.Name?.ToLower().Contains("path") == true) args[i] = pluginUserDataPath;
                else if (_sessionManagers.TryGetValue(paramType, out var sessionManager)) args[i] = sessionManager;
                else return null;
            }

            return args;
        }

        private void RegisterProviderInternals(IDataProvider provider)
        {
            var key = provider.ProviderKey;
            _providersByKey[key] = provider;

            if (!_enabledState.ContainsKey(key))
                _enabledState[key] = true;

            if (provider.GetSettings() is ProviderSettingsBase providerSettings)
            {
                providerSettings.IsEnabled = _enabledState[key];
                _settingsCache[key] = providerSettings;
            }

            _settingsViewFactories[key] = () => provider.CreateSettingsView();
        }
    }

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
