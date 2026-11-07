using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    public sealed class LinkedFileSchemaChangedEventArgs : EventArgs
    {
        public LinkedFileSchemaChangedEventArgs(string name, bool deleted)
        {
            Name = name;
            Deleted = deleted;
        }

        public string Name { get; }

        public bool Deleted { get; }
    }

    /// <summary>
    /// The user's saved file mappings, persisted as <c>linked_file_schemas.json</c> in the plugin
    /// user data folder and keyed by name. A game always holds its own copy of the mapping it
    /// uses, so editing or deleting a saved one never changes a game. Kept out of
    /// <c>PersistedSettings</c> for the reason <c>CustomProviderStore</c> is: the settings dialog
    /// restores that instance on cancel, which would revert saves made from the Manage window.
    /// </summary>
    public sealed class LinkedFileSchemaStore
    {
        public const string FileName = "linked_file_schemas.json";

        private sealed class StoreFile
        {
            public int SchemaVersion { get; set; } = 1;

            public List<LinkedFileSchema> Schemas { get; set; } = new List<LinkedFileSchema>();
        }

        private readonly string _filePath;
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<string, LinkedFileSchema> _byName =
            new Dictionary<string, LinkedFileSchema>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<LinkedFileSchemaChangedEventArgs> Changed;

        public LinkedFileSchemaStore(string pluginUserDataPath, ILogger logger = null)
        {
            _logger = logger;
            _filePath = Path.Combine(pluginUserDataPath ?? string.Empty, FileName);
            Load();
        }

        public string FilePath => _filePath;

        public IReadOnlyList<LinkedFileSchema> GetAll()
        {
            lock (_sync)
            {
                return _byName.Values
                    .Select(schema => schema.Clone())
                    .OrderBy(schema => schema.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }

        public bool TryGet(string name, out LinkedFileSchema schema)
        {
            schema = null;
            var key = LinkedFileSchema.SanitizeName(name);
            if (key.Length == 0)
            {
                return false;
            }

            lock (_sync)
            {
                if (_byName.TryGetValue(key, out var stored))
                {
                    schema = stored.Clone();
                    return true;
                }
            }

            return false;
        }

        public bool Contains(string name)
        {
            return TryGet(name, out _);
        }

        /// <summary>Adds or replaces the mapping with the schema's name. Returns the stored copy.</summary>
        public LinkedFileSchema Save(LinkedFileSchema schema)
        {
            var normalized = LinkedFileSchema.Normalize(schema);
            if (normalized?.Name == null)
            {
                throw new ArgumentException("A saved mapping needs a name.", nameof(schema));
            }

            lock (_sync)
            {
                _byName[normalized.Name] = normalized;
                Persist();
            }

            Changed?.Invoke(this, new LinkedFileSchemaChangedEventArgs(normalized.Name, deleted: false));
            return normalized.Clone();
        }

        public bool Delete(string name)
        {
            var key = LinkedFileSchema.SanitizeName(name);
            if (key.Length == 0)
            {
                return false;
            }

            lock (_sync)
            {
                if (!_byName.Remove(key))
                {
                    return false;
                }

                Persist();
            }

            Changed?.Invoke(this, new LinkedFileSchemaChangedEventArgs(key, deleted: true));
            return true;
        }

        /// <summary>
        /// Renames a saved mapping. False when the source does not exist, the new name is blank,
        /// or another mapping already has the new name.
        /// </summary>
        public bool Rename(string oldName, string newName)
        {
            var oldKey = LinkedFileSchema.SanitizeName(oldName);
            var newKey = LinkedFileSchema.SanitizeName(newName);
            if (oldKey.Length == 0 || newKey.Length == 0)
            {
                return false;
            }

            lock (_sync)
            {
                if (!_byName.TryGetValue(oldKey, out var stored))
                {
                    return false;
                }

                if (!string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase) && _byName.ContainsKey(newKey))
                {
                    return false;
                }

                _byName.Remove(oldKey);
                stored.Name = newKey;
                _byName[newKey] = stored;
                Persist();
            }

            Changed?.Invoke(this, new LinkedFileSchemaChangedEventArgs(oldKey, deleted: true));
            Changed?.Invoke(this, new LinkedFileSchemaChangedEventArgs(newKey, deleted: false));
            return true;
        }

        private void Load()
        {
            lock (_sync)
            {
                _byName.Clear();
                if (!File.Exists(_filePath))
                {
                    return;
                }

                try
                {
                    var file = JsonConvert.DeserializeObject<StoreFile>(File.ReadAllText(_filePath));
                    foreach (var schema in file?.Schemas ?? new List<LinkedFileSchema>())
                    {
                        var normalized = LinkedFileSchema.Normalize(schema);
                        if (normalized?.Name != null && !_byName.ContainsKey(normalized.Name))
                        {
                            _byName[normalized.Name] = normalized;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed loading saved file mappings from '{_filePath}'.");
                }
            }
        }

        private void Persist()
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
                    Schemas = _byName.Values
                        .OrderBy(schema => schema.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(schema => schema.Clone())
                        .ToList()
                };
                File.WriteAllText(_filePath, JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving file mappings to '{_filePath}'.");
            }
        }
    }
}
