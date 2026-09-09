using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Epic;
using System;

namespace PlayniteAchievements.Epic.Tests
{
    [TestClass]
    public class EpicSessionStateTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 2, 19, 18, 49, DateTimeKind.Utc);
        private static readonly TimeSpan Buffer = TimeSpan.FromMinutes(5);

        [TestMethod]
        public void IsUsable_ValidAccessToken_ReturnsTrue()
        {
            Assert.IsTrue(EpicSessionState.IsUsable(
                "access", Now.AddHours(1),
                null, DateTime.MinValue,
                Now, Buffer));
        }

        [TestMethod]
        public void IsUsable_ExpiredAccessTokenWithValidRefreshToken_ReturnsTrue()
        {
            // The reported case: the access token lapsed during the day, the refresh token is good
            // for days, and no user action is needed to renew.
            Assert.IsTrue(EpicSessionState.IsUsable(
                "access", Now.AddHours(-1),
                "refresh", Now.AddDays(7),
                Now, Buffer));
        }

        [TestMethod]
        public void IsUsable_AccessTokenInsideExpiryBufferWithValidRefreshToken_ReturnsTrue()
        {
            Assert.IsFalse(EpicSessionState.HasValidAccessToken("access", Now.AddMinutes(2), Now, Buffer));
            Assert.IsTrue(EpicSessionState.IsUsable(
                "access", Now.AddMinutes(2),
                "refresh", Now.AddDays(7),
                Now, Buffer));
        }

        [TestMethod]
        public void IsUsable_BothTokensExpired_ReturnsFalse()
        {
            Assert.IsFalse(EpicSessionState.IsUsable(
                "access", Now.AddHours(-1),
                "refresh", Now.AddMinutes(-1),
                Now, Buffer));
        }

        [TestMethod]
        public void IsUsable_NoTokens_ReturnsFalse()
        {
            Assert.IsFalse(EpicSessionState.IsUsable(
                null, DateTime.MinValue,
                null, DateTime.MinValue,
                Now, Buffer));
            Assert.IsFalse(EpicSessionState.IsUsable(
                "   ", Now.AddHours(1),
                string.Empty, Now.AddDays(7),
                Now, Buffer));
        }

        [TestMethod]
        public void HasValidRefreshToken_ExpiredRefreshToken_ReturnsFalse()
        {
            Assert.IsFalse(EpicSessionState.HasValidRefreshToken("refresh", Now, Now));
            Assert.IsTrue(EpicSessionState.HasValidRefreshToken("refresh", Now.AddSeconds(1), Now));
        }
    }
}
