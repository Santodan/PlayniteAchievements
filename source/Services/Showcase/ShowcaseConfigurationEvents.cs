using System;

namespace PlayniteAchievements.Services.Showcase
{
    public static class ShowcaseConfigurationEvents
    {
        public static event EventHandler Changed;

        public static void RaiseChanged()
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
