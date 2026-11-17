using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Views
{
    /// <summary>
    /// A StaticResource key that no dictionary defines throws while its XAML loads. For a provider
    /// settings view that drops the whole settings tab, so every key the plugin's XAML references
    /// statically must be defined in some plugin XAML file.
    /// </summary>
    [TestClass]
    public class StaticResourceKeysDefinedTests
    {
        private static readonly Regex CommentPattern = new Regex("<!--.*?-->", RegexOptions.Singleline);
        private static readonly Regex StaticResourcePattern = new Regex(@"\{StaticResource\s+([^}\s,]+)\s*\}");
        private static readonly Regex KeyPattern = new Regex("x:Key=\"([^\"]+)\"");

        [TestMethod]
        public void EveryStaticResourceKeyIsDefined()
        {
            var sourceRoot = FindSourceRoot();
            var files = Directory.EnumerateFiles(sourceRoot, "*.xaml", SearchOption.AllDirectories)
                .Where(file => !IsUnder(file, sourceRoot, "bin", "obj", "Localization", "packages"))
                .ToDictionary(file => file, file => CommentPattern.Replace(File.ReadAllText(file), string.Empty));
            var defined = new HashSet<string>(
                files.Values.SelectMany(text => KeyPattern.Matches(text).Cast<Match>().Select(match => match.Groups[1].Value)),
                StringComparer.Ordinal);

            var missing = files
                .SelectMany(file => StaticResourcePattern.Matches(file.Value).Cast<Match>()
                    .Select(match => match.Groups[1].Value)
                    .Where(key => !defined.Contains(key))
                    .Select(key => $"{key} in {file.Key.Substring(sourceRoot.Length + 1)}"))
                .Distinct()
                .ToList();

            Assert.IsTrue(defined.Count > 100, "The scan found too few keys; the file set is wrong.");
            Assert.AreEqual(0, missing.Count, "Undefined StaticResource keys: " + string.Join("; ", missing));
        }

        private static bool IsUnder(string file, string root, params string[] folders)
        {
            var relative = file.Substring(root.Length + 1).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Take(relative.Length - 1).Any(segment => folders.Contains(segment, StringComparer.OrdinalIgnoreCase));
        }

        private static string FindSourceRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "source");
                if (File.Exists(Path.Combine(candidate, "PlayniteAchievements.csproj")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            Assert.Fail("The repository's source folder was not found.");
            return null;
        }
    }
}
