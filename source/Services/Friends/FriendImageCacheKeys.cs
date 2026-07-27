namespace PlayniteAchievements.Services.Friends
{
    /// <summary>
    /// Top-level <c>DiskImageService</c> cache subfolders for friend imagery. Files are named
    /// stably by identity (see <see cref="FriendImageCachePathBuilder"/>): avatars by
    /// provider+user, and unowned game covers/icons/achievement icons under
    /// <c>{provider}/{gameKey}/</c> subfolders keyed by game and achievement rather than by source
    /// URL. A single cached copy is therefore shared across every friend that references the same
    /// game/achievement. Clearing unowned data removes the whole <see cref="Games"/> tree in one
    /// call (including any legacy URL-hash files left flat in the folder).
    /// </summary>
    public static class FriendImageCacheFolders
    {
        public const string Avatars = "friendavatars";
        public const string Games = "friendgames";
    }
}
