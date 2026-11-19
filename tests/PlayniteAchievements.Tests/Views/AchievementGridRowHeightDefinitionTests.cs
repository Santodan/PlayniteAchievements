using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Views
{
    /// <summary>
    /// Every row of the shared achievement grid has one height, from the settings and column
    /// widths only, so the virtualizing panel never measures rows to place them. The control is not
    /// linked into this project, so the rules are pinned against the source.
    /// </summary>
    [TestClass]
    public class AchievementGridRowHeightDefinitionTests
    {
        [TestMethod]
        public void EveryRow_GetsTheGridsOneHeight()
        {
            var source = Read("source", "Views", "Controls", "AchievementDataGridControl.RowHeight.cs");
            var apply = Between(source, "private void ApplyRowHeight(", "private double? ResolveFixedRowHeight()");

            StringAssert.Contains(apply, "row.Height = RowHeight;");
            Assert.IsFalse(apply.Contains("ClearValue"), "an unset height lets rows size to their content");
            StringAssert.Contains(source, "ResolveFixedRowHeight() ?? _autoRowHeight", "a user-set Row Height wins");
        }

        [TestMethod]
        public void NameAndDescription_ShareTheCellHeight_WithoutALineCap()
        {
            var templates = Read("source", "Resources", "AchievementCellTemplates.xaml");
            var info = Between(templates, "<DataTemplate x:Key=\"AchievementInfoTemplate\">", "</DataTemplate>");

            StringAssert.Contains(info, "<DockPanel LastChildFill=\"True\"", "each text gets the height left, so only the bottom one trims");
            StringAssert.Contains(info, "DockPanel.Dock=\"Top\"");
            Assert.AreEqual(2, info.Split(new[] { "TextTrimming=\"CharacterEllipsis\"" }, StringSplitOptions.None).Length - 1);
            Assert.IsFalse(info.Contains("MaxHeight"), "a line cap trims text that fits");
        }

        private static string Read(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail($"Could not find {string.Join(Path.DirectorySeparatorChar.ToString(), parts)}.");
            return null;
        }

        private static string Between(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(startIndex >= 0, $"Could not find '{start}'.");

            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.IsTrue(endIndex > startIndex, $"Could not find '{end}' after '{start}'.");

            return source.Substring(startIndex, endIndex - startIndex);
        }
    }
}
