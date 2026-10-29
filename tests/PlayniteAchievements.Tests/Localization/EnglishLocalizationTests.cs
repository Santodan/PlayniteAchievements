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
                if (File.Exists(path))
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
