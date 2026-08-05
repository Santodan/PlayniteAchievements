using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class FriendDisplayNameResolverTests
    {
        [TestMethod]
        public void Resolve_ManualNicknameWinsOverEverything()
        {
            Assert.AreEqual(
                "Bestie",
                FriendDisplayNameResolver.Resolve("Bestie", "Persona", "Nick", FriendNameDisplayMode.PersonaAndNickname, "id"));
            Assert.AreEqual(
                "Bestie",
                FriendDisplayNameResolver.Resolve("Bestie", null, null, FriendNameDisplayMode.Persona, "id"));
        }

        [TestMethod]
        public void Resolve_WithoutNicknameReturnsPersonaInEveryMode()
        {
            foreach (FriendNameDisplayMode mode in System.Enum.GetValues(typeof(FriendNameDisplayMode)))
            {
                Assert.AreEqual(
                    "Persona",
                    FriendDisplayNameResolver.Resolve(null, "Persona", null, mode, "id"));
            }
        }

        [TestMethod]
        public void Resolve_AppliesModeWhenBothNamesExist()
        {
            Assert.AreEqual(
                "Persona",
                FriendDisplayNameResolver.Resolve(null, "Persona", "Nick", FriendNameDisplayMode.Persona, "id"));
            Assert.AreEqual(
                "Nick",
                FriendDisplayNameResolver.Resolve(null, "Persona", "Nick", FriendNameDisplayMode.Nickname, "id"));
            Assert.AreEqual(
                "Persona (Nick)",
                FriendDisplayNameResolver.Resolve(null, "Persona", "Nick", FriendNameDisplayMode.PersonaAndNickname, "id"));
        }

        [TestMethod]
        public void Resolve_CollapsesEqualPersonaAndNickname()
        {
            // A failed persona lookup leaves the nickname in both fields; never show "Nick (Nick)".
            Assert.AreEqual(
                "Nick",
                FriendDisplayNameResolver.Resolve(null, "Nick", "nick", FriendNameDisplayMode.PersonaAndNickname, "id"));
        }

        [TestMethod]
        public void Resolve_NicknameWithoutPersonaFallsBackToFallbackThenNickname()
        {
            // Fallback id stands in for the persona name.
            Assert.AreEqual(
                "id (Nick)",
                FriendDisplayNameResolver.Resolve(null, null, "Nick", FriendNameDisplayMode.PersonaAndNickname, "id"));
            Assert.AreEqual(
                "Nick",
                FriendDisplayNameResolver.Resolve(null, null, "Nick", FriendNameDisplayMode.PersonaAndNickname, null));
        }

        [TestMethod]
        public void Resolve_EverythingMissingReturnsNull()
        {
            Assert.IsNull(FriendDisplayNameResolver.Resolve(null, " ", null, FriendNameDisplayMode.PersonaAndNickname, "  "));
        }

        [TestMethod]
        public void Resolve_IdentityOverloadPrefersIdentityValuesOverEntry()
        {
            var identity = new FriendIdentity
            {
                ProviderKey = "Steam",
                ExternalUserId = "id",
                DisplayName = "Fresh Persona",
                ProviderNickname = "Fresh Nick"
            };
            var entry = new FriendSettingsEntry
            {
                ProviderKey = "Steam",
                ExternalUserId = "id",
                DisplayName = "Stale Persona",
                ProviderNickname = "Stale Nick"
            };

            Assert.AreEqual(
                "Fresh Persona (Fresh Nick)",
                FriendDisplayNameResolver.Resolve(identity, entry, FriendNameDisplayMode.PersonaAndNickname));

            entry.Nickname = "Bestie";
            Assert.AreEqual(
                "Bestie",
                FriendDisplayNameResolver.Resolve(identity, entry, FriendNameDisplayMode.PersonaAndNickname));
        }
    }
}
