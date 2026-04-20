using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PlayniteAchievements.Providers.ImportedGameMetadata
{
    public sealed class ImportedGameMetadataSourceOption
    {
        public ImportedGameMetadataSourceOption(string id, string displayName)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }

        public string Id { get; }

        public string DisplayName { get; }
    }

    internal sealed class ImportedMetadataPluginIdentity
    {
        public MetadataPlugin Plugin { get; set; }

        public string RuntimeName { get; set; }

        public string ManifestId { get; set; }

        public string ManifestName { get; set; }

        public string AssemblyDirectory { get; set; }
    }

    internal static class ImportedGameMetadataSourceCatalog
    {
        public const string AutomaticId = "";
        public const string SteamHuntersId = "builtin:steamhunters";
        public const string CompletionistId = "builtin:completionist";
        public const string UniversalSteamMetadataPluginName = "Universal Steam Metadata";
        public const string UniversalSteamMetadataManifestId = "Universal_Steam_Metadata";

        public static IReadOnlyList<ImportedGameMetadataSourceOption> GetAvailableOptions(IPlayniteAPI api, ILogger logger)
        {
            var options = new List<ImportedGameMetadataSourceOption>
            {
                new ImportedGameMetadataSourceOption(AutomaticId, "Automatic"),
                new ImportedGameMetadataSourceOption(SteamHuntersId, "SteamHunters"),
                new ImportedGameMetadataSourceOption(CompletionistId, "Completionist.me")
            };

            var seenIds = new HashSet<string>(options.Select(option => option.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var option in GetInstalledMetadataProviderOptions(api, logger))
            {
                if (option == null || !seenIds.Add(option.Id))
                {
                    continue;
                }

                options.Add(option);
            }

            return options;
        }

        public static bool IsBuiltInSource(string metadataSourceId)
        {
            var normalizedId = metadataSourceId?.Trim() ?? string.Empty;
            return string.Equals(normalizedId, SteamHuntersId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedId, CompletionistId, StringComparison.OrdinalIgnoreCase);
        }

        public static MetadataPlugin ResolveMetadataPlugin(IPlayniteAPI api, ILogger logger, string metadataSourceId)
        {
            if (api == null || string.IsNullOrWhiteSpace(metadataSourceId) || IsBuiltInSource(metadataSourceId))
            {
                return null;
            }

            try
            {
                var metadataPlugins = GetMetadataPluginIdentities(api, logger);

                if (metadataSourceId.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    var pluginName = metadataSourceId.Substring(5).Trim();
                    var matchedByName = metadataPlugins.FirstOrDefault(plugin => MatchesPluginIdentity(plugin, pluginName));
                    if (matchedByName != null)
                    {
                        return matchedByName.Plugin;
                    }
                }

                if (Guid.TryParse(metadataSourceId, out var pluginId))
                {
                    return metadataPlugins.FirstOrDefault(plugin => plugin.Plugin?.Id == pluginId)?.Plugin;
                }

                var matchedByToken = metadataPlugins.FirstOrDefault(plugin => MatchesPluginIdentity(plugin, metadataSourceId));
                if (matchedByToken != null)
                {
                    return matchedByToken.Plugin;
                }

                LogMetadataPluginIdentities(logger, metadataPlugins, metadataSourceId);
                return null;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[ImportMetadata] Failed resolving metadata provider '{metadataSourceId}'.");
                return null;
            }
        }

        public static MetadataPlugin ResolveAutomaticMetadataPlugin(IPlayniteAPI api, ILogger logger)
        {
            try
            {
                return GetMetadataPluginIdentities(api, logger)
                    .FirstOrDefault(plugin => IsUniversalSteamMetadataPlugin(plugin))
                    ?.Plugin;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[ImportMetadata] Failed resolving Universal Steam Metadata plugin instance.");
                return null;
            }
        }

        public static bool IsUniversalSteamMetadataPlugin(MetadataPlugin plugin, IPlayniteAPI api, ILogger logger)
        {
            if (plugin == null)
            {
                return false;
            }

            var identity = GetMetadataPluginIdentities(api, logger)
                .FirstOrDefault(candidate => candidate.Plugin?.Id == plugin.Id);
            return IsUniversalSteamMetadataPlugin(identity);
        }

        private static IReadOnlyList<ImportedGameMetadataSourceOption> GetInstalledMetadataProviderOptions(IPlayniteAPI api, ILogger logger)
        {
            var options = new List<ImportedGameMetadataSourceOption>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var extensionsDirectory in GetCandidateExtensionsDirectories(api, logger))
                {
                    foreach (var manifestPath in Directory.EnumerateFiles(extensionsDirectory, "extension.yaml", SearchOption.AllDirectories))
                    {
                        string type = null;
                        string name = null;
                        foreach (var line in File.ReadLines(manifestPath))
                        {
                            if (line.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
                            {
                                type = line.Substring(5).Trim();
                            }
                            else if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                            {
                                name = line.Substring(5).Trim();
                            }
                        }

                        if (string.Equals(type, "MetadataProvider", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(name)
                            && names.Add(name.Trim()))
                        {
                            options.Add(new ImportedGameMetadataSourceOption($"name:{name.Trim()}", name.Trim()));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[ImportMetadata] Failed reading installed metadata provider manifests.");
            }

            return options
                .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IReadOnlyList<string> GetCandidateExtensionsDirectories(IPlayniteAPI api, ILogger logger)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string path)
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    candidates.Add(path);
                }
            }

            var applicationPath = api?.Paths?.ApplicationPath?.Trim();
            if (!string.IsNullOrWhiteSpace(applicationPath))
            {
                if (Directory.Exists(applicationPath))
                {
                    AddCandidate(Path.Combine(applicationPath, "Extensions"));
                }

                var applicationDirectory = Directory.Exists(applicationPath)
                    ? applicationPath
                    : Path.GetDirectoryName(applicationPath);
                AddCandidate(Path.Combine(applicationDirectory ?? string.Empty, "Extensions"));
            }

            AddCandidate(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Extensions"));

            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                var parentDirectory = Directory.GetParent(assemblyDirectory);
                AddCandidate(parentDirectory?.FullName);
                AddCandidate(Path.Combine(parentDirectory?.Parent?.FullName ?? string.Empty, "Extensions"));
            }

            logger?.Info($"[ImportMetadata] Metadata provider manifest search paths: {string.Join(", ", candidates)}");
            return candidates.ToList();
        }

        private static IReadOnlyList<ImportedMetadataPluginIdentity> GetMetadataPluginIdentities(IPlayniteAPI api, ILogger logger)
        {
            try
            {
                return (api?.Addons?.Plugins?
                    .OfType<MetadataPlugin>()
                    .Where(plugin => plugin != null)
                    .Select(plugin =>
                    {
                        var manifest = GetPluginManifestIdentity(plugin, logger);
                        return new ImportedMetadataPluginIdentity
                        {
                            Plugin = plugin,
                            RuntimeName = plugin.Name?.Trim(),
                            ManifestId = manifest.Item1,
                            ManifestName = manifest.Item2,
                            AssemblyDirectory = manifest.Item3
                        };
                    })
                    .ToList() ?? new List<ImportedMetadataPluginIdentity>());
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[ImportMetadata] Failed enumerating loaded metadata plugins.");
                return Array.Empty<ImportedMetadataPluginIdentity>();
            }
        }

        private static Tuple<string, string, string> GetPluginManifestIdentity(MetadataPlugin plugin, ILogger logger)
        {
            if (plugin == null)
            {
                return Tuple.Create<string, string, string>(null, null, null);
            }

            try
            {
                var assemblyDirectory = Path.GetDirectoryName(plugin.GetType().Assembly.Location);
                if (string.IsNullOrWhiteSpace(assemblyDirectory))
                {
                    return Tuple.Create<string, string, string>(null, null, null);
                }

                for (var directory = new DirectoryInfo(assemblyDirectory); directory != null; directory = directory.Parent)
                {
                    var manifestPath = Path.Combine(directory.FullName, "extension.yaml");
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    string manifestId = null;
                    string manifestName = null;
                    foreach (var line in File.ReadLines(manifestPath))
                    {
                        if (line.StartsWith("Id:", StringComparison.OrdinalIgnoreCase))
                        {
                            manifestId = line.Substring(3).Trim();
                        }
                        else if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                        {
                            manifestName = line.Substring(5).Trim();
                        }
                    }

                    return Tuple.Create(manifestId, manifestName, directory.FullName);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[ImportMetadata] Failed reading manifest identity for metadata plugin '{plugin?.Name}'.");
            }

            return Tuple.Create<string, string, string>(null, null, null);
        }

        private static bool MatchesPluginIdentity(ImportedMetadataPluginIdentity plugin, string value)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var token = value.Trim();
            return string.Equals(plugin.RuntimeName, token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(plugin.ManifestName, token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(plugin.ManifestId, token, StringComparison.OrdinalIgnoreCase);
        }

        private static void LogMetadataPluginIdentities(ILogger logger, IEnumerable<ImportedMetadataPluginIdentity> plugins, string requestedValue)
        {
            var summary = string.Join(" | ", (plugins ?? Enumerable.Empty<ImportedMetadataPluginIdentity>()).Select(plugin =>
                $"runtime='{plugin.RuntimeName ?? "<null>"}', manifestName='{plugin.ManifestName ?? "<null>"}', manifestId='{plugin.ManifestId ?? "<null>"}', pluginId='{plugin.Plugin?.Id.ToString() ?? "<null>"}', assembly='{plugin.AssemblyDirectory ?? "<null>"}'"));
            logger?.Info($"[ImportMetadata] Failed to resolve metadata provider '{requestedValue}'. Loaded metadata plugins: {summary}");
        }

        private static bool IsUniversalSteamMetadataPlugin(ImportedMetadataPluginIdentity plugin)
        {
            if (plugin == null)
            {
                return false;
            }

            return string.Equals(plugin.ManifestId, UniversalSteamMetadataManifestId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(plugin.ManifestName, UniversalSteamMetadataPluginName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(plugin.RuntimeName, UniversalSteamMetadataPluginName, StringComparison.OrdinalIgnoreCase);
        }
    }
}