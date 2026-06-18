using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class AchievementHotkeyGestureTests
    {
        [TestMethod]
        public void TryParse_AcceptsSupportedHotkeys()
        {
            AssertCanonical("Ctrl+Alt+V", "Ctrl+Alt+V");
            AssertCanonical("Control+Alt+v", "Ctrl+Alt+V");
            AssertCanonical("V", "V");
            AssertCanonical("5", "5");
            AssertCanonical("D5", "5");
            AssertCanonical("NumPad5", "NumPad5");
            AssertCanonical("F8", "F8");
            AssertCanonical("Shift+F12", "Shift+F12");
        }

        [TestMethod]
        public void TryParse_RejectsUnsupportedHotkeys()
        {
            Assert.IsFalse(AchievementHotkeyGesture.TryParse("Ctrl", out _));
            Assert.IsFalse(AchievementHotkeyGesture.TryParse("Ctrl+Alt", out _));
            Assert.IsFalse(AchievementHotkeyGesture.TryParse("Space", out _));
            Assert.IsFalse(AchievementHotkeyGesture.TryParse("Mouse1", out _));
        }

        [TestMethod]
        public void CanRegisterGlobally_AllowsModifierCombosAndFunctionKeysOnly()
        {
            Assert.IsTrue(Parse("Ctrl+Alt+V").CanRegisterGlobally);
            Assert.IsTrue(Parse("F8").CanRegisterGlobally);
            Assert.IsFalse(Parse("V").CanRegisterGlobally);
            Assert.IsFalse(Parse("5").CanRegisterGlobally);
        }

        [TestMethod]
        public void Equals_DetectsDuplicateAssignments()
        {
            var first = Parse("Ctrl+Alt+V");
            var duplicate = Parse("Control+Alt+v");
            var different = Parse("Ctrl+Alt+M");

            Assert.AreEqual(first, duplicate);
            Assert.AreNotEqual(first, different);
        }

        private static void AssertCanonical(string input, string expected)
        {
            Assert.IsTrue(AchievementHotkeyGesture.TryParse(input, out var gesture), input);
            Assert.IsNotNull(gesture, input);
            Assert.AreEqual(expected, gesture.ToString(), input);
        }

        private static AchievementHotkeyGesture Parse(string input)
        {
            Assert.IsTrue(AchievementHotkeyGesture.TryParse(input, out var gesture), input);
            Assert.IsNotNull(gesture, input);
            return gesture;
        }
    }
}
