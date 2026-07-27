using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class GridStyleDefinitionTests
    {
        [TestMethod]
        public void SharedGridStyles_PaintRowStateOnceWithoutCellOverdraw()
        {
            var overview = File.ReadAllText(FindRepoFile("source", "Resources", "OverviewStyles.xaml"));
            var achievement = File.ReadAllText(FindRepoFile("source", "Resources", "AchievementGridStyles.xaml"));

            var overviewCellStyle = ExtractStyleBlock(overview, "GameSummariesGridCellStyle");
            var overviewRowStyle = ExtractStyleBlock(overview, "GameSummariesGridRowBaseStyle");
            var achievementCellStyle = ExtractStyleBlock(achievement, "AchievementCellStyle");
            var achievementRowStyle = ExtractStyleBlock(achievement, "AchievementRowStyle");
            var stretchCellStyle = ExtractStyleBlock(achievement, "PlayAch.StretchDataGridCellStyle");

            AssertContainsAll(
                overviewCellStyle,
                "TargetType=\"DataGridCell\"",
                "Property=\"Background\" Value=\"Transparent\"");
            AssertContainsNone(
                overviewCellStyle,
                "Margin=\"0,0,-1,0\"",
                "Path=IsMouseOver",
                "Path=IsSelected",
                "PlayAch.Brush.Grid.RowHoverBackground",
                "PlayAch.Brush.Grid.RowSelectedBackground");
            AssertContainsAll(
                overviewRowStyle,
                "x:Name=\"DGR_Border\"",
                "TargetName=\"DGR_Border\"",
                "ContextMenu.IsOpen",
                "Property=\"IsMouseOver\" Value=\"True\"",
                "Property=\"IsSelected\" Value=\"True\"",
                "Property=\"IsKeyboardFocusWithin\" Value=\"True\"",
                "PlayAch.Brush.Grid.RowHoverBackground",
                "PlayAch.Brush.Grid.RowSelectedBackground");

            AssertContainsAll(
                achievementCellStyle,
                "TargetType=\"DataGridCell\"",
                "Property=\"Background\" Value=\"Transparent\"");
            AssertContainsNone(
                achievementCellStyle,
                "Margin=\"0,0,-1,0\"",
                "Path=IsMouseOver",
                "Path=IsSelected",
                "PlayAch.Brush.Grid.RowHoverBackground",
                "PlayAch.Brush.Grid.RowSelectedBackground");
            AssertContainsAll(
                achievementRowStyle,
                "x:Name=\"DGR_Border\"",
                "TargetName=\"DGR_Border\"",
                "ContextMenu.IsOpen",
                "Property=\"IsMouseOver\" Value=\"True\"",
                "Property=\"IsSelected\" Value=\"True\"",
                "Property=\"IsKeyboardFocusWithin\" Value=\"True\"",
                "PlayAch.Brush.Grid.RowHoverBackground",
                "PlayAch.Brush.Grid.RowSelectedBackground");
            AssertContainsAll(
                stretchCellStyle,
                "TargetType=\"DataGridCell\"");
            AssertContainsNone(
                stretchCellStyle,
                "Margin=\"0,0,-1,0\"");
        }

        private static string ExtractStyleBlock(string content, string key)
        {
            var keyText = $"x:Key=\"{key}\"";
            var start = content.IndexOf(keyText, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Style key not found: {key}");

            start = content.LastIndexOf("<Style", start, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Style start not found: {key}");

            var next = content.IndexOf("\n    <Style", start + 1, StringComparison.Ordinal);
            return next >= 0 ? content.Substring(start, next - start) : content.Substring(start);
        }

        private static void AssertContainsAll(string content, params string[] expected)
        {
            foreach (var value in expected)
            {
                StringAssert.Contains(content, value);
            }
        }

        private static void AssertContainsNone(string content, params string[] unexpected)
        {
            foreach (var value in unexpected)
            {
                Assert.IsFalse(content.Contains(value), $"Unexpected content found: {value}");
            }
        }

        private static string FindRepoFile(params string[] parts)
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(root); i++)
            {
                var candidate = Path.Combine(root, Path.Combine(parts));
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                root = Directory.GetParent(root)?.FullName;
            }

            Assert.Fail("Could not locate repo file: " + string.Join("/", parts));
            return null;
        }
    }
}
