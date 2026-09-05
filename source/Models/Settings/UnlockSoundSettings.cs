using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// The user's own sound file per <see cref="UnlockSoundTier"/>. A blank slot means "not
    /// customized": the resolver then falls back to the active theme's sound, and finally to the
    /// bundled default. Setters only trim and blank-normalize; whether the file exists is checked
    /// at resolve time so a temporarily missing drive does not erase the user's choice.
    /// </summary>
    public sealed class UnlockSoundSettings : ObservableObject
    {
        private string _common;
        private string _uncommon;
        private string _rare;
        private string _ultraRare;
        private string _hidden;
        private string _capstone;

        public string Common
        {
            get => _common;
            set => SetValue(ref _common, Normalize(value));
        }

        public string Uncommon
        {
            get => _uncommon;
            set => SetValue(ref _uncommon, Normalize(value));
        }

        public string Rare
        {
            get => _rare;
            set => SetValue(ref _rare, Normalize(value));
        }

        public string UltraRare
        {
            get => _ultraRare;
            set => SetValue(ref _ultraRare, Normalize(value));
        }

        public string Hidden
        {
            get => _hidden;
            set => SetValue(ref _hidden, Normalize(value));
        }

        public string Capstone
        {
            get => _capstone;
            set => SetValue(ref _capstone, Normalize(value));
        }

        public string GetPath(UnlockSoundTier tier)
        {
            switch (tier)
            {
                case UnlockSoundTier.Uncommon: return Uncommon;
                case UnlockSoundTier.Rare: return Rare;
                case UnlockSoundTier.UltraRare: return UltraRare;
                case UnlockSoundTier.Hidden: return Hidden;
                case UnlockSoundTier.Capstone: return Capstone;
                default: return Common;
            }
        }

        public void SetPath(UnlockSoundTier tier, string path)
        {
            switch (tier)
            {
                case UnlockSoundTier.Uncommon: Uncommon = path; break;
                case UnlockSoundTier.Rare: Rare = path; break;
                case UnlockSoundTier.UltraRare: UltraRare = path; break;
                case UnlockSoundTier.Hidden: Hidden = path; break;
                case UnlockSoundTier.Capstone: Capstone = path; break;
                default: Common = path; break;
            }
        }

        public static UnlockSoundSettings CreateDefault()
        {
            return new UnlockSoundSettings();
        }

        public UnlockSoundSettings Clone()
        {
            return new UnlockSoundSettings
            {
                Common = Common,
                Uncommon = Uncommon,
                Rare = Rare,
                UltraRare = UltraRare,
                Hidden = Hidden,
                Capstone = Capstone,
            };
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
