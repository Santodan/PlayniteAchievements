using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace PlayniteAchievements.Services.CustomProviders
{
    public sealed class CustomProviderChangedEventArgs : EventArgs
    {
        public CustomProviderChangedEventArgs(string id, bool deleted)
        {
            Id = id;
            Deleted = deleted;
        }

        public string Id { get; }

        public bool Deleted { get; }
    }

    /// <summary>
    /// Library-wide catalog of user-defined custom providers, persisted as
    /// <c>custom_providers.json</c> in the plugin user data folder. Kept out of
    /// <c>PersistedSettings</c> on purpose: the settings dialog clones and restores that
    /// instance on cancel, which would silently revert edits made from the Manage window.
    /// </summary>
    public sealed class CustomProviderStore
    {
        public const string FileName = "custom_providers.json";

        private sealed class StoreFile
        {
            public int SchemaVersion { get; set; } = 1;

            public List<CustomProviderDefinition> Providers { get; set; } = new List<CustomProviderDefinition>();
        }

        private readonly string _filePath;
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<string, CustomProviderDefinition> _byId =
            new Dictionary<string, CustomProviderDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _versions =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Geometry> _geometryCache =
            new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<CustomProviderChangedEventArgs> Changed;

        public CustomProviderStore(string pluginUserDataPath, ILogger logger = null)
        {
            _logger = logger;
            _filePath = Path.Combine(pluginUserDataPath ?? string.Empty, FileName);
            Load();
        }

        public string FilePath => _filePath;

        public IReadOnlyList<CustomProviderDefinition> GetAll()
        {
            lock (_sync)
            {
                return _byId.Values
                    .Select(definition => definition.Clone())
                    .OrderBy(definition => definition.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public bool TryGet(string id, out CustomProviderDefinition definition)
        {
            definition = null;
            var normalizedId = CustomProviderKeys.NormalizeId(id);
            if (normalizedId == null)
            {
                return false;
            }

            lock (_sync)
            {
                if (_byId.TryGetValue(normalizedId, out var stored))
                {
                    definition = stored.Clone();
                    return true;
                }
            }

            return false;
        }

        public bool Contains(string id)
        {
            return TryGet(id, out _);
        }

        /// <summary>
        /// Returns the display provider key (<c>Custom:&lt;id&gt;</c>) when the id names a stored
        /// provider, otherwise null so callers fall back to the bare Custom key.
        /// </summary>
        public string ResolveDisplayKey(string id)
        {
            return Contains(id) ? CustomProviderKeys.Build(id) : null;
        }

        public int GetVersion(string id)
        {
            var normalizedId = CustomProviderKeys.NormalizeId(id);
            if (normalizedId == null)
            {
                return 0;
            }

            lock (_sync)
            {
                return _versions.TryGetValue(normalizedId, out var version) ? version : 0;
            }
        }

        /// <summary>
        /// The provider's icon geometry, parsed once from its stored path data and frozen. Null
        /// when the id is unknown or the stored data is unreadable.
        /// </summary>
        public Geometry GetGeometry(string id)
        {
            var normalizedId = CustomProviderKeys.NormalizeId(id);
            if (normalizedId == null)
            {
                return null;
            }

            lock (_sync)
            {
                if (_geometryCache.TryGetValue(normalizedId, out var cached))
                {
                    return cached;
                }

                if (!_byId.TryGetValue(normalizedId, out var definition) ||
                    string.IsNullOrWhiteSpace(definition.IconPathData))
                {
                    return null;
                }

                try
                {
                    var geometry = Geometry.Parse(definition.IconPathData);
                    if (geometry.CanFreeze)
                    {
                        geometry.Freeze();
                    }

                    _geometryCache[normalizedId] = geometry;
                    return geometry;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Custom provider '{normalizedId}' has unreadable icon path data.");
                    return null;
                }
            }
        }

        public string GenerateUniqueId()
        {
            lock (_sync)
            {
                string id;
                do
                {
                    id = CustomProviderKeys.GenerateId();
                }
                while (_byId.ContainsKey(id));

                return id;
            }
        }

        /// <summary>
        /// Adds or replaces a provider and persists the catalog. Returns the normalized stored copy.
        /// </summary>
        public CustomProviderDefinition Upsert(CustomProviderDefinition definition)
        {
            var normalized = Normalize(definition);
            if (normalized == null)
            {
                throw new ArgumentException("A custom provider needs an id.", nameof(definition));
            }

            lock (_sync)
            {
                _byId[normalized.Id] = normalized;
                _versions[normalized.Id] = GetVersionUnsafe(normalized.Id) + 1;
                _geometryCache.Remove(normalized.Id);
                Save();
            }

            Changed?.Invoke(this, new CustomProviderChangedEventArgs(normalized.Id, deleted: false));
            return normalized.Clone();
        }

        /// <summary>
        /// Imports a definition only when its id is not already stored, so a package from another
        /// machine recreates the provider while a local definition of the same id stays authoritative.
        /// </summary>
        public bool ImportIfMissing(CustomProviderDefinition definition)
        {
            var normalized = Normalize(definition);
            if (normalized == null || string.IsNullOrWhiteSpace(normalized.Name))
            {
                return false;
            }

            lock (_sync)
            {
                if (_byId.ContainsKey(normalized.Id))
                {
                    return true;
                }
            }

            Upsert(normalized);
            return true;
        }

        public bool Delete(string id)
        {
            var normalizedId = CustomProviderKeys.NormalizeId(id);
            if (normalizedId == null)
            {
                return false;
            }

            lock (_sync)
            {
                if (!_byId.Remove(normalizedId))
                {
                    return false;
                }

                _versions[normalizedId] = GetVersionUnsafe(normalizedId) + 1;
                _geometryCache.Remove(normalizedId);
                Save();
            }

            Changed?.Invoke(this, new CustomProviderChangedEventArgs(normalizedId, deleted: true));
            return true;
        }

        private int GetVersionUnsafe(string id) => _versions.TryGetValue(id, out var version) ? version : 0;

        private static CustomProviderDefinition Normalize(CustomProviderDefinition definition)
        {
            var id = CustomProviderKeys.NormalizeId(definition?.Id);
            if (id == null)
            {
                return null;
            }

            var name = (definition.Name ?? string.Empty).Trim();
            if (name.Length > CustomProviderKeys.MaxNameLength)
            {
                name = name.Substring(0, CustomProviderKeys.MaxNameLength).Trim();
            }

            var colorHex = (definition.ColorHex ?? string.Empty).Trim();
            if (!IsValidColor(colorHex))
            {
                colorHex = CustomProviderKeys.DefaultColorHex;
            }

            return new CustomProviderDefinition
            {
                Id = id,
                Name = name,
                ColorHex = colorHex,
                IconPathData = string.IsNullOrWhiteSpace(definition.IconPathData) ? null : definition.IconPathData.Trim(),
                IconSource = string.IsNullOrWhiteSpace(definition.IconSource) ? null : definition.IconSource.Trim()
            };
        }

        internal static bool IsValidColor(string colorText)
        {
            if (string.IsNullOrWhiteSpace(colorText))
            {
                return false;
            }

            try
            {
                return ColorConverter.ConvertFromString(colorText.Trim()) is Color;
            }
            catch
            {
                return false;
            }
        }

        private void Load()
        {
            lock (_sync)
            {
                _byId.Clear();
                if (!File.Exists(_filePath))
                {
                    return;
                }

                try
                {
                    var file = JsonConvert.DeserializeObject<StoreFile>(File.ReadAllText(_filePath));
                    foreach (var definition in file?.Providers ?? new List<CustomProviderDefinition>())
                    {
                        var normalized = Normalize(definition);
                        if (normalized != null)
                        {
                            _byId[normalized.Id] = normalized;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed loading custom providers from '{_filePath}'.");
                }
            }
        }

        private void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var file = new StoreFile
                {
                    Providers = _byId.Values
                        .OrderBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(definition => definition.Clone())
                        .ToList()
                };
                File.WriteAllText(_filePath, JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving custom providers to '{_filePath}'.");
            }
        }
    }
}
