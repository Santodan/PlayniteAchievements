using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// Central registry for provider management at runtime.
    /// </summary>
    public class ProviderRegistry
    {
        private static ProviderRegistry _instance;
        public static ProviderRegistry Instance => _instance;

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Dictionary<string, int> _displayOrderIndex;
        private readonly Dictionary<string, ProviderSettingsBase> _settingsCache = new Dictionary<string, ProviderSettingsBase>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IDataProvider> _providersByKey = new Dictionary<string, IDataProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Func<ProviderSettingsViewBase>> _settingsViewFactories = new Dictionary<string, Func<ProviderSettingsViewBase>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _enabledState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProviderSettingsBase> _editSessionOriginals = new Dictionary<string, ProviderSettingsBase>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProviderSettingsBase> _editSessionCopies = new Dictionary<string, ProviderSettingsBase>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Type, object> _sharedServices = new Dictionary<Type, object>();
        private bool _editSessionActive;

        public ProviderRegistry(
            PlayniteAchievementsSettings settings,
            IEnumerable<string> displayOrder,
            ILogger logger = null,
            params object[] sharedServices)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            _displayOrderIndex = BuildOrderIndex(displayOrder);
            RegisterSharedServices(sharedServices);
            _instance = this;
        }

        // ===================== SETTINGS ACCESS =====================

        public void Save<T>(T settings) where T : ProviderSettingsBase
            => Save(settings, persistToDisk: false);

        public void Save<T>(T settings, bool persistToDisk) where T : ProviderSettingsBase
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _settingsCache[settings.ProviderKey] = settings;
            _enabledState[settings.ProviderKey] = settings.IsEnabled;
            SaveToPersisted(settings);
            if (persistToDisk)
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
            => Instance?.Save(settings, persistToDisk: false);

        public static void Write(ProviderSettingsBase settings, bool persistToDisk)
            => Instance?.Save(settings, persistToDisk);

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

        public void BeginEditSession()
        {
            if (_editSessionActive)
                return;

            _editSessionOriginals.Clear();
            _editSessionCopies.Clear();
            _editSessionActive = true;
        }

        public void CancelEditSession()
        {
            _editSessionOriginals.Clear();
            _editSessionCopies.Clear();
            _editSessionActive = false;
        }

        public void CommitEditSession(bool persistToDisk = false)
        {
            if (!_editSessionActive)
            {
                if (persistToDisk)
                    _settings._plugin?.SavePluginSettings(_settings);
                return;
            }

            foreach (var providerKey in _editSessionCopies.Keys.ToList())
            {
                if (!_editSessionCopies.TryGetValue(providerKey, out var edited) || edited == null)
                    continue;

                _editSessionOriginals.TryGetValue(providerKey, out var original);
                var live = GetLiveSettings(providerKey);
                if (live == null)
                    continue;

                var merged = MergeProviderSettings(original, edited, live);
                live.CopyFrom(merged);
                _settingsCache[providerKey] = live;
                _enabledState[providerKey] = live.IsEnabled;
                SaveToPersisted(live);

                if (_providersByKey.TryGetValue(providerKey, out var provider))
                    provider.ApplySettings(live);
            }

            CancelEditSession();

            if (persistToDisk)
                _settings._plugin?.SavePluginSettings(_settings);
        }

        public IProviderSettings GetSettingsForEdit(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
                return null;

            if (!_editSessionActive)
                BeginEditSession();

            if (_editSessionCopies.TryGetValue(providerKey, out var existingCopy))
                return existingCopy;

            var live = GetLiveSettings(providerKey);
            if (live == null)
                return null;

            var original = live.Clone() as ProviderSettingsBase;
            var copy = live.Clone() as ProviderSettingsBase;
            if (original == null || copy == null)
                return live;

            _editSessionOriginals[providerKey] = original;
            _editSessionCopies[providerKey] = copy;
            return copy;
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

        public IEnumerable<string> GetSettingsViewProviderKeys()
            => OrderProviderKeys(_settingsViewFactories.Keys);

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
                        providers.Add(provider);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to create provider: {providerType.Name}");
                }
            }

            var sorted = providers
                .OrderBy(p => GetDisplayOrderIndex(p?.ProviderKey))
                .ThenBy(p => p.ProviderKey)
                .ToList();

            foreach (var provider in sorted)
                RegisterProviderInternals(provider);

            return sorted;
        }

        private IEnumerable<Type> DiscoverProviderTypes()
            => GetType().Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && typeof(IDataProvider).IsAssignableFrom(t));

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
                else if (TryResolveSharedService(paramType, out var sharedService)) args[i] = sharedService;
                else return null;
            }

            return args;
        }

        private void RegisterSharedServices(IEnumerable<object> sharedServices)
        {
            if (sharedServices == null)
            {
                return;
            }

            foreach (var service in sharedServices.Where(service => service != null))
            {
                _sharedServices[service.GetType()] = service;
            }
        }

        private bool TryResolveSharedService(Type paramType, out object service)
        {
            if (paramType != null)
            {
                if (_sharedServices.TryGetValue(paramType, out service))
                {
                    return true;
                }

                foreach (var candidate in _sharedServices.Values)
                {
                    if (paramType.IsInstanceOfType(candidate))
                    {
                        service = candidate;
                        return true;
                    }
                }
            }

            service = null;
            return false;
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

        private ProviderSettingsBase GetLiveSettings(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
                return null;

            if (_settingsCache.TryGetValue(providerKey, out var cached))
                return cached;

            if (_providersByKey.TryGetValue(providerKey, out var provider) &&
                provider?.GetSettings() is ProviderSettingsBase providerSettings)
            {
                _settingsCache[providerKey] = providerSettings;
                return providerSettings;
            }

            return null;
        }

        private IEnumerable<string> OrderProviderKeys(IEnumerable<string> providerKeys)
        {
            return (providerKeys ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(GetDisplayOrderIndex)
                .ThenBy(key => key, StringComparer.OrdinalIgnoreCase);
        }

        private int GetDisplayOrderIndex(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return int.MaxValue;
            }

            return _displayOrderIndex.TryGetValue(providerKey, out var index) ? index : int.MaxValue;
        }

        private static Dictionary<string, int> BuildOrderIndex(IEnumerable<string> providerOrder)
        {
            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (providerOrder == null)
            {
                return orderIndex;
            }

            var index = 0;
            foreach (var providerKey in providerOrder.Where(key => !string.IsNullOrWhiteSpace(key)))
            {
                if (!orderIndex.ContainsKey(providerKey))
                {
                    orderIndex[providerKey] = index++;
                }
            }

            return orderIndex;
        }

        private static ProviderSettingsBase MergeProviderSettings(
            ProviderSettingsBase original,
            ProviderSettingsBase edited,
            ProviderSettingsBase live)
        {
            if (edited == null)
                return live;

            if (original == null || live == null)
                return edited.Clone() as ProviderSettingsBase ?? edited;

            var originalJson = JObject.Parse(original.SerializeToJson());
            var editedJson = JObject.Parse(edited.SerializeToJson());
            var liveJson = JObject.Parse(live.SerializeToJson());
            var mergedJson = MergeTokens(originalJson, editedJson, liveJson) as JObject ?? editedJson;

            var merged = live.Clone() as ProviderSettingsBase ?? edited.Clone() as ProviderSettingsBase;
            if (merged == null)
                return edited;

            merged.DeserializeFromJson(mergedJson.ToString());
            return merged;
        }

        private static JToken MergeTokens(JToken original, JToken edited, JToken live)
        {
            if (JToken.DeepEquals(edited, original))
                return live?.DeepClone() ?? original?.DeepClone();

            if (JToken.DeepEquals(live, original))
                return edited?.DeepClone();

            if (original is JObject originalObject &&
                edited is JObject editedObject &&
                live is JObject liveObject)
            {
                var merged = new JObject();
                var propertyNames = originalObject.Properties().Select(p => p.Name)
                    .Concat(editedObject.Properties().Select(p => p.Name))
                    .Concat(liveObject.Properties().Select(p => p.Name))
                    .Distinct(StringComparer.Ordinal);

                foreach (var propertyName in propertyNames)
                {
                    merged[propertyName] = MergeTokens(
                        originalObject[propertyName],
                        editedObject[propertyName],
                        liveObject[propertyName]);
                }

                return merged;
            }

            return edited?.DeepClone();
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
