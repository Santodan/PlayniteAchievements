using System;
using System.Threading;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Tests.TestInfrastructure;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class MarkdownEditingTests
    {
        [TestMethod]
        public void ToggleMarker_WrapsTheSelectionAndKeepsItSelected()
        {
            StaTestHost.Run(() =>
            {
                var box = Box("say hello now", 4, 5);

                MarkdownEditing.ToggleMarker(box, "**");

                Assert.AreEqual("say **hello** now", box.Text);
                Assert.AreEqual("hello", box.SelectedText);
            });
        }

        [TestMethod]
        public void ToggleMarker_UnwrapsMarkersAroundOrInsideTheSelection()
        {
            StaTestHost.Run(() =>
            {
                var around = Box("say **hello** now", 6, 5);
                MarkdownEditing.ToggleMarker(around, "**");
                Assert.AreEqual("say hello now", around.Text);
                Assert.AreEqual("hello", around.SelectedText);

                var inside = Box("say ~~hello~~ now", 4, 9);
                MarkdownEditing.ToggleMarker(inside, "~~");
                Assert.AreEqual("say hello now", inside.Text);
                Assert.AreEqual("hello", inside.SelectedText);
            });
        }

        [TestMethod]
        public void ToggleMarker_ItalicNeverTakesHalfOfABoldMarker()
        {
            StaTestHost.Run(() =>
            {
                var bold = Box("**hello**", 2, 5);
                MarkdownEditing.ToggleMarker(bold, "*");
                Assert.AreEqual("***hello***", bold.Text, "italic is added inside bold");

                MarkdownEditing.ToggleMarker(bold, "*");
                Assert.AreEqual("**hello**", bold.Text, "and toggles back off");
            });
        }

        [TestMethod]
        public void ToggleMarker_RespectsTheMaximumLength()
        {
            StaTestHost.Run(() =>
            {
                var box = Box("abc", 0, 3);

                MarkdownEditing.ToggleMarker(box, "**", maxLength: 6);

                Assert.AreEqual("abc", box.Text);
            });
        }

        [TestMethod]
        public void InsertLink_UsesTheSelectionAsLabelAndSelectsTheAddress()
        {
            StaTestHost.Run(() =>
            {
                var box = Box("see docs", 4, 4);

                MarkdownEditing.InsertLink(box);

                Assert.AreEqual("see [docs](https://)", box.Text);
                Assert.AreEqual("https://", box.SelectedText);

                var empty = Box("x", 1, 0);
                MarkdownEditing.InsertLink(empty);
                Assert.AreEqual("x[](https://)", empty.Text);
                Assert.AreEqual(2, empty.SelectionStart);
            });
        }

        private static TextBox Box(string text, int start, int length)
        {
            var box = new TextBox { Text = text };
            box.Select(start, length);
            return box;
        }
    }
}
