using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using Playnite.SDK;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Which template a fire-test preview should render: the plugin's own template honoring the
    /// user's appearance customization, or the currently-running theme's override. A theme's
    /// template can only render while that theme is the active theme (its resources are loaded),
    /// so the theme source always targets the current app mode.
    /// </summary>
    public enum NotificationTemplatePreviewSource
    {
        PluginStyle,
        ActiveTheme
    }

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

        // Plugin-owned custom template files, installed via the .pastyle.zip import into
        // <PluginUserData>\custom_templates and consulted below (after theme overrides, before the
        // bundled default). Same authoring contract as a theme override file.
        public const string CustomTemplatesDirectoryName = "custom_templates";
        public const string CustomToastTemplateFileName = "AchievementToast.xaml";
        public const string CustomFrameTemplateFileName = "ScreenshotFrame.xaml";

        // The bundled default source for the notification storyboards and content shadow. The two
        // surface DataTemplates are NOT here; they load from their own single-source files below.
        private const string NotificationResourcesUri =
            "pack://application:,,,/PlayniteAchievements;component/Resources/NotificationResources.xaml";

        // Single source of truth for each surface's built-in default template: the same embedded
        // loose-XAML file is parsed for live rendering (LoadDefaultTemplateDictionary) and returned
        // verbatim by the "Export default template" action (ReadDefaultTemplateXaml), so the export
        // always equals the live default. Manifest names follow the folder path under the assembly.
        private const string ToastDefaultTemplateResourceName =
            "PlayniteAchievements.Resources.DefaultTemplates.AchievementToast.xaml";
        private const string FrameDefaultTemplateResourceName =
            "PlayniteAchievements.Resources.DefaultTemplates.ScreenshotFrame.xaml";
        private const string DefaultTemplatePackBaseUri = "pack://application:,,,/";

        private static readonly Dictionary<string, CachedThemeDictionary> ThemeDictionaryCache =
            new Dictionary<string, CachedThemeDictionary>(StringComparer.OrdinalIgnoreCase);

        // Parsed bundled default templates, keyed by manifest resource name. The embedded XAML is
        // immutable, so a successful parse is cached process-wide.
        private static readonly Dictionary<string, ResourceDictionary> PluginDefaultTemplateCache =
            new Dictionary<string, ResourceDictionary>(StringComparer.Ordinal);

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly Func<DataTemplate> _loadDefaultTemplate;
        private readonly Func<DataTemplate> _loadDefaultFrameTemplate;
        private readonly string _customTemplatesDirectory;

        public AchievementToastTemplateResolver(
            IPlayniteAPI api,
            ILogger logger,
            Func<DataTemplate> loadDefaultTemplate = null,
            Func<DataTemplate> loadDefaultFrameTemplate = null,
            string customTemplatesDirectory = null)
        {
            _api = api;
            _logger = logger;
            _loadDefaultTemplate = loadDefaultTemplate;
            _loadDefaultFrameTemplate = loadDefaultFrameTemplate;
            _customTemplatesDirectory = string.IsNullOrWhiteSpace(customTemplatesDirectory)
                ? null
                : customTemplatesDirectory;
        }

        /// <summary>
        /// Builds the custom-templates directory path under the plugin's user-data folder, or null
        /// when the user-data path is unavailable. Shared by every construction site so the live
        /// toast, the screenshot frame, and the settings mockups all resolve the same custom files.
        /// </summary>
        public static string GetCustomTemplatesDirectory(string pluginUserDataPath)
        {
            return string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? null
                : Path.Combine(pluginUserDataPath, CustomTemplatesDirectoryName);
        }

        /// <summary>
        /// The directory for a specific custom-template scope: a game (highest), a provider, or the
        /// global default. Mirrors the notification-image owner layout. Null when no custom-templates
        /// directory was supplied (e.g. tests).
        /// </summary>
        private string GetScopeDirectory(string providerKey, Guid gameId)
        {
            if (_customTemplatesDirectory == null)
            {
                return null;
            }

            if (gameId != Guid.Empty)
            {
                return Path.Combine(_customTemplatesDirectory, "games", gameId.ToString("D"));
            }

            if (!string.IsNullOrWhiteSpace(providerKey))
            {
                return Path.Combine(_customTemplatesDirectory, "providers", providerKey.Trim());
            }

            return Path.Combine(_customTemplatesDirectory, "global");
        }

        /// <summary>
        /// Exact file path for one scope's custom template (used to install / remove / read that
        /// scope), or null when no directory is configured. Does not fall back across scopes.
        /// </summary>
        public string GetScopedCustomTemplatePath(bool isFrame, string providerKey, Guid gameId)
        {
            var directory = GetScopeDirectory(providerKey, gameId);
            return directory == null
                ? null
                : Path.Combine(directory, isFrame ? CustomFrameTemplateFileName : CustomToastTemplateFileName);
        }

        /// <summary>
        /// The custom template that applies for an unlock, resolved most-specific first: the game's
        /// template, then the provider's, then the global default. Returns the first that exists on
        /// disk, or null when none is installed.
        /// </summary>
        public string ResolveCustomTemplatePath(bool isFrame, string providerKey, Guid gameId)
        {
            if (_customTemplatesDirectory == null)
            {
                return null;
            }

            if (gameId != Guid.Empty)
            {
                var gamePath = GetScopedCustomTemplatePath(isFrame, null, gameId);
                if (!string.IsNullOrWhiteSpace(gamePath) && File.Exists(gamePath))
                {
                    return gamePath;
                }
            }

            if (!string.IsNullOrWhiteSpace(providerKey))
            {
                var providerPath = GetScopedCustomTemplatePath(isFrame, providerKey, Guid.Empty);
                if (!string.IsNullOrWhiteSpace(providerPath) && File.Exists(providerPath))
                {
                    return providerPath;
                }
            }

            var globalPath = GetScopedCustomTemplatePath(isFrame, null, Guid.Empty);
            return !string.IsNullOrWhiteSpace(globalPath) && File.Exists(globalPath) ? globalPath : null;
        }

        /// <summary>True when a custom template is installed for the exact scope.</summary>
        public bool HasCustomTemplate(bool isFrame, string providerKey, Guid gameId)
        {
            var path = GetScopedCustomTemplatePath(isFrame, providerKey, gameId);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }

        public DataTemplate ResolveTemplate(bool allowThemeSources = true, string providerKey = null, Guid gameId = default)
        {
            return ResolveTemplate(Application.Current?.Resources, allowThemeSources, providerKey, gameId);
        }

        public DataTemplate ResolveTemplate(
            ResourceDictionary applicationResources,
            bool allowThemeSources = true,
            string providerKey = null,
            Guid gameId = default)
        {
            return ResolveResource<DataTemplate>(
                applicationResources, TemplateKey, ThemeOverrideRelativePath, _loadDefaultTemplate, allowThemeSources,
                ResolveCustomTemplatePath(isFrame: false, providerKey, gameId));
        }

        /// <summary>
        /// Resolves the screenshot-frame DataTemplate (composited onto framed unlock screenshots,
        /// never shown on screen) using the same theme-override precedence as the toast template,
        /// except the theme file is PlayniteAchievements\ScreenshotFrame.xaml.
        /// </summary>
        public DataTemplate ResolveFrameTemplate(bool allowThemeSources = true, string providerKey = null, Guid gameId = default)
        {
            return ResolveFrameTemplate(Application.Current?.Resources, allowThemeSources, providerKey, gameId);
        }

        public DataTemplate ResolveFrameTemplate(
            ResourceDictionary applicationResources,
            bool allowThemeSources = true,
            string providerKey = null,
            Guid gameId = default)
        {
            return ResolveResource<DataTemplate>(
                applicationResources, FrameTemplateKey, FrameThemeOverrideRelativePath, _loadDefaultFrameTemplate, allowThemeSources,
                ResolveCustomTemplatePath(isFrame: true, providerKey, gameId));
        }

        /// <summary>
        /// Resolves the notification or frame template from one explicit source for the fire-test
        /// buttons: the plugin's bundled template (ignoring themes), or the currently-running
        /// theme's override (which renders because its resources are loaded in the active mode).
        /// Falls back to the bundled template when the active theme ships no override.
        /// </summary>
        public DataTemplate ResolvePreviewTemplate(
            NotificationTemplatePreviewSource source,
            bool isFrame,
            string providerKey = null,
            Guid gameId = default)
        {
            var key = isFrame ? FrameTemplateKey : TemplateKey;
            var overrideRelativePath = isFrame ? FrameThemeOverrideRelativePath : ThemeOverrideRelativePath;
            var pluginDefault = isFrame ? _loadDefaultFrameTemplate : _loadDefaultTemplate;
            var customPath = ResolveCustomTemplatePath(isFrame, providerKey, gameId);

            if (source == NotificationTemplatePreviewSource.PluginStyle)
            {
                // The plugin-owned path: the installed custom template if present, else the
                // bundled default (themes are intentionally skipped for this preview).
                if (TryLoadCustomTemplateResource<DataTemplate>(customPath, key, out var custom))
                {
                    return custom;
                }

                return LoadPluginDefaultResource(key, pluginDefault);
            }

            // The active theme: resolve exactly as a real notification would (loaded theme
            // resource, then the theme's override file, then the custom template, then bundled).
            return ResolveResource<DataTemplate>(
                Application.Current?.Resources, key, overrideRelativePath, pluginDefault, allowThemeSources: true,
                customPath);
        }

        /// <summary>
        /// True when the given preview source can supply its own template: the plugin style
        /// always can; the active theme only when it actually ships the surface override. Lets
        /// the UI disable a theme fire-test button that would just fall back to the plugin
        /// template.
        /// </summary>
        public bool ThemeProvidesTemplate(NotificationTemplatePreviewSource source, bool isFrame)
        {
            if (source == NotificationTemplatePreviewSource.PluginStyle)
            {
                return true;
            }

            var key = isFrame ? FrameTemplateKey : TemplateKey;
            var overrideRelativePath = isFrame ? FrameThemeOverrideRelativePath : ThemeOverrideRelativePath;

            try
            {
                if (TryFindLoadedThemeResource<DataTemplate>(Application.Current?.Resources, key, out _))
                {
                    return true;
                }

                var dictionary = LoadActiveThemeDictionary(Application.Current?.Resources, overrideRelativePath);
                return dictionary != null && TryGetDirectResource(dictionary, key, out DataTemplate _);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to probe active theme template for '{key}'.");
                return false;
            }
        }

        /// <summary>
        /// Resolves one of the toast animation storyboards (slide-in/out, countdown) using the same
        /// theme-override precedence as the toast template: an already-loaded theme resource, then
        /// the active theme's AchievementToast.xaml, then the bundled plugin default. Returns null
        /// when no key is found anywhere, letting the caller fall back to a code-built animation so a
        /// broken theme override never disables toasts.
        /// </summary>
        public Storyboard ResolveStoryboard(string key, bool allowThemeSources = true)
        {
            return ResolveResource<Storyboard>(
                Application.Current?.Resources, key, ThemeOverrideRelativePath, null, allowThemeSources);
        }

        /// <summary>
        /// Resolves a plain resource value (e.g. a string or number a theme uses to override the
        /// toast position or duration) using the same theme-override precedence as the template.
        /// Returns null when no theme defines the key, letting the caller keep the plugin setting as
        /// the default. The bundled plugin dictionary intentionally does not define these keys, so
        /// the fallback yields null rather than a plugin-supplied value.
        /// </summary>
        public object ResolveResourceValue(string key, bool allowThemeSources = true)
        {
            return ResolveResource<object>(
                Application.Current?.Resources, key, ThemeOverrideRelativePath, null, allowThemeSources);
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
            return ResolveActiveThemeOverridePaths(applicationResources, overrideRelativePath, GetThemeModeName());
        }

        public IReadOnlyList<string> ResolveActiveThemeOverridePaths(
            ResourceDictionary applicationResources,
            string overrideRelativePath,
            string modeName)
        {
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
            Func<T> pluginDefaultOverride,
            bool allowThemeSources = true,
            string customTemplateFullPath = null)
            where T : class
        {
            // allowThemeSources=false is the user's per-surface theme-styling opt-out: both
            // theme lookups are skipped so the plugin-owned resources (custom template, then the
            // bundled default) win.
            if (allowThemeSources)
            {
                if (TryFindLoadedThemeResource<T>(applicationResources, key, out var loaded))
                {
                    return loaded;
                }

                if (TryLoadActiveThemeResource<T>(applicationResources, key, overrideRelativePath, out var themeResource))
                {
                    return themeResource;
                }
            }

            // Plugin-owned custom template (theme-independent), consulted whether or not theme
            // sources are allowed. A parse failure here returns null and falls through to the
            // bundled default, so a bad custom template can never break notifications.
            if (TryLoadCustomTemplateResource<T>(customTemplateFullPath, key, out var customResource))
            {
                return customResource;
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
            string overrideRelativePath,
            string modeNameOverride = null)
        {
            var modeName = modeNameOverride ?? GetThemeModeName();
            var themeId = GetActiveThemeId(modeName);
            var path = ResolveActiveThemeOverridePaths(applicationResources, overrideRelativePath, modeName)
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
                // Surface templates come from their single-source files (shared with export); the
                // storyboards and content shadow come from NotificationResources.xaml.
                ResourceDictionary dictionary;
                if (key == TemplateKey)
                {
                    dictionary = LoadDefaultTemplateDictionary(isFrame: false);
                }
                else if (key == FrameTemplateKey)
                {
                    dictionary = LoadDefaultTemplateDictionary(isFrame: true);
                }
                else
                {
                    dictionary = new ResourceDictionary
                    {
                        Source = new Uri(NotificationResourcesUri, UriKind.Absolute)
                    };
                }

                return dictionary != null && TryGetDirectResource(dictionary, key, out T resource)
                    ? resource
                    : null;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to load default achievement toast resource '{key}'.");
                return null;
            }
        }

        /// <summary>
        /// Loads (and caches process-wide) the bundled default template dictionary for the surface
        /// from its embedded single-source loose-XAML file. The same file's text is returned
        /// verbatim by <see cref="ReadDefaultTemplateXaml"/>, so the live default and the exported
        /// default never diverge. The file merges NotificationResources.xaml for its dependencies.
        /// </summary>
        private ResourceDictionary LoadDefaultTemplateDictionary(bool isFrame)
        {
            var resourceName = isFrame ? FrameDefaultTemplateResourceName : ToastDefaultTemplateResourceName;
            if (PluginDefaultTemplateCache.TryGetValue(resourceName, out var cached))
            {
                return cached;
            }

            ResourceDictionary dictionary = null;
            try
            {
                var xaml = ReadEmbeddedResourceText(resourceName);
                dictionary = LoadResourceDictionaryFromText(
                    xaml,
                    new ParserContext { BaseUri = new Uri(DefaultTemplatePackBaseUri, UriKind.Absolute) });
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to load bundled default notification template: {resourceName}");
                dictionary = null;
            }

            PluginDefaultTemplateCache[resourceName] = dictionary;
            return dictionary;
        }

        private static string ReadEmbeddedResourceText(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        $"Embedded default template resource not found: {resourceName}");
                }

                using (var reader = new StreamReader(
                           stream,
                           new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// Loads a plugin-owned custom template file into a ResourceDictionary using WPF's native
        /// Source-based loader — the same mechanism as the bundled default (LoadPluginDefaultResource)
        /// — rather than a hand-rolled XamlReader stream. Cached per path by last-write time; a parse
        /// failure caches null so the caller falls back to the bundled default. Returns null when the
        /// path is unset or missing.
        /// </summary>
        private ResourceDictionary LoadCustomTemplateDictionary(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
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
                _logger?.Debug(ex, $"Failed to inspect custom notification template: {path}");
                return null;
            }

            var cacheKey = $"custom|{path}";
            if (ThemeDictionaryCache.TryGetValue(cacheKey, out var cached) &&
                cached.LastWriteTimeUtc == lastWriteUtc)
            {
                return cached.Dictionary;
            }

            ResourceDictionary dictionary = null;
            try
            {
                dictionary = new ResourceDictionary { Source = new Uri(path, UriKind.Absolute) };
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to load custom notification template (using bundled default): {path}");
                dictionary = null;
            }

            ThemeDictionaryCache[cacheKey] = new CachedThemeDictionary(lastWriteUtc, dictionary);
            return dictionary;
        }

        private bool TryLoadCustomTemplateResource<T>(string customTemplateFullPath, string key, out T resource)
            where T : class
        {
            resource = null;
            var dictionary = LoadCustomTemplateDictionary(customTemplateFullPath);
            return dictionary != null && TryGetDirectResource(dictionary, key, out resource);
        }

        /// <summary>
        /// Validates that <paramref name="xaml"/> loads (via the same native Source loader real
        /// resolution uses) as a ResourceDictionary defining the surface's template key. Loose XAML
        /// needs assembly-qualified namespaces, so this catches the classic authoring mistake at
        /// install time. Returns true on success; otherwise sets <paramref name="error"/>.
        /// </summary>
        public bool TryValidateTemplateXaml(string xaml, bool isFrame, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(xaml))
            {
                error = "The template file is empty.";
                return false;
            }

            var key = isFrame ? FrameTemplateKey : TemplateKey;
            string tempPath = null;
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "TemplateValidate");
                Directory.CreateDirectory(tempDir);
                tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".xaml");
                File.WriteAllText(tempPath, xaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var dictionary = new ResourceDictionary { Source = new Uri(tempPath, UriKind.Absolute) };
                if (!TryGetDirectResource(dictionary, key, out DataTemplate _))
                {
                    error = $"The file does not define a DataTemplate with x:Key \"{key}\".";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); }
                    catch (Exception ex) { _logger?.Debug(ex, $"Failed to delete template validation temp file: {tempPath}"); }
                }
            }
        }

        /// <summary>
        /// Installs a custom template for the surface after validating it, writing it into the
        /// custom-templates directory and evicting the cached parse so the next resolve reloads it.
        /// Throws when no directory is configured or the XAML fails validation.
        /// </summary>
        public void SaveCustomTemplate(bool isFrame, string xaml, string providerKey, Guid gameId)
        {
            var path = GetScopedCustomTemplatePath(isFrame, providerKey, gameId);
            if (path == null)
            {
                throw new InvalidOperationException("No custom templates directory is configured.");
            }

            if (!TryValidateTemplateXaml(xaml, isFrame, out var error))
            {
                throw new InvalidOperationException($"The custom template is not valid: {error}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, xaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ThemeDictionaryCache.Remove($"custom|{path}");
        }

        /// <summary>
        /// Removes the installed custom template for the exact scope, reverting that scope to the
        /// next-most-specific custom template, then theme/bundled.
        /// </summary>
        public void DeleteCustomTemplate(bool isFrame, string providerKey, Guid gameId)
        {
            var path = GetScopedCustomTemplatePath(isFrame, providerKey, gameId);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to delete custom notification template: {path}");
            }

            ThemeDictionaryCache.Remove($"custom|{path}");
        }

        /// <summary>
        /// Returns the installed custom template's XAML for the scope (resolved most-specific
        /// first: game, then provider, then global), or null when no custom template is installed.
        /// This is the only template the plugin bundles into an exported package: it is
        /// user-authored loose XAML that passed validation on install, so it imports intact. The
        /// active theme's override is intentionally never returned here — it is coupled to that
        /// theme's resources and to the theme being active, so it would import broken elsewhere.
        /// </summary>
        public string ReadCustomTemplateXaml(bool isFrame, string providerKey, Guid gameId)
        {
            var customPath = ResolveCustomTemplatePath(isFrame, providerKey, gameId);
            if (string.IsNullOrWhiteSpace(customPath) || !File.Exists(customPath))
            {
                return null;
            }

            try
            {
                return File.ReadAllText(customPath);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to read custom template for export: {customPath}");
                return null;
            }
        }

        /// <summary>
        /// Returns the surface's built-in default template as loose, standalone XAML, read verbatim
        /// from the same embedded file the plugin renders at runtime (see
        /// <see cref="LoadDefaultTemplateDictionary"/>), so the "Export default template" output is
        /// always exactly the live default. The file follows the override contract
        /// (assembly-qualified namespaces, merged NotificationResources.xaml, the template key), so
        /// it renders on any machine and theme when edited and re-imported.
        /// </summary>
        public string ReadDefaultTemplateXaml(bool isFrame)
        {
            return ReadEmbeddedResourceText(
                isFrame ? FrameDefaultTemplateResourceName : ToastDefaultTemplateResourceName);
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
