using System.IO;
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.StartPage
{
    [TestClass]
    public class StartPageLocalizationTests
    {
        [TestMethod]
        public void EnglishLocalization_DoesNotAddStartPageSpecificKeys()
        {
            var content = File.ReadAllText(FindRepoFile("source", "Localization", "en_US.xaml"));

            Assert.IsFalse(content.Contains("LOCPlayAch_" + "StartPage"));
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
