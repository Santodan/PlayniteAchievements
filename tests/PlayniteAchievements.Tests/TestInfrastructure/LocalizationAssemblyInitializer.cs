using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;

namespace PlayniteAchievements.Tests.TestInfrastructure
{
    /// <summary>
    /// Single assembly-wide test host bootstrap (MSTest permits one
    /// <see cref="AssemblyInitializeAttribute"/> per assembly). It performs, in order:
    /// <list type="number">
    /// <item>Installs an English-backed <see cref="ResourceProvider"/> from the shipped
    /// <c>en_US.xaml</c> so localized lookups resolve to real English text instead of the
    /// <c>&lt;!Key!&gt;</c> fallback.</item>
    /// <item>Registers the WPF <c>pack://</c> URI scheme via <see cref="PackUriHelper"/> so
    /// <c>RarityAppearanceHelper</c>'s static pack Uris construct instead of throwing
    /// <see cref="UriFormatException"/> ("Invalid port specified"). This deliberately does NOT
    /// create a WPF <see cref="System.Windows.Application"/>: doing so sets
    /// <c>Application.Current</c> AppDomain-wide, which flips the friends-overview view model onto
    /// its <c>Dispatcher.BeginInvoke</c> apply path and races every <c>LoadAsync()</c>-based
    /// assertion in the suite.</item>
    /// <item>Loads the built plugin assembly into the AppDomain so
    /// <c>pack://application:,,,/PlayniteAchievements;component/Resources/*.xaml</c> badge geometry
    /// resource streams resolve from tests.</item>
    /// </list>
    /// A dedicated STA thread runner is exposed for the few tests that must build WPF resources on
    /// an STA apartment; it does not own a WPF application.
    /// </summary>
    [TestClass]
    public static class LocalizationAssemblyInitializer
    {
        private static readonly XNamespace XamlNamespace =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        [AssemblyInitialize]
        public static void Initialize(TestContext context)
        {
            var enUsPath = FindRepoFile("source", "Localization", "en_US.xaml");
            var strings = LoadStringResources(enUsPath);

            // ResourceProvider.SetGlobalProvider is internal to the SDK (InternalsVisibleTo does not
            // cover this test assembly), so install the English-backed provider via reflection.
            var setGlobalProvider = typeof(ResourceProvider).GetMethod(
                "SetGlobalProvider",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (setGlobalProvider == null)
            {
                throw new InvalidOperationException(
                    "ResourceProvider.SetGlobalProvider was not found on the Playnite SDK.");
            }

            setGlobalProvider.Invoke(null, new object[] { new EnglishResourceProvider(strings) });

            LoadBuiltPluginAssembly();
            RegisterPackScheme();
        }

        /// <summary>
        /// Runs <paramref name="action"/> on a fresh STA thread, rethrowing any exception on the
        /// caller. Mirrors the RunOnSta helper used elsewhere in the suite; use for bodies that
        /// build/consume WPF resources requiring an STA apartment (e.g. pack-loaded badge geometry).
        /// </summary>
        public static void RunOnSta(Action action)
        {
            if (action == null)
            {
                return;
            }

            Exception captured = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();

            if (captured != null)
            {
                ExceptionDispatchInfo.Capture(captured).Throw();
            }
        }

        private static void RegisterPackScheme()
        {
            // Touching a PackUriHelper static forces its static constructor, which registers the
            // pack:// URI *parser* so pack Uris construct instead of throwing UriFormatException.
            var unused = PackUriHelper.UriSchemePack;

            // Streaming a pack resource additionally needs the pack WebRequest handler, which WPF only
            // wires up when an Application is constructed (otherwise the load throws
            // NotSupportedException "The URI prefix is not recognized"). Construct one on an STA thread
            // to perform that process-wide registration, then null Application.Current: the WebRequest
            // registration persists globally, but leaving Application.Current set would flip the
            // friends-overview view model onto its async Dispatcher.BeginInvoke apply path and race
            // every LoadAsync-based assertion in the suite.
            RunOnSta(() =>
            {
                if (Application.Current == null)
                {
                    var unusedApp = new Application();
                }

                foreach (var field in typeof(Application).GetFields(
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (field.FieldType == typeof(Application))
                    {
                        field.SetValue(null, null);
                    }
                }
            });
        }

        private static Assembly _pluginAssembly;

        private static void LoadBuiltPluginAssembly()
        {
            var pluginDll =
                FindRepoFileOrNull("source", "bin", "Debug", "PlayniteAchievements.dll") ??
                FindRepoFileOrNull("source", "bin", "Release", "PlayniteAchievements.dll");

            if (pluginDll == null)
            {
                throw new FileNotFoundException(
                    "Built plugin assembly not found at source/bin/Debug/PlayniteAchievements.dll " +
                    "(or Release). Build source/PlayniteAchievements.csproj (Configuration=Debug) " +
                    "before running the test suite so pack:// badge resources resolve.");
            }

            _pluginAssembly = Assembly.LoadFrom(pluginDll);

            // WPF's pack resolver locates "PlayniteAchievements;component/..." by probing for an
            // assembly named "PlayniteAchievements" on the test app base, where it does not exist
            // (the plugin was LoadFrom'd out of source/bin). Bridge that short-name request to the
            // already-loaded plugin assembly so its compiled badge resource streams resolve. Only the
            // plugin's RESOURCE streams are used here; test code binds to its own compiled copies of
            // the source types (assembly "PlayniteAchievements.Tests").
            AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;
        }

        private static Assembly ResolvePluginAssembly(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name).Name;
            return string.Equals(requested, "PlayniteAchievements", StringComparison.OrdinalIgnoreCase)
                ? _pluginAssembly
                : null;
        }

        private static Dictionary<string, string> LoadStringResources(string xamlPath)
        {
            var document = XDocument.Load(xamlPath);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var element in document.Descendants()
                .Where(e => e.Name.LocalName == "String"))
            {
                var key = (string)element.Attribute(XamlNamespace + "Key");
                if (!string.IsNullOrEmpty(key))
                {
                    result[key] = element.Value;
                }
            }

            return result;
        }

        private static string FindRepoFile(params string[] parts)
        {
            return FindRepoFileOrNull(parts)
                ?? throw new FileNotFoundException("Could not locate repo file: " + string.Join("/", parts));
        }

        private static string FindRepoFileOrNull(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private sealed class EnglishResourceProvider : IResourceProvider
        {
            private readonly Dictionary<string, string> _strings;

            public EnglishResourceProvider(Dictionary<string, string> strings)
            {
                _strings = strings;
            }

            // Mirrors the SDK's not-found behavior so unrelated keys still surface as "<!Key!>".
            public string GetString(string key) =>
                _strings.TryGetValue(key, out var value) ? value : "<!" + key + "!>";

            public object GetResource(string key) =>
                _strings.TryGetValue(key, out var value) ? value : null;
        }
    }
}
