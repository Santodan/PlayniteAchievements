using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Tests.Views
{
    // The Manage Achievements nav rail declares its tab order in three places that must agree:
    // the RadioButton order in the XAML, the ControllerTabOrder array driving controller/keyboard
    // next-prev, and the GetVisibleTabButtons() list driving focus. A fourth mirror -- which tabs
    // require cached achievement data -- lives in the XAML visibility bindings and in
    // ManageAchievementsTabs.RequireAchievementData, because XAML cannot read the set directly.
    [TestClass]
    public class ManageAchievementsTabOrderDefinitionTests
    {
        [TestMethod]
        public void NavRail_XamlOrder_MatchesControllerAndFocusOrder()
        {
            var xamlOrder = ReadXamlTabOrder();
            var controllerOrder = ReadListedTabs(
                ReadControlCode(), "ControllerTabOrder =", "};");
            var focusOrder = ReadFocusButtonOrder();

            Assert.AreEqual(
                11,
                xamlOrder.Count,
                "Expected 11 tabs in the nav rail; update this test if a tab was added or removed.");

            CollectionAssert.AreEqual(
                xamlOrder,
                controllerOrder,
                "ControllerTabOrder must match the RadioButton order in ManageAchievementsControl.xaml. "
                    + "XAML: " + string.Join(", ", xamlOrder)
                    + " | ControllerTabOrder: " + string.Join(", ", controllerOrder));

            CollectionAssert.AreEqual(
                xamlOrder,
                focusOrder,
                "GetVisibleTabButtons() must match the RadioButton order in ManageAchievementsControl.xaml. "
                    + "XAML: " + string.Join(", ", xamlOrder)
                    + " | GetVisibleTabButtons: " + string.Join(", ", focusOrder));
        }

        [TestMethod]
        public void NavRail_AchievementDataGatedTabs_MatchRequireAchievementDataSet()
        {
            var gatedInXaml = ReadXamlAchievementDataGatedTabs();
            var gatedInCode = ReadListedTabs(
                File.ReadAllText(FindRepoFile(
                    "source", "ViewModels", "ManageAchievements", "ManageAchievementsTab.cs")),
                "RequireAchievementData =",
                "};");

            CollectionAssert.AreEquivalent(
                gatedInCode,
                gatedInXaml,
                "Every tab whose nav button binds visibility to HasAchievementData must be listed in "
                    + "ManageAchievementsTabs.RequireAchievementData, and vice versa. "
                    + "XAML: " + string.Join(", ", gatedInXaml)
                    + " | RequireAchievementData: " + string.Join(", ", gatedInCode));
        }

        [TestMethod]
        public void NavRail_GroupHeaders_ReuseExistingLocalizationKeys()
        {
            var xaml = ReadControlXaml();

            // Group headers reuse keys already defined in en_US.xaml; no new strings are introduced.
            var headerKeys = new[]
            {
                "LOCPlayAch_Common_General",
                "LOCPlayAch_Achievements",
                "LOCPlayAch_Settings_Appearance",
                "LOCPlayAch_Settings_Maintenance_Title"
            };

            var english = File.ReadAllText(FindRepoFile("source", "Localization", "en_US.xaml"));

            foreach (var key in headerKeys)
            {
                Assert.IsTrue(
                    xaml.Contains("{DynamicResource " + key + "}"),
                    "Nav rail is missing the group header binding for " + key + ".");
                Assert.IsTrue(
                    english.Contains("x:Key=\"" + key + "\""),
                    "Group header key " + key + " must already exist in en_US.xaml.");
            }

            // The Achievements group collapses with its tabs, so its header is gated too.
            Assert.IsTrue(
                Regex.IsMatch(
                    xaml,
                    "LOCPlayAch_Achievements\\}\"[\\s\\S]{0,400}?Binding HasAchievementData"),
                "The Achievements group header must bind visibility to HasAchievementData so it "
                    + "collapses with the tabs it labels.");
        }

        [TestMethod]
        public void CapstonesPane_DropsUnreachableEmptyState()
        {
            var xaml = ReadControlXaml();
            var viewModel = File.ReadAllText(FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsViewModel.cs"));

            // The empty state was gated on the same flag that hides the Capstones nav button and
            // forces a fallback to Overview, so it could never render.
            Assert.IsFalse(
                xaml.Contains("CapstoneEmptyMessage"),
                "The unreachable Capstones empty-state TextBlock must stay removed.");
            Assert.IsFalse(
                viewModel.Contains("CapstoneEmptyMessage"),
                "The unused CapstoneEmptyMessage property must stay removed.");
            Assert.IsFalse(
                xaml.Contains("HasCapstoneData") || viewModel.Contains("HasCapstoneData"),
                "HasCapstoneData was renamed to HasAchievementData.");
        }

        private static List<string> ReadXamlTabOrder()
        {
            return Regex.Matches(ReadControlXaml(), "<RadioButton x:Name=\"(\\w+)TabButton\"")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToList();
        }

        private static List<string> ReadXamlAchievementDataGatedTabs()
        {
            // Each RadioButton is a self-closing element; capture the block to test it for the gate.
            return Regex.Matches(ReadControlXaml(), "<RadioButton\\s[\\s\\S]*?/>")
                .Cast<Match>()
                .Select(match => match.Value)
                .Where(block => block.Contains("Binding HasAchievementData"))
                .Select(block => Regex.Match(block, "x:Name=\"(\\w+)TabButton\"").Groups[1].Value)
                .ToList();
        }

        private static List<string> ReadFocusButtonOrder()
        {
            var code = ReadControlCode();
            var method = code.IndexOf("GetVisibleTabButtons()", StringComparison.Ordinal);
            Assert.IsTrue(method >= 0, "Could not find GetVisibleTabButtons() in the control code.");

            // Start past the array opener so the method name itself is not matched as a button.
            var start = code.IndexOf("new[]", method, StringComparison.Ordinal);
            Assert.IsTrue(start > method, "Could not find the GetVisibleTabButtons() array literal.");

            var end = code.IndexOf(".Where(", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "Could not find the end of the GetVisibleTabButtons() list.");

            return Regex.Matches(code.Substring(start, end - start), "(\\w+)TabButton")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToList();
        }

        private static List<string> ReadListedTabs(string code, string startMarker, string endMarker)
        {
            var start = code.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "Could not find " + startMarker + " in the source.");

            var end = code.IndexOf(endMarker, start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "Could not find the end of " + startMarker + ".");

            return Regex.Matches(code.Substring(start, end - start), "ManageAchievementsTab\\.(\\w+)")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToList();
        }

        private static string ReadControlXaml()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsControl.xaml"));
        }

        private static string ReadControlCode()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsControl.xaml.cs"));
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
