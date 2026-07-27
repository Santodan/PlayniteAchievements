using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models.Settings
{
    public sealed class RarityColorSettings : ObservableObject
    {
        public const string DefaultCommon = "#B56A37";
        public const string DefaultUncommon = "#A6B1BF";
        public const string DefaultRare = "#CF9B1F";
        public const string DefaultUltraRare = "#86C8FF";
        public const string DefaultCompletedStart = "#9C27B0";
        public const string DefaultCompletedEnd = "#2196F3";
        public const string DefaultTrophyBronze = "#B56A37";
        public const string DefaultTrophySilver = "#A6B1BF";
        public const string DefaultTrophyGold = "#CF9B1F";
        public const string DefaultTrophyPlatinum = "#86C8FF";

        private string _common = DefaultCommon;
        private string _uncommon = DefaultUncommon;
        private string _rare = DefaultRare;
        private string _ultraRare = DefaultUltraRare;
        private string _completedStart = DefaultCompletedStart;
        private string _completedEnd = DefaultCompletedEnd;
        private string _trophyBronze = DefaultTrophyBronze;
        private string _trophySilver = DefaultTrophySilver;
        private string _trophyGold = DefaultTrophyGold;
        private string _trophyPlatinum = DefaultTrophyPlatinum;

        public string Common
        {
            get => _common;
            set => SetValue(ref _common, Normalize(value, DefaultCommon));
        }

        public string Uncommon
        {
            get => _uncommon;
            set => SetValue(ref _uncommon, Normalize(value, DefaultUncommon));
        }

        public string Rare
        {
            get => _rare;
            set => SetValue(ref _rare, Normalize(value, DefaultRare));
        }

        public string UltraRare
        {
            get => _ultraRare;
            set => SetValue(ref _ultraRare, Normalize(value, DefaultUltraRare));
        }

        public string CompletedStart
        {
            get => _completedStart;
            set => SetValue(ref _completedStart, Normalize(value, DefaultCompletedStart));
        }

        public string CompletedEnd
        {
            get => _completedEnd;
            set => SetValue(ref _completedEnd, Normalize(value, DefaultCompletedEnd));
        }

        public string TrophyBronze
        {
            get => _trophyBronze;
            set => SetValue(ref _trophyBronze, Normalize(value, DefaultTrophyBronze));
        }

        public string TrophySilver
        {
            get => _trophySilver;
            set => SetValue(ref _trophySilver, Normalize(value, DefaultTrophySilver));
        }

        public string TrophyGold
        {
            get => _trophyGold;
            set => SetValue(ref _trophyGold, Normalize(value, DefaultTrophyGold));
        }

        public string TrophyPlatinum
        {
            get => _trophyPlatinum;
            set => SetValue(ref _trophyPlatinum, Normalize(value, DefaultTrophyPlatinum));
        }

        public static RarityColorSettings CreateDefault()
        {
            return new RarityColorSettings();
        }

        public RarityColorSettings Clone()
        {
            return new RarityColorSettings
            {
                Common = Common,
                Uncommon = Uncommon,
                Rare = Rare,
                UltraRare = UltraRare,
                CompletedStart = CompletedStart,
                CompletedEnd = CompletedEnd,
                TrophyBronze = TrophyBronze,
                TrophySilver = TrophySilver,
                TrophyGold = TrophyGold,
                TrophyPlatinum = TrophyPlatinum
            };
        }

        private static string Normalize(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }
}
