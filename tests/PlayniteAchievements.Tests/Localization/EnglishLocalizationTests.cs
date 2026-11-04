using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlayniteAchievements.Tests.Localization
{
    [TestClass]
    public class EnglishLocalizationTests
    {
        [TestMethod]
        public void EveryLocalizationDictionaryLoadsInWpfAndHasNonEmptyLabels()
        {
            Exception failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var directory = Path.GetDirectoryName(FindRepoFile("source", "Localization", "en_US.xaml"));
                    foreach (var path in Directory.EnumerateFiles(directory, "*.xaml"))
                    {
                        using (var stream = File.OpenRead(path))
                        {
                            var dictionary = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
                            foreach (var key in dictionary.Keys)
                            {
                                var label = dictionary[key] as string;
                                Assert.IsFalse(string.IsNullOrWhiteSpace(label), Path.GetFileName(path) + " :: " + key);
                                Assert.IsFalse(label.StartsWith("<!LOC", StringComparison.Ordinal), Path.GetFileName(path) + " :: " + key);
                            }
                        }
                    }
                }
                catch (Exception exception) { failure = exception; }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "WPF localization loading timed out.");
            if (failure != null) Assert.Fail(failure.ToString());
        }

        [TestMethod]
        public void RuntimeGeneratedKeysResolveInEnglish()
        {
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var keys = new HashSet<string>(XDocument.Load(FindRepoFile("source", "Localization", "en_US.xaml"))
                .Root.Elements().Select(element => (string)element.Attribute(xaml + "Key")), StringComparer.Ordinal);
            var required = new List<string>();
            foreach (PlayniteAchievements.Models.RefreshModeType mode in Enum.GetValues(typeof(PlayniteAchievements.Models.RefreshModeType)))
            {
                required.Add(PlayniteAchievements.Models.RefreshModeExtensions.GetResourceKey(mode));
                required.Add(PlayniteAchievements.Models.RefreshModeExtensions.GetShortResourceKey(mode));
            }
            foreach (var area in PlayniteAchievements.Models.Tagging.CustomizationTagAreas.All)
                foreach (var type in area.TagTypes)
                    required.Add(PlayniteAchievements.Models.Tagging.CustomizationTagAreas.NameResourceKey(type));
            required.AddRange(PlayniteAchievements.Services.Tagging.DefaultTagNameCatalog.StatusResourceKeys.Values);
            required.AddRange(PlayniteAchievements.Services.Achievements.AchievementTagHelper.BuiltInTagKeys
                .Select(tag => "LOCPlayAch_AchievementTag_" + tag));
            required.AddRange(PlayniteAchievements.Services.Achievements.CategoryKindHelper.AssignableKinds
                .Select(kind => "LOCPlayAch_CategoryKind_" + kind));
            var showcaseFamilies = new (Type Type, string Prefix, string[] Shared)[]
            {
                (Type: typeof(PlayniteAchievements.Models.Settings.ShowcaseScoreCardArt), Prefix: "ScoreCardArt", Shared: new[] { "GameCover" }),
                (typeof(PlayniteAchievements.Models.Settings.ShowcaseProfileMedalMode), "ProfileMedalMode", new[] { "Both", "None" }),
                (typeof(PlayniteAchievements.Models.Settings.ShowcasePointsGrouping), "PointsGrouping", new[] { "Game" }),
                (typeof(PlayniteAchievements.Models.Settings.ShowcaseMosaicSource), "MosaicSource", new[] { "All" }),
                (typeof(PlayniteAchievements.Models.Settings.UnlockNextCriterion), "UnlockNextCriterion", Array.Empty<string>()),
                (typeof(PlayniteAchievements.Models.Settings.ShowcaseAchievementGridSource), "AchievementGridSource", new[] { "UnlockNext" }),
                (typeof(PlayniteAchievements.Models.Settings.ShowcaseGameMosaicSource), "GameMosaicSource", Array.Empty<string>()),
                (typeof(PlayniteAchievements.Models.Settings.ShowcaseImageFitMode), "ImageFit", Array.Empty<string>()),
                (typeof(PlayniteAchievements.Models.Settings.ShowcaseTextSize), "TextSize", Array.Empty<string>()),
                (typeof(PlayniteAchievements.Models.Settings.ShowcasePageTemplate), "Template", new[] { "Showcase", "Collection" })
            };
            foreach (var family in showcaseFamilies)
                required.AddRange(Enum.GetNames(family.Type).Except(family.Shared)
                    .Select(name => "LOCPlayAch_Showcase_" + family.Prefix + "_" + name));
            var providersDirectory = FindRepoFile("source", "Providers");
            foreach (var path in Directory.EnumerateFiles(providersDirectory, "*Settings.cs", SearchOption.AllDirectories))
                required.AddRange(Regex.Matches(File.ReadAllText(path), "ProviderKey\\s*=>\\s*\"([A-Za-z0-9]+)\"")
                    .Cast<Match>().Select(match => "LOCPlayAch_Provider_" + match.Groups[1].Value));
            foreach (var key in required.Distinct()) Assert.IsTrue(keys.Contains(key), "Missing runtime label: " + key);
        }

        [TestMethod]
        public void TranslatedFormatLabelsKeepTheEnglishArgumentIndexes()
        {
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var englishPath = FindRepoFile("source", "Localization", "en_US.xaml");
            var english = XDocument.Load(englishPath).Root.Elements()
                .ToDictionary(element => (string)element.Attribute(xaml + "Key"), element => element.Value);
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(englishPath), "*.xaml"))
                foreach (var element in XDocument.Load(path).Root.Elements())
                {
                    var key = (string)element.Attribute(xaml + "Key");
                    if (key == null || !english.TryGetValue(key, out var baseline)) continue;
                    Func<string, string[]> arguments = value => Regex.Matches(value, @"(?<!\{)\{(\d+)[^{}]*\}(?!\})")
                        .Cast<Match>().Select(match => match.Groups[1].Value).Distinct().OrderBy(index => index).ToArray();
                    CollectionAssert.AreEqual(arguments(baseline), arguments(element.Value), Path.GetFileName(path) + " :: " + key);
                }
        }

        [TestMethod]
        public void LocalizationDictionariesContainNoDuplicateKeys()
        {
            var directory = Path.GetDirectoryName(FindRepoFile("source", "Localization", "en_US.xaml"));
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (var path in Directory.EnumerateFiles(directory, "*.xaml"))
            {
                var duplicates = XDocument.Load(path).Root.Elements()
                    .Select(element => (string)element.Attribute(xaml + "Key"))
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .GroupBy(key => key, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                CollectionAssert.AreEqual(Array.Empty<string>(), duplicates,
                    Path.GetFileName(path) + ": duplicate keys prevent WPF from loading the dictionary.");
            }
        }

        [TestMethod]
        public void EnUsContainsHoyoverseSettingsViewResourceKeys()
        {
            var enUsPath = FindRepoFile("source", "Localization", "en_US.xaml");
            var viewPath = FindRepoFile("source", "Providers", "Hoyoverse", "HoyoverseSettingsView.xaml");
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

            var keys = new HashSet<string>(
                XDocument.Load(enUsPath)
                    .Descendants()
                    .Select(element => (string)element.Attribute(xaml + "Key"))
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.Ordinal);
            var usedKeys = Regex.Matches(File.ReadAllText(viewPath), @"LOCPlayAch_[A-Za-z0-9_]+")
                .Cast<Match>()
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var missing = usedKeys
                .Where(key => !keys.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(Array.Empty<string>(), missing);
        }

        [TestMethod]
        public void EnUsContainsProviderOverrideKeys()
        {
            var enUsPath = FindRepoFile("source", "Localization", "en_US.xaml");
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

            var keys = new HashSet<string>(
                XDocument.Load(enUsPath)
                    .Descendants()
                    .Select(element => (string)element.Attribute(xaml + "Key"))
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.Ordinal);

            var expected = new[]
            {
                "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Epic",
                "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_GOG",
                "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_PSN",
                "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Xbox",
                "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_EA",
                "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Hoyoverse",
                "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_BattleNet",
                "LOCPlayAch_ManageAchievements_Overrides_ProviderValueRequired",
                "LOCPlayAch_ManageAchievements_Overrides_ProviderInvalidChoice",
                "LOCPlayAch_Menu_PsnNpCommId_InvalidId",
                "LOCPlayAch_Common_Validation_InvalidId"
            };

            var missing = expected
                .Where(key => !keys.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(Array.Empty<string>(), missing);
        }

        [TestMethod]
        public void EnUsContainsAppearanceSectionResourceKeys()
        {
            var enUsPath = FindRepoFile("source", "Localization", "en_US.xaml");
            var viewPath = FindRepoFile(
                "source",
                "Views",
                "Settings",
                "Display",
                "ColorsSection.xaml");
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

            var keys = new HashSet<string>(
                XDocument.Load(enUsPath)
                    .Descendants()
                    .Select(element => (string)element.Attribute(xaml + "Key"))
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.Ordinal);
            var missing = Regex.Matches(
                    File.ReadAllText(viewPath),
                    @"LOCPlayAch_[A-Za-z0-9_]+")
                .Cast<Match>()
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .Where(key => !keys.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(Array.Empty<string>(), missing);
        }

        [TestMethod]
        public void SourceReferencesOnlyKeysDefinedInEnUs()
        {
            var enUsPath = FindRepoFile("source", "Localization", "en_US.xaml");
            var sourceRoot = Directory.GetParent(Path.GetDirectoryName(enUsPath)).FullName;
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

            var keys = new HashSet<string>(
                XDocument.Load(enUsPath)
                    .Descendants()
                    .Select(element => (string)element.Attribute(xaml + "Key"))
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.Ordinal);
            var skippedDirectories = new[] { "Localization", "bin", "obj" }
                .Select(name => Path.DirectorySeparatorChar + name + Path.DirectorySeparatorChar)
                .ToList();

            // Keys contain a lowercase letter, which separates them from identifiers such as
            // LOCKED; tokens ending in "_" are prefixes composed with an enum value at runtime.
            var missing = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Where(path => !skippedDirectories.Any(directory =>
                    path.Substring(sourceRoot.Length).IndexOf(directory, StringComparison.OrdinalIgnoreCase) >= 0))
                .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\bLOC[A-Z][A-Za-z0-9_]*[a-z][A-Za-z0-9_]*")
                    .Cast<Match>()
                    .Select(match => match.Value)
                    .Where(key => !key.EndsWith("_", StringComparison.Ordinal) && !keys.Contains(key))
                    .Select(key => key + " (" + Path.GetFileName(path) + ")"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(entry => entry, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(Array.Empty<string>(), missing, string.Join(Environment.NewLine, missing));
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path) || Directory.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
        }
    }
}
