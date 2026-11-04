using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass, DoNotParallelize]
    public class CefDebugTraceTests
    {
        [TestMethod]
        public void DisabledTracingPreservesResultsAndExceptions()
        {
            PollingDebugLog.Configure(null, false);
            var calls = 0;
            Assert.AreEqual(7, CefDebugTrace.Run("test", () => { calls++; return 7; }));
            Assert.AreEqual(1, calls);
            Assert.IsNull(CefDebugTrace.Begin("test"));
            var failure = new InvalidOperationException();
            var caught = Assert.ThrowsException<InvalidOperationException>(
                () => CefDebugTrace.Run<object>("test", () => throw failure));
            Assert.AreSame(failure, caught);
        }

        [TestMethod]
        public void BrowserEventsOutsidePollingIncludeCallerStackTimingAndError()
        {
            var folder = Path.Combine(Path.GetTempPath(), "cef-debug-" + Guid.NewGuid());
            try
            {
                PollingDebugLog.Configure(folder, true);
                CefDebugTrace.Run("create-offscreen", () => new object());
                Assert.ThrowsException<InvalidOperationException>(
                    () => CefDebugTrace.Run<object>("test-failure", () => throw new InvalidOperationException()));
                PollingDebugLog.Configure(null, false);
                var log = File.ReadAllText(Path.Combine(folder, "PollingDebug.log"));
                StringAssert.Contains(log, "poll=0");
                StringAssert.Contains(log, "cef.begin");
                StringAssert.Contains(log, "cef.end");
                StringAssert.Contains(log, "cef.result");
                StringAssert.Contains(log, "CefDebugTraceTests.cs");
                StringAssert.Contains(log, "stack=");
                StringAssert.Contains(log, "elapsedMs=");
                StringAssert.Contains(log, "operation=test-failure type=InvalidOperationException");
                Assert.AreEqual("example.com", CefDebugTrace.Host("https://example.com/private?token=secret"));
            }
            finally
            {
                PollingDebugLog.Configure(null, false);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
