using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Item representing an editable achievement in the manual achievements wizard.
    /// Uses AchievementDetail directly to avoid duplicating definition.
    /// </summary>
    public sealed class ManualAchievementEditItem : INotifyPropertyChanged
    {
        private bool _isUnlocked;
        private bool _isRevealed;
        private DateTime? _unlockTime;

        public AchievementDetail Source { get; }

        // Forward properties from AchievementDetail
        public string ApiName => Source.ApiName;
        public string DisplayName => Source.DisplayName;
        public string Description => Source.Description;
        public string UnlockedIconUrl => Source.UnlockedIconPath;
        public string LockedIconUrl => Source.LockedIconPath;
        public bool Hidden => Source.Hidden;
        public double? GlobalPercentUnlocked => Source.GlobalPercentUnlocked;
        public int? Points => Source.Points;
        public bool HasRarity => GlobalPercentUnlocked.HasValue;

        public bool IsUnlocked
        {
            get => _isUnlocked;
            set
            {
                if (_isUnlocked != value)
                {
                    _isUnlocked = value;
                    OnPropertyChanged(nameof(IsUnlocked));
                    OnPropertyChanged(nameof(DisplayIconUrl));
                    OnPropertyChanged(nameof(CanReveal));
                    OnPropertyChanged(nameof(IsHidden));
                    OnPropertyChanged(nameof(DisplayNameResolved));
                    OnPropertyChanged(nameof(DescriptionResolved));

                    // Auto-set unlock time to now when unlocking without a time
                    if (value && !_unlockTime.HasValue)
                    {
                        UnlockTime = DateTime.UtcNow;
                    }
                    // Clear unlock time when locking
                    else if (!value)
                    {
                        UnlockTime = null;
                    }
                }
            }
        }

        /// <summary>
        /// Icon URL for display.
        /// For hidden achievements that aren't revealed, uses placeholder icon.
        /// For unlocked achievements, uses the unlocked icon.
        /// For locked achievements, uses the locked icon.
        /// </summary>
        public string DisplayIconUrl
        {
            get
            {
                // For hidden achievements that aren't revealed, use placeholder
                if (IsHidden)
                {
                    return DefaultIcon;
                }

                var icon = IsUnlocked ? UnlockedIconUrl : LockedIconUrl;
                if (string.IsNullOrWhiteSpace(icon))
                {
                    return DefaultIcon;
                }

                return icon;
            }
        }

        private static string DefaultIcon => AchievementIconResolver.GetDefaultIcon();

        /// <summary>
        /// Whether this hidden achievement can be revealed (hidden and not unlocked).
        /// </summary>
        public bool CanReveal => Hidden && !IsUnlocked;

        /// <summary>
        /// Whether the achievement info should be hidden (can reveal and not yet revealed).
        /// </summary>
        public bool IsHidden => CanReveal && !_isRevealed;

        /// <summary>
        /// Whether the hidden achievement has been revealed in this session.
        /// </summary>
        public bool IsRevealed
        {
            get => _isRevealed;
            set
            {
                if (_isRevealed != value)
                {
                    _isRevealed = value;
                    OnPropertyChanged(nameof(IsRevealed));
                    OnPropertyChanged(nameof(IsHidden));
                    OnPropertyChanged(nameof(DisplayIconUrl));
                    OnPropertyChanged(nameof(DisplayNameResolved));
                    OnPropertyChanged(nameof(DescriptionResolved));
                }
            }
        }

        /// <summary>
        /// Display name, masked if hidden and not revealed.
        /// </summary>
        public string DisplayNameResolved => IsHidden
            ? ResourceProvider.GetString("LOCPlayAch_Achievements_HiddenTitle")
            : DisplayName;

        /// <summary>
        /// Description, masked if hidden and not revealed.
        /// </summary>
        public string DescriptionResolved => IsHidden
            ? ResourceProvider.GetString("LOCPlayAch_Achievements_ClickToReveal")
            : Description;

        /// <summary>
        /// Toggles the reveal state if the achievement can be revealed.
        /// </summary>
        public void ToggleReveal()
        {
            if (CanReveal)
            {
                IsRevealed = !IsRevealed;
            }
        }

        public DateTime? UnlockTime
        {
            get => _unlockTime;
            set
            {
                if (_unlockTime != value)
                {
                    _unlockTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UnlockTimeLocal));
                    OnPropertyChanged(nameof(UnlockDate));
                    OnPropertyChanged(nameof(UnlockTimeOfDay));

                    // Ensure unlocked state matches
                    if (value.HasValue && !_isUnlocked)
                    {
                        _isUnlocked = true;
                        OnPropertyChanged(nameof(IsUnlocked));
                    }
                }
            }
        }

        /// <summary>
        /// Local time representation for display.
        /// </summary>
        public DateTime? UnlockTimeLocal
        {
            get => UnlockTime?.ToLocalTime();
            set
            {
                if (value.HasValue)
                {
                    // Convert local to UTC
                    UnlockTime = value.Value.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Local).ToUniversalTime()
                        : value.Value.ToUniversalTime();
                }
                else
                {
                    UnlockTime = null;
                }
            }
        }

        /// <summary>
        /// Date component of unlock time (for DatePicker).
        /// </summary>
        public DateTime? UnlockDate
        {
            get => UnlockTimeLocal?.Date;
            set
            {
                if (value.HasValue)
                {
                    // Preserve time of day or default to noon
                    var existingTime = UnlockTimeLocal?.TimeOfDay ?? TimeSpan.FromHours(12);
                    UnlockTimeLocal = value.Value.Date + existingTime;
                }
                else if (!IsUnlocked)
                {
                    UnlockTime = null;
                }
            }
        }

        /// <summary>
        /// Time of day component (for TimePicker).
        /// </summary>
        public TimeSpan? UnlockTimeOfDay
        {
            get => UnlockTimeLocal?.TimeOfDay;
            set
            {
                if (value.HasValue && UnlockDate.HasValue)
                {
                    UnlockTimeLocal = UnlockDate.Value.Date + value.Value;
                }
            }
        }

        public ManualAchievementEditItem(AchievementDetail source, bool isUnlocked, DateTime? unlockTime)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            _isUnlocked = isUnlocked;
            _unlockTime = unlockTime;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
