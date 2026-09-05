namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// The six unlock-sound slots. Four follow the rarity tiers; Hidden applies when the user opted
    /// into a distinct sound for hidden achievements; Capstone covers capstone achievements and the
    /// game-completed notification. The enum name doubles as the settings property name, and
    /// <see cref="UnlockSoundTierExtensions.ToFileBaseName"/> is the file basename every sound
    /// directory layout uses.
    /// </summary>
    public enum UnlockSoundTier
    {
        Common,
        Uncommon,
        Rare,
        UltraRare,
        Hidden,
        Capstone,
    }

    public static class UnlockSoundTierExtensions
    {
        /// <summary>Every tier in display order, for tables and preload sets.</summary>
        public static readonly UnlockSoundTier[] All =
        {
            UnlockSoundTier.Common,
            UnlockSoundTier.Uncommon,
            UnlockSoundTier.Rare,
            UnlockSoundTier.UltraRare,
            UnlockSoundTier.Hidden,
            UnlockSoundTier.Capstone,
        };

        /// <summary>
        /// Bare lowercase basename ("ultrarare", never "Ultra-Rare"): the name a theme or the
        /// bundled pack must use for this tier's sound file, in any supported extension.
        /// </summary>
        public static string ToFileBaseName(this UnlockSoundTier tier)
        {
            switch (tier)
            {
                case UnlockSoundTier.Uncommon: return "uncommon";
                case UnlockSoundTier.Rare: return "rare";
                case UnlockSoundTier.UltraRare: return "ultrarare";
                case UnlockSoundTier.Hidden: return "hidden";
                case UnlockSoundTier.Capstone: return "capstone";
                default: return "common";
            }
        }
    }
}
