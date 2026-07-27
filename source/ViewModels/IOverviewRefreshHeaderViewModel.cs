using PlayniteAchievements.Models;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace PlayniteAchievements.ViewModels
{
    public interface IOverviewRefreshHeaderViewModel
    {
        ObservableCollection<RefreshMode> RefreshModes { get; }

        string SelectedRefreshMode { get; set; }

        string RefreshModeSelectionText { get; }

        string RefreshOrCancelButtonText { get; }

        string RefreshOrCancelButtonGlyph { get; }

        ICommand RefreshCommand { get; }

        ICommand RefreshOrCancelCommand { get; }

        bool ShowProgress { get; }

        double ProgressPercent { get; }

        string ProgressMessage { get; }
    }
}
