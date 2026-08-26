using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RetroAchievements;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class ConsoleMappingRegistryTests
    {
        [DataTestMethod]
        [DataRow("Microsoft MSX")]
        [DataRow("Microsoft MSX2")]
        [DataRow("microsoft_msx")]
        [DataRow("microsoft_msx2")]
        public void TryResolve_MsxVariants_ResolveToConsole29(string platform)
        {
            var resolved = ConsoleMappingRegistry.Instance.TryResolve(platform, out var consoleId);

            Assert.IsTrue(resolved, $"'{platform}' should resolve to an RA console.");
            Assert.AreEqual(29, consoleId, $"'{platform}' should resolve to the MSX console.");
        }

        [TestMethod]
        public void TryResolve_UnknownPlatform_ReturnsFalse()
        {
            var resolved = ConsoleMappingRegistry.Instance.TryResolve("Some Unknown Handheld", out var consoleId);

            Assert.IsFalse(resolved);
            Assert.AreEqual(0, consoleId);
        }
    }
}
