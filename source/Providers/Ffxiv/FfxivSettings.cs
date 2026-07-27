using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Ffxiv
{
    /// <summary>
    /// Final Fantasy XIV provider settings. Achievement data is sourced from the
    /// FFXIV Collect API for the configured character.
    /// </summary>
    public class FfxivSettings : ProviderSettingsBase
    {
        private string _characterName;
        private string _world;
        private string _region = "na";
        private long _resolvedCharacterId;

        /// <inheritdoc />
        public override string ProviderKey => "FFXIV";

        /// <summary>
        /// In-game character name (e.g. "Raelys Skyborn").
        /// </summary>
        public string CharacterName
        {
            get => _characterName;
            set => SetValue(ref _characterName, value);
        }

        /// <summary>
        /// Home world name (e.g. "Behemoth"). Used to disambiguate the Lodestone
        /// character search.
        /// </summary>
        public string World
        {
            get => _world;
            set => SetValue(ref _world, value);
        }

        /// <summary>
        /// Lodestone region subdomain used for character search: na, eu, de, fr, jp.
        /// </summary>
        public string Region
        {
            get => _region;
            set => SetValue(ref _region, string.IsNullOrWhiteSpace(value) ? "na" : value.Trim().ToLowerInvariant());
        }

        /// <summary>
        /// Cached Lodestone character id resolved from <see cref="CharacterName"/>
        /// and <see cref="World"/>. Zero when not yet resolved.
        /// </summary>
        public long ResolvedCharacterId
        {
            get => _resolvedCharacterId;
            set => SetValue(ref _resolvedCharacterId, value);
        }
    }
}
