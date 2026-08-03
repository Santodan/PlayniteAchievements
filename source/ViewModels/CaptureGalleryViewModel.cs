using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Captures;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Backs the capture gallery viewer. Two modes:
    /// <list type="bullet">
    /// <item>SingleAchievement — one achievement's captures; the type selector flips among that
    /// achievement's variants and there is no achievement-to-achievement navigation.</item>
    /// <item>GameGallery — the whole game; the type selector picks a variant and the prev/next
    /// arrows move between the achievements that have a capture of that variant.</item>
    /// </list>
    /// </summary>
    public sealed class CaptureGalleryViewModel : ObservableObject
    {
        public enum GalleryMode
        {
            SingleAchievement,
            GameGallery
        }

        private readonly GameCaptureSet _set;
        private readonly List<CaptureVariant> _availableVariants;
        private List<CaptureItem> _navItems = new List<CaptureItem>();
        private int _index;
        private CaptureVariant _selectedVariant;

        private CaptureGalleryViewModel(GameCaptureSet set, GalleryMode mode, string headerTitle)
        {
            _set = set ?? GameCaptureSet.Empty;
            Mode = mode;
            HeaderTitle = headerTitle ?? string.Empty;
            _availableVariants = _set.AvailableVariants.ToList();
            _selectedVariant = _availableVariants.Count > 0 ? _availableVariants[0] : CaptureVariant.Clean;

            NextCommand = new RelayCommand(_ => MoveNext(), _ => _index < _navItems.Count - 1);
            PreviousCommand = new RelayCommand(_ => MovePrevious(), _ => _index > 0);
            SelectVariantCommand = new RelayCommand(p =>
            {
                if (p is CaptureVariant variant)
                {
                    SelectedVariant = variant;
                }
            });

            RebuildNavItems();
        }

        public static CaptureGalleryViewModel ForGame(GameCaptureSet set, string gameTitle) =>
            new CaptureGalleryViewModel(set, GalleryMode.GameGallery, gameTitle);

        public static CaptureGalleryViewModel ForAchievement(
            GameCaptureSet fullSet,
            string achievementDisplayName,
            string achievementStem)
        {
            var group = fullSet?.Groups?.FirstOrDefault(g =>
                string.Equals(g.AchievementStem, achievementStem, StringComparison.OrdinalIgnoreCase));
            var narrowed = group != null
                ? new GameCaptureSet(new List<AchievementCaptureGroup> { group })
                : GameCaptureSet.Empty;
            return new CaptureGalleryViewModel(narrowed, GalleryMode.SingleAchievement, achievementDisplayName);
        }

        public GalleryMode Mode { get; }

        public string HeaderTitle { get; }

        public IReadOnlyList<CaptureVariant> AvailableVariants => _availableVariants;

        public bool HasAny => _set.HasAny;

        public bool IsEmpty => !_set.HasAny;

        public bool HasClean => _availableVariants.Contains(CaptureVariant.Clean);

        public bool HasNotification => _availableVariants.Contains(CaptureVariant.Notification);

        public bool HasFramed => _availableVariants.Contains(CaptureVariant.Framed);

        public bool HasVideo => _availableVariants.Contains(CaptureVariant.Video);

        public CaptureVariant SelectedVariant
        {
            get => _selectedVariant;
            set
            {
                if (!_availableVariants.Contains(value))
                {
                    return;
                }

                if (SetValueAndReturn(ref _selectedVariant, value))
                {
                    RaiseVariantSelectionFlags();
                    RebuildNavItems();
                }
            }
        }

        public bool IsCleanSelected => _selectedVariant == CaptureVariant.Clean;

        public bool IsNotificationSelected => _selectedVariant == CaptureVariant.Notification;

        public bool IsFramedSelected => _selectedVariant == CaptureVariant.Framed;

        public bool IsVideoSelected => _selectedVariant == CaptureVariant.Video;

        public ICommand NextCommand { get; }

        public ICommand PreviousCommand { get; }

        public ICommand SelectVariantCommand { get; }

        /// <summary>Prev/next arrows are only meaningful in game mode with more than one item.</summary>
        public bool CanNavigate => Mode == GalleryMode.GameGallery && _navItems.Count > 1;

        public CaptureItem Current =>
            (_index >= 0 && _index < _navItems.Count) ? _navItems[_index] : null;

        public string CurrentImagePath => Current != null && !Current.IsVideo ? Current.FilePath : null;

        public string CurrentVideoPath => Current != null && Current.IsVideo ? Current.FilePath : null;

        public bool IsVideo => Current?.IsVideo == true;

        public bool HasCurrent => Current != null;

        public bool ShowImage => HasCurrent && !IsVideo;

        public bool ShowVideo => HasCurrent && IsVideo;

        /// <summary>True when the selected variant has no capture in the current scope.</summary>
        public bool NoContentForVariant => _set.HasAny && !HasCurrent;

        public string PositionText =>
            _navItems.Count > 1 ? $"{_index + 1} / {_navItems.Count}" : string.Empty;

        /// <summary>Label under the media: the achievement stem in game mode, the header otherwise.</summary>
        public string CurrentAchievementLabel =>
            Mode == GalleryMode.GameGallery ? (Current?.AchievementStem ?? string.Empty) : HeaderTitle;

        // Controller/keyboard entry points (return true when they consumed the input).
        public bool TryMoveNext()
        {
            if (CanNavigate && _index < _navItems.Count - 1)
            {
                MoveNext();
                return true;
            }

            return false;
        }

        public bool TryMovePrevious()
        {
            if (CanNavigate && _index > 0)
            {
                MovePrevious();
                return true;
            }

            return false;
        }

        public bool CycleVariant(int direction)
        {
            if (_availableVariants.Count < 2 || direction == 0)
            {
                return false;
            }

            var current = _availableVariants.IndexOf(_selectedVariant);
            if (current < 0)
            {
                current = 0;
            }

            var next = ((current + direction) % _availableVariants.Count + _availableVariants.Count) % _availableVariants.Count;
            SelectedVariant = _availableVariants[next];
            return true;
        }

        private void MoveNext()
        {
            if (_index < _navItems.Count - 1)
            {
                _index++;
                RaiseCurrentChanged();
            }
        }

        private void MovePrevious()
        {
            if (_index > 0)
            {
                _index--;
                RaiseCurrentChanged();
            }
        }

        private void RebuildNavItems()
        {
            _navItems = _set.Groups
                .SelectMany(g => g.ForVariant(_selectedVariant))
                .ToList();
            _index = 0;
            RaiseCurrentChanged();
        }

        private void RaiseVariantSelectionFlags()
        {
            OnPropertyChanged(nameof(SelectedVariant));
            OnPropertyChanged(nameof(IsCleanSelected));
            OnPropertyChanged(nameof(IsNotificationSelected));
            OnPropertyChanged(nameof(IsFramedSelected));
            OnPropertyChanged(nameof(IsVideoSelected));
        }

        private void RaiseCurrentChanged()
        {
            OnPropertyChanged(nameof(Current));
            OnPropertyChanged(nameof(CurrentImagePath));
            OnPropertyChanged(nameof(CurrentVideoPath));
            OnPropertyChanged(nameof(IsVideo));
            OnPropertyChanged(nameof(HasCurrent));
            OnPropertyChanged(nameof(ShowImage));
            OnPropertyChanged(nameof(ShowVideo));
            OnPropertyChanged(nameof(NoContentForVariant));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(CurrentAchievementLabel));
            OnPropertyChanged(nameof(CanNavigate));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
