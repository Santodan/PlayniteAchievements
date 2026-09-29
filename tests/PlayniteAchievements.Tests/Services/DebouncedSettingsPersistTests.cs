using System;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Settings;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class DebouncedSettingsPersistTests
    {
        [TestMethod]
        public void Schedule_ThenFlush_PersistsOnce()
        {
            RunOnStaThread(() =>
            {
                var persistCount = 0;
                using (var persist = new DebouncedSettingsPersist(new Border(), () => persistCount++))
                {
                    persist.Schedule();
                    persist.Schedule();
                    persist.Flush();
                }

                Assert.AreEqual(1, persistCount);
            });
        }

        [TestMethod]
        public void Schedule_ThenCancel_ThenFlush_DoesNotPersist()
        {
            RunOnStaThread(() =>
            {
                var persistCount = 0;
                using (var persist = new DebouncedSettingsPersist(new Border(), () => persistCount++))
                {
                    persist.Schedule();
                    persist.Cancel();
                    persist.Flush();
                }

                Assert.AreEqual(0, persistCount);
            });
        }

        [TestMethod]
        public void Schedule_ThenFlush_WhileDeferred_DoesNotPersist()
        {
            RunOnStaThread(() =>
            {
                var persistCount = 0;
                using (var persist = new DebouncedSettingsPersist(new Border(), () => persistCount++, () => true))
                {
                    persist.Schedule();
                    persist.Flush();
                }

                Assert.AreEqual(0, persistCount);
            });
        }

        [TestMethod]
        public void Flush_WithoutSchedule_DoesNotPersist()
        {
            RunOnStaThread(() =>
            {
                var persistCount = 0;
                using (var persist = new DebouncedSettingsPersist(new Border(), () => persistCount++))
                {
                    persist.Flush();
                }

                Assert.AreEqual(0, persistCount);
            });
        }

        [TestMethod]
        public void Schedule_AfterDispose_DoesNotPersistOnFlush()
        {
            RunOnStaThread(() =>
            {
                var persistCount = 0;
                var persist = new DebouncedSettingsPersist(new Border(), () => persistCount++);
                persist.Dispose();

                persist.Schedule();
                persist.Flush();

                Assert.AreEqual(0, persistCount);
            });
        }

        private static void RunOnStaThread(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex is TargetInvocationException invocationException && invocationException.InnerException != null
                        ? invocationException.InnerException
                        : ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (error != null)
            {
                throw new AssertFailedException(error.ToString());
            }
        }
    }
}
