using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Views
{
    /// <summary>
    /// Guards every <c>DataGridRowReorderBehavior.SetOptions</c> call site against the behavior's
    /// own required-member validation.
    /// </summary>
    /// <remarks>
    /// That validation throws from the attached-property callback, which runs during view
    /// construction — so an incomplete options object does not fail the build or the drag, it takes
    /// down the whole window the grid lives in. The compiler cannot catch it because every member
    /// is an optional initializer, which is exactly how the Editor tab shipped a broken options
    /// object: it set eight of the eleven and omitted the three drag-chrome elements.
    /// </remarks>
    [TestClass]
    public class DataGridRowReorderOptionsDefinitionTests
    {
        // Mirrors ValidateOptions in DataGridRowReorderBehavior; the tether test below fails if
        // the behavior starts requiring something this list does not name.
        private static readonly string[] RequiredMembers =
        {
            "DragDataFormat",
            "DropIndicator",
            "DragCountPopup",
            "DragCountText",
            "IsReorderableItem",
            "ExtractDragKeys",
            "MoveItemsRelativeToTarget",
            "MoveItemsToEnd"
        };

        [TestMethod]
        public void EverySetOptionsCallSite_SuppliesEveryRequiredMember()
        {
            var callSites = FindCallSiteFiles();
            Assert.IsTrue(callSites.Count > 0, "Expected at least one SetOptions call site.");

            foreach (var file in callSites)
            {
                var block = ExtractOptionsInitializer(File.ReadAllText(file));
                var missing = RequiredMembers
                    .Where(member => !block.Contains(member + " ="))
                    .ToList();

                Assert.AreEqual(
                    0,
                    missing.Count,
                    $"{Path.GetFileName(file)} omits required reorder option(s): {string.Join(", ", missing)}. " +
                    "The behavior validates these at view construction, so a missing one breaks the window.");
            }
        }

        [TestMethod]
        public void EveryCodeBehind_DeclaresTheDragChromeElementsItPassesIn()
        {
            // The three chrome members are named XAML elements rather than lambdas, so a call site
            // can only satisfy them if its own view declares them.
            foreach (var file in FindCallSiteFiles())
            {
                var xamlPath = file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(0, file.Length - 3)
                    : null;
                if (xamlPath == null || !File.Exists(xamlPath))
                {
                    continue;
                }

                var xaml = File.ReadAllText(xamlPath);
                var block = ExtractOptionsInitializer(File.ReadAllText(file));

                foreach (var member in new[] { "DropIndicator", "DragCountPopup", "DragCountText" })
                {
                    var elementName = ReadAssignedValue(block, member);
                    if (string.IsNullOrWhiteSpace(elementName))
                    {
                        continue;
                    }

                    StringAssert.Contains(
                        xaml,
                        $"x:Name=\"{elementName}\"",
                        $"{Path.GetFileName(xamlPath)} must declare the '{elementName}' element that " +
                        $"{Path.GetFileName(file)} passes as {member}.");
                }
            }
        }

        [TestMethod]
        public void RequiredMemberList_MatchesTheBehaviorValidation()
        {
            var behavior = File.ReadAllText(
                FindRepoFile("source", "Views", "Helpers", "DataGridRowReorderBehavior.cs"));
            var validation = Between(behavior, "private static void ValidateOptions", "throw new InvalidOperationException");

            foreach (var member in RequiredMembers)
            {
                StringAssert.Contains(
                    validation,
                    "options." + member,
                    $"ValidateOptions no longer checks {member}; update RequiredMembers to match.");
            }

            var checkedCount = validation
                .Split(new[] { "options." }, StringSplitOptions.None)
                .Length - 1;
            Assert.AreEqual(
                RequiredMembers.Length,
                checkedCount,
                "ValidateOptions checks a different number of members than RequiredMembers lists.");
        }

        private static List<string> FindCallSiteFiles()
        {
            var sourceRoot = Path.GetDirectoryName(FindRepoFile("source", "PlayniteAchievements.csproj"));
            return Directory
                .EnumerateFiles(sourceRoot, "*.xaml.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(path => File.ReadAllText(path).Contains("DataGridRowReorderBehavior.SetOptions"))
                .ToList();
        }

        private static string ExtractOptionsInitializer(string code)
        {
            var start = code.IndexOf("new DataGridRowReorderOptions", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "Expected a DataGridRowReorderOptions initializer.");

            var open = code.IndexOf('{', start);
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return code.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail("Unterminated DataGridRowReorderOptions initializer.");
            return null;
        }

        private static string ReadAssignedValue(string block, string member)
        {
            var marker = member + " = ";
            var index = block.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            var start = index + marker.Length;
            var end = block.IndexOfAny(new[] { ',', '\r', '\n' }, start);
            return end < 0 ? null : block.Substring(start, end - start).Trim();
        }

        private static string Between(string text, string from, string to)
        {
            var start = text.IndexOf(from, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find '{from}'.");
            var end = text.IndexOf(to, start, StringComparison.Ordinal);
            Assert.IsTrue(end >= 0, $"Could not find '{to}'.");
            return text.Substring(start, end - start);
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
