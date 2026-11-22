using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Views
{
    /// <summary>
    /// The parts of an achievement cell most rows do not show (the hardcore border, the locked
    /// progress, the compare layout) sit in presenters that stay collapsed, so they are not built
    /// for a row that does not show them. A collapsed presenter has not built its content, and
    /// until it has, its own bindings read no row; so the cell template's triggers show it.
    /// </summary>
    [TestClass]
    public class AchievementCellLazyPartsDefinitionTests
    {
        [DataTestMethod]
        [DataRow("AchievementIconTemplate", "HardcoreBorder")]
        [DataRow("AchievementUnlockDateTemplate", "Progress")]
        [DataRow("AchievementUnlockDateTemplate", "Compare")]
        public void ARarelyShownPart_IsACollapsedPresenter_ThatTheTemplatesTriggerShows(string template, string part)
        {
            var xaml = Read("source", "Resources", "AchievementCellTemplates.xaml");
            var body = Between(xaml, $"<DataTemplate x:Key=\"{template}\">", "\n    </DataTemplate>");

            var presenter = Regex.Match(body, $"<ContentPresenter x:Name=\"{part}\"[^>]*>", RegexOptions.Singleline);
            Assert.IsTrue(presenter.Success, $"{part} is a presenter");
            StringAssert.Contains(presenter.Value, "Visibility=\"Collapsed\"");
            Assert.IsFalse(presenter.Value.Contains("<ContentPresenter.Style>"), "a style on the presenter binds before it has a row");

            var triggers = Between(body, "<DataTemplate.Triggers>", "</DataTemplate.Triggers>");
            StringAssert.Contains(triggers, $"<Setter TargetName=\"{part}\" Property=\"Visibility\" Value=\"Visible\"/>");
        }

        private static string Read(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return File.ReadAllText(path).Replace("\r\n", "\n");
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
