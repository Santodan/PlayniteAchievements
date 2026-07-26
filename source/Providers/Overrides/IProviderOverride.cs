namespace PlayniteAchievements.Providers.Overrides
{
    /// <summary>
    /// Implemented by an <see cref="IDataProvider"/> that supports a per-game override, letting
    /// the user manually bind a single game to this provider's data source. The override UI
    /// discovers participating providers via this interface and drives its input from the
    /// returned <see cref="ProviderOverrideDescriptor"/>.
    /// </summary>
    public interface IProviderOverride
    {
        ProviderOverrideDescriptor OverrideDescriptor { get; }
    }
}
