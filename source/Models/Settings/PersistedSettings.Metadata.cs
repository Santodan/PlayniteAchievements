using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>Settings for the Metadata tab: which metadata providers may supply data to games another provider services.</summary>
    public partial class PersistedSettings
    {
        private List<string> _metadataIconSources = new List<string>();
        private List<string> _metadataCapstoneArtSources = new List<string>();
        private bool _metadataAllowNameSearch = true;

        /// <summary>
        /// Metadata provider keys whose achievement icons replace the servicing provider's, in
        /// preference order. Empty keeps every provider's own icons.
        /// </summary>
        public List<string> MetadataIconSources
        {
            get => _metadataIconSources;
            set => SetValue(ref _metadataIconSources, value ?? new List<string>());
        }

        /// <summary>
        /// Metadata provider keys whose capstone art (a PlayStation platinum) an authored auto
        /// capstone uses, in preference order. Empty uses the game's icon or cover.
        /// </summary>
        public List<string> MetadataCapstoneArtSources
        {
            get => _metadataCapstoneArtSources;
            set => SetValue(ref _metadataCapstoneArtSources, value ?? new List<string>());
        }

        /// <summary>
        /// Lets metadata providers match a game by searching its name when no id is known. Off
        /// limits matching to ids the game already carries and links the user entered.
        /// </summary>
        public bool MetadataAllowNameSearch
        {
            get => _metadataAllowNameSearch;
            set => SetValue(ref _metadataAllowNameSearch, value);
        }
    }
}
