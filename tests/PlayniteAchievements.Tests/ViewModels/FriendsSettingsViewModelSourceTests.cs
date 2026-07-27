using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class FriendsSettingsViewModelSourceTests
    {
        [TestMethod]
        public void FriendsSettings_MirrorsExophaseRowsIntoProviderSettingsBeforePersist()
        {
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "FriendsSettingsViewModel.cs"));

            StringAssert.Contains(code, "SyncExophaseProviderFriends(providerKey)");
            StringAssert.Contains(code, "exophaseSettings.Friends = friends");
            StringAssert.Contains(code, "_providerRegistry?.Save(exophaseSettings, persistToDisk: false)");
        }

        [TestMethod]
        public void FriendsSettings_MergedNativeProviderAndExophaseDisablesMatchingExophasePlatform()
        {
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "FriendsSettingsViewModel.cs"));

            StringAssert.Contains(code, "ApplyExophasePlatformConflicts()");
            StringAssert.Contains(code, "disabled.Add(\"steam\")");
            StringAssert.Contains(code, "disabled.Add(\"retro\")");
            StringAssert.Contains(code, "string.Equals(token, \"steam\", StringComparison.OrdinalIgnoreCase)");
            StringAssert.Contains(code, "string.Equals(token, \"retro\", StringComparison.OrdinalIgnoreCase)");
        }

        [TestMethod]
        public void FriendsSettings_UnmergeActionBindsToGridCommandAndRefreshesCanExecute()
        {
            var xaml = File.ReadAllText(FindRepoFile("source", "Views", "FriendsSettingsTab.xaml"));
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "FriendsSettingsViewModel.cs"));

            StringAssert.Contains(xaml, "DataContext.UnmergeFriendCommand, RelativeSource={RelativeSource AncestorType={x:Type DataGrid}}");
            StringAssert.Contains(xaml, "IsEnabled=\"{Binding CanUnmerge}\"");
            StringAssert.Contains(code, "(UnmergeFriendCommand as RelayCommand)?.RaiseCanExecuteChanged();");
        }

        [TestMethod]
        public void FriendsSettings_OffersRetroAchievementsAutoDiscoveryWhenAuthenticated()
        {
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "FriendsSettingsViewModel.cs"));

            StringAssert.Contains(code, "RetroAchievementsProviderKey = \"RetroAchievements\"");
            StringAssert.Contains(code, "MergeCachedFriends(_settings.Persisted, _friendCache, RetroAchievementsProviderKey)");
            StringAssert.Contains(code, "AutoDiscoverProviders.Add(new FriendAutoDiscoverProviderItem(");
            StringAssert.Contains(code, "RetroAchievementsProviderKey,");
            StringAssert.Contains(code, "string.Equals(providerKey, RetroAchievementsProviderKey, StringComparison.OrdinalIgnoreCase)");
            StringAssert.Contains(code, "return provider.IsAuthenticated;");
        }

        [TestMethod]
        public void RetroAchievementsDataProvider_ExposesFriendsProvider()
        {
            var code = File.ReadAllText(FindRepoFile("source", "Providers", "RetroAchievements", "RetroAchievementsDataProvider.cs"));

            StringAssert.Contains(code, "RetroAchievementsFriendsProvider _friendsProvider");
            StringAssert.Contains(code, "public PlayniteAchievements.Models.Friends.IFriendsProvider Friends =>");
            StringAssert.Contains(code, "new RetroAchievementsFriendsProvider");
        }

        [TestMethod]
        public void RetroAchievementsFriendsProvider_FoldsSubsetOwnershipIntoBaseGames()
        {
            var code = File.ReadAllText(FindRepoFile("source", "Providers", "RetroAchievements", "RetroAchievementsFriendsProvider.cs"));

            StringAssert.Contains(code, "BuildOwnedGameRowsAsync");
            StringAssert.Contains(code, "ResolveSubsetBaseMappingsAsync");
            StringAssert.Contains(code, "GetBaseGamesForSubsetAsync");
            StringAssert.Contains(code, "ParentGameId");
            StringAssert.Contains(code, "RetroAchievementsSubsetTitleResolver.IsSubsetLikeTitle");
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar.ToString(), parts));
        }
    }
}
