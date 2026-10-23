namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>A page a name search offered: its id on the source and its game title there.</summary>
    internal sealed class MetadataPageCandidate
    {
        public MetadataPageCandidate(string id, string title, string origin = null)
        {
            Id = id;
            Title = title;
            Origin = origin;
        }

        public string Id { get; }

        public string Title { get; }

        /// <summary>How the source offered it ("search #2", "built name"), for the lookup's trace.</summary>
        public string Origin { get; }
    }
}
