namespace PlayniteAchievements.Services.Database.Rows
{
    internal sealed class GameRow
    {
        public long Id { get; set; }
        public string ProviderKey { get; set; }
        public string ProviderPlatformKey { get; set; }
        public long? ProviderGameId { get; set; }
        public string PlayniteGameId { get; set; }
        public string GameName { get; set; }
        public string LibrarySourceName { get; set; }
        public string FirstSeenUtc { get; set; }
        public string LastUpdatedUtc { get; set; }
    }
}
