using System.Collections.Generic;
using System.ComponentModel;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Backing store for a control-bar Compare dropdown. The dropdown itself is a plain
    /// single-select string filter, so implementations expose their people as opaque keys and
    /// resolve display names, favorite markers, and selection against those keys. Raising
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> refreshes the dropdown.
    ///
    /// Two flavors implement this: <see cref="FriendCompareController"/> overlays a friend onto
    /// the local user's rows, and <see cref="FriendVsFriendCompareController"/> overlays one
    /// friend onto another friend's rows.
    /// </summary>
    public interface IGridCompareSource : INotifyPropertyChanged
    {
        bool IsCompareAvailable { get; }

        string CompareSelectionText { get; }

        IEnumerable<string> OptionKeys { get; }

        bool IsKeySelected(string key);

        string GetDisplayNameForKey(string key);

        bool IsKeyFavorite(string key);

        void SelectKey(string key, bool isSelected);
    }
}
