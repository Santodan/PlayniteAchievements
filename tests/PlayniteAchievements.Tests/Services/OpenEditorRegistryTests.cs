using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class OpenEditorRegistryTests
    {
        [TestMethod]
        public void AllClosed_FiresOnlyWhenTheLastEditorOnAnyGameCloses()
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var fired = 0;
            Action handler = () => fired++;
            OpenEditorRegistry.AllClosed += handler;
            try
            {
                OpenEditorRegistry.Open(first);
                OpenEditorRegistry.Open(second);
                Assert.IsTrue(OpenEditorRegistry.IsAnyOpen);

                OpenEditorRegistry.Close(first);
                Assert.AreEqual(0, fired);
                Assert.IsTrue(OpenEditorRegistry.IsAnyOpen);

                OpenEditorRegistry.Close(second);
                Assert.AreEqual(1, fired);
                Assert.IsFalse(OpenEditorRegistry.IsAnyOpen);
            }
            finally
            {
                OpenEditorRegistry.AllClosed -= handler;
                OpenEditorRegistry.Close(first);
                OpenEditorRegistry.Close(second);
            }
        }

        [TestMethod]
        public void AllClosed_WaitsForEveryEditorOnTheSameGame()
        {
            var game = Guid.NewGuid();
            var fired = 0;
            Action handler = () => fired++;
            OpenEditorRegistry.AllClosed += handler;
            try
            {
                OpenEditorRegistry.Open(game);
                OpenEditorRegistry.Open(game);

                OpenEditorRegistry.Close(game);
                Assert.AreEqual(0, fired);
                Assert.IsTrue(OpenEditorRegistry.IsOpen(game));

                OpenEditorRegistry.Close(game);
                Assert.AreEqual(1, fired);
            }
            finally
            {
                OpenEditorRegistry.AllClosed -= handler;
                OpenEditorRegistry.Close(game);
            }
        }

        [TestMethod]
        public void Close_OnAGameWithNoOpenEditor_DoesNotFireAllClosed()
        {
            var fired = 0;
            Action handler = () => fired++;
            OpenEditorRegistry.AllClosed += handler;
            try
            {
                OpenEditorRegistry.Close(Guid.NewGuid());
                Assert.AreEqual(0, fired);
            }
            finally
            {
                OpenEditorRegistry.AllClosed -= handler;
            }
        }
    }
}
