using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RPCS3;
using System;
using System.IO;

namespace PlayniteAchievements.Providers.Tests
{
    /// <summary>
    /// RPCS3 installs every TRP entry into the trophy folder, so localized
    /// TROP_XX.SFM files sit next to the language-neutral TROPCONF.SFM.
    /// </summary>
    [TestClass]
    public class Rpcs3TrophyParserLocalizationTests
    {
        // The SCE numeric language id for Italian, as used in TROP_XX.SFM names.
        private const int ItalianTropIndex = 5;

        [TestMethod]
        public void ParseTrophyDefinitions_ItalianLocale_PrefersLocalizedSfmSibling()
        {
            var folder = CreateTempDirectory();

            try
            {
                WriteTropconf(folder, "Default Trophy", "Default Description", "Default Group");
                WriteLocalizedSfm(folder, ItalianTropIndex, "Trofeo Italiano", "Descrizione Italiana", "Gruppo Italiano");

                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitions(
                    Path.Combine(folder, "TROPCONF.SFM"), "it", null);

                Assert.AreEqual(1, trophies.Count);
                Assert.AreEqual("Trofeo Italiano", trophies[0].Name);
                Assert.AreEqual("Descrizione Italiana", trophies[0].Description);
                Assert.AreEqual("Gruppo Italiano", trophies[0].GroupName);

                // Structure still comes from TROPCONF.SFM.
                Assert.AreEqual(0, trophies[0].Id);
                Assert.AreEqual("S", trophies[0].TrophyType);
                Assert.IsTrue(trophies[0].Hidden);
            }
            finally
            {
                DeleteDirectory(folder);
            }
        }

        [TestMethod]
        public void ParseTrophyDefinitions_ItalianLocaleWithoutSibling_KeepsTropconfText()
        {
            var folder = CreateTempDirectory();

            try
            {
                WriteTropconf(folder, "Default Trophy", "Default Description", "Default Group");

                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitions(
                    Path.Combine(folder, "TROPCONF.SFM"), "it", null);

                Assert.AreEqual(1, trophies.Count);
                Assert.AreEqual("Default Trophy", trophies[0].Name);
                Assert.AreEqual("Default Description", trophies[0].Description);
                Assert.AreEqual("Default Group", trophies[0].GroupName);
            }
            finally
            {
                DeleteDirectory(folder);
            }
        }

        [TestMethod]
        public void ParseTrophyDefinitions_UnmappedLocale_IgnoresLocalizedSiblings()
        {
            var folder = CreateTempDirectory();

            try
            {
                WriteTropconf(folder, "Default Trophy", "Default Description", "Default Group");
                WriteLocalizedSfm(folder, ItalianTropIndex, "Trofeo Italiano", "Descrizione Italiana", "Gruppo Italiano");

                // "el" (Greek) has no PS3 language id, so no TROP_XX.SFM is eligible.
                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitions(
                    Path.Combine(folder, "TROPCONF.SFM"), "el", null);

                Assert.AreEqual(1, trophies.Count);
                Assert.AreEqual("Default Trophy", trophies[0].Name);
            }
            finally
            {
                DeleteDirectory(folder);
            }
        }

        [TestMethod]
        public void ParseTrophyDefinitions_LocalizedSiblingWithBlankText_KeepsTropconfText()
        {
            var folder = CreateTempDirectory();

            try
            {
                WriteTropconf(folder, "Default Trophy", "Default Description", "Default Group");
                WriteLocalizedSfm(folder, ItalianTropIndex, string.Empty, string.Empty, "Gruppo Italiano");

                var trophies = Rpcs3TrophyParser.ParseTrophyDefinitions(
                    Path.Combine(folder, "TROPCONF.SFM"), "it", null);

                Assert.AreEqual(1, trophies.Count);
                Assert.AreEqual("Default Trophy", trophies[0].Name);
                Assert.AreEqual("Default Description", trophies[0].Description);
                Assert.AreEqual("Gruppo Italiano", trophies[0].GroupName);
            }
            finally
            {
                DeleteDirectory(folder);
            }
        }

        private static void WriteTropconf(string folder, string trophyName, string trophyDescription, string groupName)
        {
            File.WriteAllText(
                Path.Combine(folder, "TROPCONF.SFM"),
                BuildTrophyConfXml(trophyName, trophyDescription, groupName));
        }

        private static void WriteLocalizedSfm(
            string folder,
            int tropIndex,
            string trophyName,
            string trophyDescription,
            string groupName)
        {
            File.WriteAllText(
                Path.Combine(folder, $"TROP_{tropIndex:00}.SFM"),
                BuildTrophyConfXml(trophyName, trophyDescription, groupName));
        }

        private static string BuildTrophyConfXml(string trophyName, string trophyDescription, string groupName)
        {
            return $@"<trophyconf>
  <npcommid>NPWR12345_00</npcommid>
  <group id=""001"">
    <name>{groupName}</name>
  </group>
  <trophy id=""0"" ttype=""S"" hidden=""yes"" pid=""0"" gid=""001"">
    <name>{trophyName}</name>
    <detail>{trophyDescription}</detail>
  </trophy>
</trophyconf>";
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(Rpcs3TrophyParserLocalizationTests),
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }
}
