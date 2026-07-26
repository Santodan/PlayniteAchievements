using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Services.Tagging;

namespace PlayniteAchievements.Services.Tagging.Tests
{
    [TestClass]
    public class DefaultTagNameCatalogTests
    {
        private const string EnglishXaml = @"<ResourceDictionary
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
    xmlns:sys=""clr-namespace:System;assembly=mscorlib"">
    <sys:String x:Key=""LOCPlayAch_Tag_PrefixFormat"">[PA] {0}</sys:String>
    <sys:String x:Key=""LOCPlayAch_Tagging_HasAchievements"">Has Achievements</sys:String>
    <sys:String x:Key=""LOCPlayAch_Filter_InProgress"">In Progress</sys:String>
    <sys:String x:Key=""LOCPlayAch_Completed"">Completed</sys:String>
</ResourceDictionary>";

        private const string GermanXaml = @"<ResourceDictionary
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
    xmlns:sys=""clr-namespace:System;assembly=mscorlib"">
    <sys:String x:Key=""LOCPlayAch_Tag_PrefixFormat"">[PA] {0}</sys:String>
    <sys:String x:Key=""LOCPlayAch_Tagging_HasAchievements"">Hat Erfolge</sys:String>
    <sys:String x:Key=""LOCPlayAch_Completed""></sys:String>
</ResourceDictionary>";

        private string _tempDirectory;

        [TestInitialize]
        public void Initialize()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "PlayAchTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
            }
        }

        [TestMethod]
        public void IsKnownDefault_RecognizesHardcodedEnglish_WithoutDirectory()
        {
            var catalog = new DefaultTagNameCatalog(null);

            foreach (TagType tagType in Enum.GetValues(typeof(TagType)))
            {
                Assert.IsTrue(
                    catalog.IsKnownDefault(tagType, TaggingSettings.GetDefaultDisplayName(tagType)),
                    $"Hardcoded English default for {tagType} was not recognized.");
            }

            Assert.IsFalse(catalog.IsKnownDefault(TagType.Completed, "My Custom Tag"));
        }

        [TestMethod]
        public void IsKnownDefault_RecognizesTranslatedDefaultsFromLocaleFiles()
        {
            WriteLocale("en_US.xaml", EnglishXaml);
            WriteLocale("de_DE.xaml", GermanXaml);

            var catalog = new DefaultTagNameCatalog(_tempDirectory);

            Assert.IsTrue(catalog.IsKnownDefault(TagType.HasAchievements, "[PA] Hat Erfolge"));
            Assert.IsTrue(catalog.IsKnownDefault(TagType.HasAchievements, "[PA] Has Achievements"));
            // Trimmed and case-insensitive matching.
            Assert.IsTrue(catalog.IsKnownDefault(TagType.HasAchievements, "  [pa] hat erfolge "));
            Assert.IsFalse(catalog.IsKnownDefault(TagType.HasAchievements, "[PA] Hat Erfolge!"));
        }

        [TestMethod]
        public void IsKnownDefault_FallsBackToEnglishForMissingOrBlankKeys()
        {
            WriteLocale("en_US.xaml", EnglishXaml);
            WriteLocale("de_DE.xaml", GermanXaml);

            var catalog = new DefaultTagNameCatalog(_tempDirectory);

            // de_DE has no LOCPlayAch_Filter_InProgress and a blank LOCPlayAch_Completed,
            // so its users had the en_US values persisted as defaults.
            Assert.IsTrue(catalog.IsKnownDefault(TagType.InProgress, "[PA] In Progress"));
            Assert.IsTrue(catalog.IsKnownDefault(TagType.Completed, "[PA] Completed"));
        }

        [TestMethod]
        public void Catalog_SkipsMalformedLocaleFiles()
        {
            WriteLocale("en_US.xaml", EnglishXaml);
            WriteLocale("xx_XX.xaml", "<not-valid-xml");
            WriteLocale("de_DE.xaml", GermanXaml);

            var catalog = new DefaultTagNameCatalog(_tempDirectory);

            Assert.IsTrue(catalog.IsKnownDefault(TagType.HasAchievements, "[PA] Hat Erfolge"));
        }

        [TestMethod]
        public void GetRelocalizedName_HealsKnownDefaultToCurrentDefault()
        {
            WriteLocale("en_US.xaml", EnglishXaml);
            WriteLocale("de_DE.xaml", GermanXaml);

            var catalog = new DefaultTagNameCatalog(_tempDirectory);

            Assert.AreEqual(
                "[PA] Hat Erfolge",
                catalog.GetRelocalizedName(TagType.HasAchievements, "[PA] Has Achievements", "[PA] Hat Erfolge"));
        }

        [TestMethod]
        public void GetRelocalizedName_LeavesCustomizedAndCurrentNamesAlone()
        {
            WriteLocale("en_US.xaml", EnglishXaml);
            WriteLocale("de_DE.xaml", GermanXaml);

            var catalog = new DefaultTagNameCatalog(_tempDirectory);

            // Customized name is never touched.
            Assert.IsNull(catalog.GetRelocalizedName(TagType.HasAchievements, "My Custom Tag", "[PA] Hat Erfolge"));
            // Already matching the current default is a no-op.
            Assert.IsNull(catalog.GetRelocalizedName(TagType.HasAchievements, "[PA] Hat Erfolge", "[PA] Hat Erfolge"));
            // Blank names are handled by InitializeDefaults, not re-localization.
            Assert.IsNull(catalog.GetRelocalizedName(TagType.HasAchievements, " ", "[PA] Hat Erfolge"));
            Assert.IsNull(catalog.GetRelocalizedName(TagType.HasAchievements, "[PA] Has Achievements", null));
        }

        [TestMethod]
        public void RealLocalizationDirectory_RecognizesEnglishDefaultsForAllTagTypes()
        {
            var localizationDirectory = Path.GetDirectoryName(
                FindRepoFile("source", "Localization", "en_US.xaml"));

            var catalog = new DefaultTagNameCatalog(localizationDirectory);

            foreach (TagType tagType in Enum.GetValues(typeof(TagType)))
            {
                Assert.IsTrue(
                    catalog.IsKnownDefault(tagType, TaggingSettings.GetDefaultDisplayName(tagType)),
                    $"English default for {tagType} was not recognized against the shipped locale files.");
            }

            Assert.IsFalse(catalog.IsKnownDefault(TagType.HasAchievements, "My Custom Tag"));
        }

        [TestMethod]
        public void StatusResourceKeys_CoverEveryTagType()
        {
            foreach (TagType tagType in Enum.GetValues(typeof(TagType)))
            {
                Assert.IsTrue(
                    DefaultTagNameCatalog.StatusResourceKeys.ContainsKey(tagType),
                    $"StatusResourceKeys is missing an entry for {tagType}.");
            }
        }

        private void WriteLocale(string fileName, string content)
        {
            File.WriteAllText(Path.Combine(_tempDirectory, fileName), content);
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
