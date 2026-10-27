using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class OpenEditorRegistryTests
    {
        [TestMethod]
        public void NotifyBackgroundWrite_NamesTheGameAndSurvivesAThrowingListener()
        {
            var game = Guid.NewGuid();
            Guid? seen = null;
            Action<Guid> throwing = _ => throw new InvalidOperationException();
            Action<Guid> recording = gameId => seen = gameId;
            OpenEditorRegistry.BackgroundWritten += recording;
            try
            {
                OpenEditorRegistry.NotifyBackgroundWrite(game);
                Assert.AreEqual(game, seen);

                OpenEditorRegistry.BackgroundWritten += throwing;
                OpenEditorRegistry.NotifyBackgroundWrite(Guid.NewGuid());

                seen = null;
                OpenEditorRegistry.NotifyBackgroundWrite(Guid.Empty);
                Assert.IsNull(seen);
            }
            finally
            {
                OpenEditorRegistry.BackgroundWritten -= recording;
                OpenEditorRegistry.BackgroundWritten -= throwing;
            }
        }

        [TestMethod]
        public void DeferWhileOpen_IsFalseWithNoEditor()
        {
            var ran = 0;

            Assert.IsFalse(OpenEditorRegistry.DeferWhileOpen(Guid.NewGuid(), new object(), () => ran++));
            Assert.AreEqual(0, ran);
        }

        [TestMethod]
        public void DeferWhileOpen_RunsOncePerWriterWhenTheEditorCloses()
        {
            var game = Guid.NewGuid();
            var writer = new object();
            var other = new object();
            var writerRuns = 0;
            var otherRuns = 0;
            OpenEditorRegistry.Open(game);
            try
            {
                Assert.IsTrue(OpenEditorRegistry.DeferWhileOpen(game, writer, () => writerRuns++));
                Assert.IsTrue(OpenEditorRegistry.DeferWhileOpen(game, writer, () => writerRuns++));
                Assert.IsTrue(OpenEditorRegistry.DeferWhileOpen(game, other, () => otherRuns++));
                Assert.AreEqual(0, writerRuns);
            }
            finally
            {
                OpenEditorRegistry.Close(game);
            }

            Assert.AreEqual(1, writerRuns);
            Assert.AreEqual(1, otherRuns);

            // Run once: closing again with nothing deferred runs nothing more.
            OpenEditorRegistry.Open(game);
            OpenEditorRegistry.Close(game);
            Assert.AreEqual(1, writerRuns);
        }

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
