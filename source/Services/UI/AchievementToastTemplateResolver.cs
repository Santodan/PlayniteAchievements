using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using Playnite.SDK;

namespace PlayniteAchievements.Services.UI
{
    public sealed class AchievementToastTemplateResolver
    {
        public const string TemplateKey = "PlayAch.Template.AchievementToast";
        public const string FrameTemplateKey = "PlayAch.Template.ScreenshotFrame";
        public const string SlideInStoryboardKey = "PlayAch.Storyboard.ToastSlideIn";
        public const string SlideOutStoryboardKey = "PlayAch.Storyboard.ToastSlideOut";
        public const string CountdownStoryboardKey = "PlayAch.Storyboard.ToastCountdown";
        public const string PositionResourceKey = "PlayAch.Toast.Position";
        public const string DurationSecondsResourceKey = "PlayAch.Toast.DurationSeconds";
        public const string ThemeOverrideRelativePath = "PlayniteAchievements\\AchievementToast.xaml";
        public const string FrameThemeOverrideRelativePath = "PlayniteAchievements\\ScreenshotFrame.xaml";

        private const string PluginDefaultDictionaryUri =
            "pack://application:,,,/PlayniteAchievements;component/Resources/AchievementResources.xaml";

        private static readonly Dictionary<string, CachedThemeDictionary> ThemeDictionaryCache =
            new Dictionary<string, CachedThemeDictionary>(StringComparer.OrdinalIgnoreCase);

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly Func<DataTemplate> _loadDefaultTemplate;
        private readonly Func<DataTemplate> _loadDefaultFrameTemplate;

        public AchievementToastTemplateResolver(
            IPlayniteAPI api,
            ILogger logger,
            Func<DataTemplate> loadDefaultTemplate = null,
            Func<DataTemplate> loadDefaultFrameTemplate = null)
        {
            _api = api;
            _logger = logger;
            _loadDefaultTemplate = loadDefaultTemplate;
            _loadDefaultFrameTemplate = loadDefaultFrameTemplate;
        }

        public DataTemplate ResolveTemplate()
        {
            return ResolveTemplate(Application.Current?.Resources);
        }

        public DataTemplate ResolveTemplate(ResourceDictionary applicationResources)
        {
            return ResolveResource<DataTemplate>(
                applicationResources, TemplateKey, ThemeOverrideRelativePath, _loadDefaultTemplate);
        }

        /// <summary>
        /// Resolves the screenshot-frame DataTemplate (composited onto framed unlock screenshots,
        /// never shown on screen) using the same theme-override precedence as the toast template,
        /// except the theme file is PlayniteAchievements\ScreenshotFrame.xaml.
        /// </summary>
        public DataTemplate ResolveFrameTemplate()
        {
            return ResolveFrameTemplate(Application.Current?.Resources);
        }

        public DataTemplate ResolveFrameTemplate(ResourceDictionary applicationResources)
        {
            return ResolveResource<DataTemplate>(
                applicationResources, FrameTemplateKey, FrameThemeOverrideRelativePath, _loadDefaultFrameTemplate);
        }

        /// <summary>
        /// Resolves one of the toast animation storyboards (slide-in/out, countdown) using the same
        /// theme-override precedence as the toast template: an already-loaded theme resource, then
        /// the active theme's AchievementToast.xaml, then the bundled plugin default. Returns null
        /// when no key is found anywhere, letting the caller fall back to a code-built animation so a
        /// broken theme override never disables toasts.
        /// </summary>
        public Storyboard ResolveStoryboard(string key)
        {
            return ResolveResource<Storyboard>(Application.Current?.Resources, key, ThemeOverrideRelativePath, null);
        }

        /// <summary>
        /// Resolves a plain resource value (e.g. a string or number a theme uses to override the
        /// toast position or duration) using the same theme-override precedence as the template.
        /// Returns null when no theme defines the key, letting the caller keep the plugin setting as
        /// the default. The bundled plugin dictionary intentionally does not define these keys, so
        /// the fallback yields null rather than a plugin-supplied value.
        /// </summary>
        public object ResolveResourceValue(string key)
        {
            return ResolveResource<object>(Application.Current?.Resources, key, ThemeOverrideRelativePath, null);
        }

        public string ResolveActiveThemeOverridePath()
        {
            return ResolveActiveThemeOverridePaths(Application.Current?.Resources).FirstOrDefault();
        }

        public IReadOnlyList<string> ResolveActiveThemeOverridePaths(ResourceDictionary applicationResources)
        {
            return ResolveActiveThemeOverridePaths(applicationResources, ThemeOverrideRelativePath);
        }

        public IReadOnlyList<string> ResolveActiveThemeOverridePaths(
            ResourceDictionary applicationResources,
            string overrideRelativePath)
        {
            var modeName = GetThemeModeName();
            var themeId = GetActiveThemeId(modeName);
            var themesRoots = GetThemesRootPaths();
            var themeDirectories = ResolveThemeDirectories(applicationResources, themesRoots, modeName, themeId);
            var overridePaths = themeDirectories
                .Select(directory => Path.Combine(directory, overrideRelativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return overridePaths
                .OrderByDescending(File.Exists)
                .ToList();
        }

        public void LogActiveThemeOverrideDiagnostics(string context = null)
        {
            if (_logger == null)
            {
                return;
            }

            var applicationResources = Application.Current?.Resources;
            var modeName = GetThemeModeName();
            var activeThemeId = GetActiveThemeId(modeName);
            var themesRoots = GetThemesRootPaths();
            var overridePaths = ResolveActiveThemeOverridePaths(applicationResources);
            var contextPrefix = string.IsNullOrWhiteSpace(context) ? string.Empty : $"{context}: ";

            _logger.Info(
                $"[ToastTheme] {contextPrefix}mode={modeName}, activeTheme='{activeThemeId ?? "<null>"}', " +
                $"desktopTheme='{_api?.ApplicationSettings?.DesktopTheme ?? "<null>"}', " +
                $"fullscreenTheme='{_api?.ApplicationSettings?.FullscreenTheme ?? "<null>"}', " +
                $"configurationPath='{_api?.Paths?.ConfigurationPath ?? "<null>"}', " +
                $"applicationPath='{_api?.Paths?.ApplicationPath ?? "<null>"}', " +
                $"isPortable={_api?.Paths?.IsPortable}");

            if (themesRoots.Count == 0)
            {
                _logger.Warn($"[ToastTheme] {contextPrefix}No theme roots could be resolved.");
            }
            else
            {
                foreach (var root in themesRoots)
                {
                    _logger.Info($"[ToastTheme] {contextPrefix}themeRoot exists={Directory.Exists(root)} path='{root}'");
                }
            }

            var loadedTemplate = TryFindLoadedThemeResource<DataTemplate>(
                applicationResources,
                TemplateKey,
                out _);
            _logger.Info($"[ToastTheme] {contextPrefix}loadedResource key='{TemplateKey}' found={loadedTemplate}");

            if (overridePaths.Count == 0)
            {
                _logger.Warn($"[ToastTheme] {contextPrefix}No active theme override candidate paths were resolved.");
                return;
            }

            foreach (var path in overridePaths)
            {
                _logger.Info($"[ToastTheme] {contextPrefix}overrideCandidate exists={File.Exists(path)} path='{path}'");
            }

            var selectedPath = overridePaths.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                _logger.Info($"[ToastTheme] {contextPrefix}No AchievementToast.xaml override file found; bundled default will be used.");
            }
            else
            {
                var dictionary = LoadActiveThemeDictionary(applicationResources, ThemeOverrideRelativePath);
                if (dictionary == null)
                {
                    _logger.Warn($"[ToastTheme] {contextPrefix}Override file exists but did not load as a ResourceDictionary: '{selectedPath}'.");
                }
                else
                {
                    _logger.Info(
                        $"[ToastTheme] {contextPrefix}Loaded override '{selectedPath}'. " +
                        $"template={HasDirectResourceKey(dictionary, TemplateKey)}, " +
                        $"slideIn={HasDirectResourceKey(dictionary, SlideInStoryboardKey)}, " +
                        $"slideOut={HasDirectResourceKey(dictionary, SlideOutStoryboardKey)}, " +
                        $"countdown={HasDirectResourceKey(dictionary, CountdownStoryboardKey)}, " +
                        $"position={HasDirectResourceKey(dictionary, PositionResourceKey)}, " +
                        $"duration={HasDirectResourceKey(dictionary, DurationSecondsResourceKey)}");
                }
            }

            var framePath = ResolveActiveThemeOverridePaths(applicationResources, FrameThemeOverrideRelativePath)
                .FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(framePath))
            {
                _logger.Info($"[ToastTheme] {contextPrefix}No ScreenshotFrame.xaml override file found; bundled default frame will be used.");
                return;
            }

            var frameDictionary = LoadActiveThemeDictionary(applicationResources, FrameThemeOverrideRelativePath);
            if (frameDictionary == null)
            {
                _logger.Warn($"[ToastTheme] {contextPrefix}Frame override file exists but did not load as a ResourceDictionary: '{framePath}'.");
                return;
            }

            _logger.Info(
                $"[ToastTheme] {contextPrefix}Loaded frame override '{framePath}'. " +
                $"frame={HasDirectResourceKey(frameDictionary, FrameTemplateKey)}");
        }

        private T ResolveResource<T>(
            ResourceDictionary applicationResources,
            string key,
            string overrideRelativePath,
            Func<T> pluginDefaultOverride)
            where T : class
        {
            if (TryFindLoadedThemeResource<T>(applicationResources, key, out var loaded))
            {
                return loaded;
            }

            if (TryLoadActiveThemeResource<T>(applicationResources, key, overrideRelativePath, out var themeResource))
            {
                return themeResource;
            }

            return LoadPluginDefaultResource(key, pluginDefaultOverride);
        }

        private bool TryFindLoadedThemeResource<T>(ResourceDictionary resources, string key, out T resource)
            where T : class
        {
            resource = null;
            if (resources == null)
            {
                return false;
            }

            if (TryGetDirectResource(resources, key, out resource))
            {
                return true;
            }

            return TryFindLoadedThemeResourceInMergedDictionaries(resources.MergedDictionaries, key, out resource);
        }

        private bool TryFindLoadedThemeResourceInMergedDictionaries<T>(
            Collection<ResourceDictionary> dictionaries,
            string key,
            out T resource)
            where T : class
        {
            resource = null;
            if (dictionaries == null || dictionaries.Count == 0)
            {
                return false;
            }

            for (var i = dictionaries.Count - 1; i >= 0; i--)
            {
                var dictionary = dictionaries[i];
                if (dictionary == null || IsPluginDictionary(dictionary))
                {
                    continue;
                }

                if (TryGetDirectResource(dictionary, key, out resource))
                {
                    return true;
                }

                if (TryFindLoadedThemeResourceInMergedDictionaries(dictionary.MergedDictionaries, key, out resource))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryLoadActiveThemeResource<T>(
            ResourceDictionary applicationResources,
            string key,
            string overrideRelativePath,
            out T resource)
            where T : class
        {
            resource = null;

            var dictionary = LoadActiveThemeDictionary(applicationResources, overrideRelativePath);
            return dictionary != null && TryGetDirectResource(dictionary, key, out resource);
        }

        /// <summary>
        /// Parses the active theme's override file (AchievementToast.xaml or ScreenshotFrame.xaml)
        /// into a ResourceDictionary (or returns the cached parse when the file is unchanged), so
        /// all keys in one file are pulled from a single parse. The cache is keyed per file path.
        /// Returns null when the theme ships no override or the file fails to parse.
        /// </summary>
        private ResourceDictionary LoadActiveThemeDictionary(
            ResourceDictionary applicationResources,
            string overrideRelativePath)
        {
            var modeName = GetThemeModeName();
            var themeId = GetActiveThemeId(modeName);
            var path = ResolveActiveThemeOverridePaths(applicationResources, overrideRelativePath)
                .FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to inspect achievement toast theme override: {path}");
                return null;
            }

            var cacheKey = $"{modeName}|{themeId}|{path}";
            if (ThemeDictionaryCache.TryGetValue(cacheKey, out var cached) &&
                cached.LastWriteTimeUtc == lastWriteUtc)
            {
                return cached.Dictionary;
            }

            try
            {
                var xaml = ReadThemeOverrideText(path);
                var dictionary = LoadResourceDictionaryFromText(
                    xaml,
                    new ParserContext { BaseUri = new Uri(path, UriKind.Absolute) });
                if (dictionary == null)
                {
                    _logger?.Debug($"Achievement toast theme override did not load as a ResourceDictionary: {path}");
                }

                ThemeDictionaryCache[cacheKey] = new CachedThemeDictionary(lastWriteUtc, dictionary);
                return dictionary;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to load achievement toast theme override: {path}");
                ThemeDictionaryCache[cacheKey] = new CachedThemeDictionary(lastWriteUtc, null);
                return null;
            }
        }

        private static ResourceDictionary LoadResourceDictionaryFromText(string xaml, ParserContext parserContext)
        {
            var bytes = Encoding.UTF8.GetBytes(xaml);
            using (var stream = new MemoryStream(bytes))
            {
                return XamlReader.Load(stream, parserContext) as ResourceDictionary;
            }
        }

        private string ReadThemeOverrideText(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new StreamReader(
                           stream,
                           new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                           detectEncodingFromByteOrderMarks: true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (DecoderFallbackException ex)
            {
                _logger?.Info(
                    $"[ToastTheme] Override file is not valid UTF-8; retrying Windows-1252 fallback: '{path}'.");
                _logger?.Debug(ex, $"Achievement toast theme override was not valid UTF-8: {path}");
                return Encoding.GetEncoding(1252).GetString(File.ReadAllBytes(path));
            }
        }

        private T LoadPluginDefaultResource<T>(string key, Func<T> pluginDefaultOverride)
            where T : class
        {
            if (pluginDefaultOverride != null)
            {
                return pluginDefaultOverride();
            }

            try
            {
                var dictionary = new ResourceDictionary
                {
                    Source = new Uri(PluginDefaultDictionaryUri, UriKind.Absolute)
                };

                return TryGetDirectResource(dictionary, key, out T resource) ? resource : null;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to load default achievement toast resource '{key}'.");
                return null;
            }
        }

        private string GetThemeModeName()
        {
            return _api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen
                ? "Fullscreen"
                : "Desktop";
        }

        private string GetActiveThemeId(string modeName)
        {
            if (string.Equals(modeName, "Fullscreen", StringComparison.OrdinalIgnoreCase))
            {
                return _api?.ApplicationSettings?.FullscreenTheme;
            }

            return _api?.ApplicationSettings?.DesktopTheme;
        }

        private IReadOnlyList<string> GetThemesRootPaths()
        {
            var roots = new List<string>();
            AddThemesRoot(roots, _api?.Paths?.ConfigurationPath);
            AddThemesRoot(roots, _api?.Paths?.ApplicationPath);
            return roots;
        }

        private static void AddThemesRoot(ICollection<string> roots, string basePath)
        {
            if (roots == null || string.IsNullOrWhiteSpace(basePath))
            {
                return;
            }

            try
            {
                var themesRoot = Path.GetFullPath(Path.Combine(basePath, "Themes"));
                if (!roots.Contains(themesRoot, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(themesRoot);
                }
            }
            catch
            {
            }
        }

        private static IReadOnlyList<string> ResolveThemeDirectories(
            ResourceDictionary applicationResources,
            IEnumerable<string> themesRoots,
            string modeName,
            string themeId)
        {
            var directories = new List<string>();

            if (!string.IsNullOrWhiteSpace(themeId))
            {
                foreach (var themesRoot in themesRoots ?? Enumerable.Empty<string>())
                {
                    AddThemeDirectory(directories, ResolveThemeDirectory(themesRoot, modeName, themeId));
                }
            }

            foreach (var directory in ResolveThemeDirectoriesFromLoadedResources(
                         applicationResources,
                         modeName,
                         themesRoots))
            {
                AddThemeDirectory(directories, directory);
            }

            return directories;
        }

        private static void AddThemeDirectory(ICollection<string> directories, string directory)
        {
            if (directories == null || string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(directory);
                if (!directories.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    directories.Add(fullPath);
                }
            }
            catch
            {
            }
        }

        private static string ResolveThemeDirectory(string themesRoot, string modeName, string themeId)
        {
            var modeDirectory = Path.Combine(themesRoot, modeName);
            if (!Directory.Exists(modeDirectory))
            {
                return null;
            }

            var exactPath = Path.Combine(modeDirectory, themeId);
            if (Directory.Exists(exactPath))
            {
                return exactPath;
            }

            foreach (var directory in EnumerateDirectories(modeDirectory))
            {
                if (ThemeDirectoryNameMatches(directory, themeId) ||
                    ThemeManifestMatches(directory, themeId))
                {
                    return directory;
                }
            }

            return null;
        }

        private static bool ThemeDirectoryNameMatches(string themeDirectory, string themeId)
        {
            if (string.IsNullOrWhiteSpace(themeDirectory) || string.IsNullOrWhiteSpace(themeId))
            {
                return false;
            }

            var directoryName = Path.GetFileName(themeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(directoryName, themeId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return directoryName?.EndsWith("_" + themeId, StringComparison.OrdinalIgnoreCase) == true ||
                   directoryName?.EndsWith("-" + themeId, StringComparison.OrdinalIgnoreCase) == true ||
                   directoryName?.EndsWith(" " + themeId, StringComparison.OrdinalIgnoreCase) == true;
        }

        private static bool ThemeManifestMatches(string themeDirectory, string themeId)
        {
            if (string.IsNullOrWhiteSpace(themeDirectory) || string.IsNullOrWhiteSpace(themeId))
            {
                return false;
            }

            try
            {
                var manifestPath = Path.Combine(themeDirectory, "theme.yaml");
                if (!File.Exists(manifestPath))
                {
                    return false;
                }

                foreach (var rawLine in File.ReadLines(manifestPath))
                {
                    var line = StripInlineYamlComment(rawLine)?.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var separator = line.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separator).Trim();
                    if (!string.Equals(key, "Id", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = NormalizeYamlScalar(line.Substring(separator + 1));
                    if (string.Equals(value, themeId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static string StripInlineYamlComment(string rawLine)
        {
            if (rawLine == null)
            {
                return null;
            }

            var inSingleQuote = false;
            var inDoubleQuote = false;
            for (var i = 0; i < rawLine.Length; i++)
            {
                var c = rawLine[i];
                if (c == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (c == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (c == '#' && !inSingleQuote && !inDoubleQuote)
                {
                    return rawLine.Substring(0, i);
                }
            }

            return rawLine;
        }

        private static string NormalizeYamlScalar(string value)
        {
            return value?
                .Trim()
                .TrimStart('\uFEFF')
                .Trim()
                .Trim('"')
                .Trim('\'')
                .Trim();
        }

        private static IEnumerable<string> ResolveThemeDirectoriesFromLoadedResources(
            ResourceDictionary resources,
            string modeName,
            IEnumerable<string> themesRoots)
        {
            if (resources == null || string.IsNullOrWhiteSpace(modeName))
            {
                return Enumerable.Empty<string>();
            }

            var roots = (themesRoots ?? Enumerable.Empty<string>())
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.Combine(root, modeName))
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (roots.Count == 0)
            {
                return Enumerable.Empty<string>();
            }

            var results = new List<string>();
            foreach (var sourcePath in EnumerateResourceDictionarySourcePaths(resources))
            {
                foreach (var modeDirectory in roots)
                {
                    foreach (var themeDirectory in EnumerateDirectories(modeDirectory))
                    {
                        if (PathIsInDirectory(sourcePath, themeDirectory))
                        {
                            AddThemeDirectory(results, themeDirectory);
                        }
                    }
                }
            }

            return results;
        }

        private static IEnumerable<string> EnumerateResourceDictionarySourcePaths(ResourceDictionary resources)
        {
            if (resources == null)
            {
                yield break;
            }

            var path = GetResourceDictionarySourcePath(resources.Source);
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }

            foreach (var dictionary in resources.MergedDictionaries)
            {
                foreach (var nestedPath in EnumerateResourceDictionarySourcePaths(dictionary))
                {
                    yield return nestedPath;
                }
            }
        }

        private static string GetResourceDictionarySourcePath(Uri source)
        {
            if (source == null)
            {
                return null;
            }

            try
            {
                if (source.IsAbsoluteUri && source.IsFile)
                {
                    return Path.GetFullPath(source.LocalPath);
                }

                var raw = source.OriginalString;
                if (!string.IsNullOrWhiteSpace(raw) && Path.IsPathRooted(raw))
                {
                    return Path.GetFullPath(raw);
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool PathIsInDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> EnumerateDirectories(string path)
        {
            try
            {
                return Directory.Exists(path)
                    ? Directory.GetDirectories(path)
                    : Enumerable.Empty<string>();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static bool TryGetDirectResource<T>(ResourceDictionary dictionary, string key, out T resource)
            where T : class
        {
            resource = null;
            if (dictionary == null)
            {
                return false;
            }

            var keys = dictionary.Keys.Cast<object>().ToList();
            if (!keys.Any(k => string.Equals(k as string, key, StringComparison.Ordinal)))
            {
                return false;
            }

            resource = dictionary[key] as T;
            return resource != null;
        }

        private static bool HasDirectResourceKey(ResourceDictionary dictionary, string key)
        {
            if (dictionary == null)
            {
                return false;
            }

            return dictionary.Keys
                .Cast<object>()
                .Any(k => string.Equals(k as string, key, StringComparison.Ordinal));
        }

        private static bool IsPluginDictionary(ResourceDictionary dictionary)
        {
            var source = dictionary?.Source?.OriginalString;
            return !string.IsNullOrWhiteSpace(source) &&
                   source.IndexOf("/PlayniteAchievements;component/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class CachedThemeDictionary
        {
            public CachedThemeDictionary(DateTime lastWriteTimeUtc, ResourceDictionary dictionary)
            {
                LastWriteTimeUtc = lastWriteTimeUtc;
                Dictionary = dictionary;
            }

            public DateTime LastWriteTimeUtc { get; }

            public ResourceDictionary Dictionary { get; }
        }
    }
}
