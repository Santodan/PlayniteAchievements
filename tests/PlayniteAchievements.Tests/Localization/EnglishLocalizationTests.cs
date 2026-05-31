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
