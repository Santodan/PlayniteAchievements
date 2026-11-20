namespace PlayniteAchievements.Providers.Local
{
    public sealed class NativeSourceIconSettings
    {
        public string CustomIconPath { get; set; } = "";
        public string BorrowedProviderIconKey { get; set; } = "";
        public NativeSourceIconSettings Copy() => (NativeSourceIconSettings)MemberwiseClone();
    }
}
