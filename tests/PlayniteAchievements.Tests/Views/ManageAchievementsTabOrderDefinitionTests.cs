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
                5,
                xamlOrder.Count,
                "Expected 5 tabs in the nav rail; update this test if a tab was added or removed.");

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
            var tabsFile = File.ReadAllText(FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsTab.cs"));

            var gatedInXaml = ReadXamlTabsGatedOn("HasAchievementData");
            var requireInCode = ReadListedTabs(tabsFile, "RequireAchievementData =", "};");

            CollectionAssert.IsSubsetOf(
                gatedInXaml,
                requireInCode,
                "A tab gated on HasAchievementData must be listed in RequireAchievementData. "
                    + "XAML: " + string.Join(", ", gatedInXaml));
        }

        [TestMethod]
        public void NavRail_HasNoGroupHeaders()
        {
            var xaml = ReadControlXaml();

            // The rail is five tabs, so it lists them flat. The general Overrides tab folded into the
            // Overview, which left each remaining group with a single entry, and the headers went
            // with it.
            var formerHeaderKeys = new[]
            {
                "LOCPlayAch_Common_General",
                "LOCPlayAch_Settings_Appearance",
                "LOCPlayAch_Settings_Maintenance_Title",
                "LOCPlayAch_Achievements"
            };

            foreach (var key in formerHeaderKeys)
            {
                Assert.IsFalse(
                    xaml.Contains("{DynamicResource " + key + "}"),
                    "Nav rail group header " + key + " must stay removed.");
            }

            Assert.IsFalse(
                xaml.Contains("NavSectionHeaderStyle"),
                "The nav rail has no group headers, so it needs no header style.");
        }

        [TestMethod]
        public void EveryContentHost_DeclaresItsOwnSelectedTabTrigger()
        {
            // Each pane defaults to Collapsed and is shown by one DataTrigger on SelectedTab.
            // Deleting a pane by line range once took the next pane's trigger with it, leaving an
            // empty Style.Triggers over that default -- three tabs that could never render, and
            // nothing else catches it.
            var xaml = ReadControlXaml();

            Assert.IsFalse(
                Regex.IsMatch(xaml, "<Style\\.Triggers>\\s*</Style\\.Triggers>"),
                "A content host has an empty Style.Triggers, so its pane can never become visible.");

            var triggered = Regex.Matches(xaml, "<DataTrigger Binding=\"\\{Binding SelectedTab\\}\" Value=\"(\\w+)\"")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToList();

            CollectionAssert.AreEquivalent(
                ReadXamlTabOrder(),
                triggered,
                "Every nav button needs a pane keyed to the same tab, and vice versa. "
                    + "Buttons: " + string.Join(", ", ReadXamlTabOrder())
                    + " | Panes: " + string.Join(", ", triggered));
        }

        [TestMethod]
        public void LocalOverridesPage_ExposesFourInternalTabsWithoutLegacyOuterGate()
        {
            var xaml = File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsOverridesTab.xaml"));

            Assert.IsFalse(
                xaml.Contains("Binding SelectedOverridesTab}" + "\" Value=\"Local\""),
                "The dedicated Local Overrides page must not hide behind the legacy outer Local gate.");

            foreach (var tab in new[] { "LocalSavesSchema", "Steam", "Epic", "LumaPlay" })
            {
                StringAssert.Contains(
                    xaml,
                    "ConverterParameter=" + tab + "}",
                    "Missing Local Overrides internal tab: " + tab);
            }
        }

        [TestMethod]
        public void LocalOverridesPage_ConstructorInitializesItsXaml()
        {
            var code = File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsOverridesTab.xaml.cs"));

            StringAssert.Contains(code, "public ManageAchievementsOverridesTab()");
            StringAssert.Contains(code, "InitializeComponent();");
        }

        private static List<string> ReadXamlTabOrder()
        {
            return Regex.Matches(ReadControlXaml(), "<RadioButton x:Name=\"(\\w+)TabButton\"")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToList();
        }

        private static List<string> ReadXamlTabsGatedOn(string flagName)
        {
            // Each RadioButton is a self-closing element; capture the block to test it for the gate.
            return Regex.Matches(ReadControlXaml(), "<RadioButton\\s[\\s\\S]*?/>")
                .Cast<Match>()
                .Select(match => match.Value)
                .Where(block => block.Contains("Binding " + flagName))
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
