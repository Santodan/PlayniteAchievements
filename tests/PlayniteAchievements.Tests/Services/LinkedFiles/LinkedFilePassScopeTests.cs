using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using System.Linq;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class LinkedFilePassScopeTests
    {
        [TestMethod]
        public void LibraryRefreshes_ReadEveryLinkedGame()
        {
            var scope = LinkedFilePassScope.FromRequest(RefreshModeType.Full, null, new RefreshOptions { Scope = RefreshGameScope.All }, null);

            Assert.IsTrue(scope.IsLibraryWide);
            Assert.IsTrue(scope.RespectExclusions);
        }

        [TestMethod]
        public void NamedGames_ReadOnlyThoseAndFollowTheExplicitIncludeRule()
        {
            var single = Guid.NewGuid();
            var named = Guid.NewGuid();

            var scope = LinkedFilePassScope.FromRequest(RefreshModeType.Single, single, new RefreshOptions(), new[] { named, Guid.Empty, single });

            Assert.IsFalse(scope.IsLibraryWide);
            CollectionAssert.AreEquivalent(new[] { single, named }, scope.GameIds.ToArray());
            Assert.IsFalse(scope.RespectExclusions, "Explicit includes bypass exclusions by default, as the provider refresh does.");

            var monitorStyle = LinkedFilePassScope.FromRequest(
                RefreshModeType.Custom,
                null,
                new RefreshOptions { Scope = RefreshGameScope.Explicit, PlayniteGameIds = new[] { named }, ForceBypassExclusionsForExplicitIncludes = false },
                null);
            CollectionAssert.AreEqual(new[] { named }, monitorStyle.GameIds.ToArray());
            Assert.IsTrue(monitorStyle.RespectExclusions);
        }

        [TestMethod]
        public void FriendsOnlyRefreshes_ReadNothing()
        {
            Assert.IsNull(LinkedFilePassScope.FromRequest(RefreshModeType.FriendsFull, null, null, null));
            Assert.IsNull(LinkedFilePassScope.FromRequest(RefreshModeType.Custom, null, new RefreshOptions { Subjects = RefreshSubjects.Friends }, null));
            Assert.IsNotNull(LinkedFilePassScope.FromRequest(RefreshModeType.Custom, null, new RefreshOptions { Subjects = RefreshSubjects.All }, null));
        }
    }
}
