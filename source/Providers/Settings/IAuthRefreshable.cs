using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Settings
{
    /// <summary>
    /// Interface for provider settings views that can refresh their authentication status.
    /// </summary>
    public interface IAuthRefreshable
    {
        /// <summary>
        /// Refreshes the authentication status displayed in the view.
        /// </summary>
        Task RefreshAuthStatusAsync();
    }
}
