using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// Persists showcase settings through the plugin and notifies live surfaces of the change.
    /// Consolidates the persist-then-broadcast idiom shared by the widget renderers.
    /// </summary>
    public static class ShowcaseConfigurationCommit
    {
        public static void Commit()
        {
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            if (persisted != null)
            {
                ShowcaseGridSurfaces.PruneOrphaned(persisted.GridOptions, persisted.Showcase);
            }

            PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi();
            ShowcaseConfigurationEvents.RaiseChanged();
        }
    }
}
