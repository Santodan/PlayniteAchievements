using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class ForkReleaseStartupTests
    {
        // The plugin cannot be linked into the isolated test project. Guard the
        // startup integration that was lost during the upstream plugin rewrite.
        [TestMethod]
        public void PluginConstructsAndStartsForkReleaseMonitor()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "source", "PlayniteAchievementsPlugin.cs")))
            {
                directory = directory.Parent;
            }

            Assert.IsNotNull(directory, "Repository source must be available for the integration check.");
            var source = File.ReadAllText(Path.Combine(directory.FullName, "source", "PlayniteAchievementsPlugin.cs"));
            var constructor = source.IndexOf("_forkReleaseMonitor = new Services.ReleaseMonitoring.ForkReleaseMonitor(", StringComparison.Ordinal);
            var startup = source.IndexOf("public override void OnApplicationStarted(", StringComparison.Ordinal);
            Assert.IsTrue(constructor >= 0 && constructor < startup, "Construct the monitor before startup.");
            StringAssert.Contains(source.Substring(constructor, startup - constructor), "() => PersistSettingsForUiSilently()");

            var nextOverride = source.IndexOf("public override", startup + 1, StringComparison.Ordinal);
            var startupSource = nextOverride < 0 ? source.Substring(startup) : source.Substring(startup, nextOverride - startup);
            StringAssert.Contains(startupSource, "Task.Run(() => _forkReleaseMonitor?.CheckForForkReleaseAsync()");
            Assert.IsFalse(source.Contains("new Services.ReleaseMonitoring.UpstreamReleaseMonitor("), "Fork startup must not enable upstream release notifications.");
        }
    }
}
