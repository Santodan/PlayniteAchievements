using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.CustomProviders;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class CustomProviderKeysTests
    {
        [TestMethod]
        public void Build_And_TryGetId_RoundTrip()
        {
            var key = CustomProviderKeys.Build(" abc123 ");

            Assert.AreEqual("Custom:abc123", key);
            Assert.IsTrue(CustomProviderKeys.TryGetId(key, out var id));
            Assert.AreEqual("abc123", id);
            Assert.IsTrue(CustomProviderKeys.IsCustomProviderKey("custom:ABC"));
        }

        [TestMethod]
        public void BareCustom_IsNotACustomProviderKey()
        {
            Assert.IsFalse(CustomProviderKeys.TryGetId("Custom", out _));
            Assert.IsFalse(CustomProviderKeys.TryGetId("Custom:", out _));
            Assert.IsFalse(CustomProviderKeys.TryGetId("Steam", out _));
            Assert.IsTrue(CustomProviderKeys.IsBaseKey(" custom "));
            Assert.IsNull(CustomProviderKeys.Build("  "));
        }

        [TestMethod]
        public void IconKey_RoundTrip()
        {
            var iconKey = CustomProviderKeys.BuildIconKey("abc");

            Assert.AreEqual("ProviderIconCustom:abc", iconKey);
            Assert.IsTrue(CustomProviderKeys.TryGetIdFromIconKey(iconKey, out var id));
            Assert.AreEqual("abc", id);
            Assert.IsFalse(CustomProviderKeys.TryGetIdFromIconKey("ProviderIconManual", out _));
        }

    }
}
