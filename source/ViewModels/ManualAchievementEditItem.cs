using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Time display mode for the time picker.
    /// </summary>
    public enum TimeMode
    {
        AM,
        PM,
        TwentyFourHour
    }
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
        /// Otherwise uses unlocked icon with grayscale prefix applied for locked achievements.
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

                var candidate = UnlockedIconUrl;
                if (!IsUnlocked && !string.IsNullOrWhiteSpace(candidate))
                {
                    candidate = AchievementIconResolver.ApplyGrayPrefix(candidate);
                }

                return !string.IsNullOrWhiteSpace(candidate) ? candidate : DefaultIcon;
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
                    InitializeTimePickerFromUnlockTime();

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

        // Time picker properties
        private TimeMode _selectedTimeMode;
        private int _selectedHour;
        private int _selectedMinute;

        /// <summary>
        /// Available hour values based on current time mode.
        /// </summary>
        public IEnumerable<int> AvailableHours => SelectedTimeMode == TimeMode.TwentyFourHour
            ? Enumerable.Range(0, 24)
            : Enumerable.Range(1, 12);

        /// <summary>
        /// Available minute values (0-59).
        /// </summary>
        public IEnumerable<int> AvailableMinutes => Enumerable.Range(0, 60);

        /// <summary>
        /// Available time modes.
        /// </summary>
        public IEnumerable<TimeMode> AvailableTimeModes => Enum.GetValues(typeof(TimeMode)).Cast<TimeMode>();

        /// <summary>
        /// Selected time mode (AM/PM/24hr).
        /// </summary>
        public TimeMode SelectedTimeMode
        {
            get => _selectedTimeMode;
            set
            {
                if (_selectedTimeMode != value)
                {
                    var previousMode = _selectedTimeMode;

                    // Convert hour BEFORE changing mode so it's valid when AvailableHours updates
                    if (value == TimeMode.TwentyFourHour)
                    {
                        // Switching to 24hr: convert from 12hr
                        _selectedHour = Convert12To24Hour(_selectedHour, previousMode);
                    }
                    else if (previousMode == TimeMode.TwentyFourHour)
                    {
                        // Switching from 24hr to 12hr
                        Convert24To12Hour(_selectedHour, out _selectedHour, out value);
                    }
                    // Switching between AM and PM - keep hour the same

                    _selectedTimeMode = value;
                    OnPropertyChanged(nameof(SelectedTimeMode));
                    OnPropertyChanged(nameof(AvailableHours));
                    OnPropertyChanged(nameof(SelectedHour));
                    UpdateUnlockTimeFromPicker();
                }
            }
        }

        /// <summary>
        /// Selected hour (1-12 for AM/PM, 0-23 for 24hr).
        /// </summary>
        public int SelectedHour
        {
            get => _selectedHour;
            set
            {
                if (_selectedHour != value)
                {
                    _selectedHour = value;
                    OnPropertyChanged(nameof(SelectedHour));
                    UpdateUnlockTimeFromPicker();
                }
            }
        }

        /// <summary>
        /// Selected minute (0-59).
        /// </summary>
        public int SelectedMinute
        {
            get => _selectedMinute;
            set
            {
                if (_selectedMinute != value)
                {
                    _selectedMinute = value;
                    OnPropertyChanged(nameof(SelectedMinute));
                    UpdateUnlockTimeFromPicker();
                }
            }
        }

        private void UpdateUnlockTimeFromPicker()
        {
            if (!UnlockDate.HasValue) return;

            int hour24 = SelectedTimeMode == TimeMode.TwentyFourHour
                ? SelectedHour
                : Convert12To24Hour(SelectedHour, SelectedTimeMode);

            UnlockTimeLocal = UnlockDate.Value.Date + new TimeSpan(hour24, SelectedMinute, 0);
        }

        private int Convert12To24Hour(int hour12, TimeMode mode)
        {
            if (mode == TimeMode.TwentyFourHour) return hour12;

            if (mode == TimeMode.AM)
            {
                return hour12 == 12 ? 0 : hour12;
            }
            else // PM
            {
                return hour12 == 12 ? 12 : hour12 + 12;
            }
        }

        private void Convert24To12Hour(int hour24, out int hour12, out TimeMode mode)
        {
            if (hour24 == 0)
            {
                hour12 = 12;
                mode = TimeMode.AM;
            }
            else if (hour24 < 12)
            {
                hour12 = hour24;
                mode = TimeMode.AM;
            }
            else if (hour24 == 12)
            {
                hour12 = 12;
                mode = TimeMode.PM;
            }
            else
            {
                hour12 = hour24 - 12;
                mode = TimeMode.PM;
            }
        }

        private void InitializeTimePickerFromUnlockTime()
        {
            if (UnlockTimeLocal.HasValue)
            {
                var time = UnlockTimeLocal.Value.TimeOfDay;
                Convert24To12Hour(time.Hours, out _selectedHour, out _selectedTimeMode);
                _selectedMinute = time.Minutes;
            }
            else
            {
                // Default to noon
                _selectedHour = 12;
                _selectedMinute = 0;
                _selectedTimeMode = TimeMode.PM;
            }

            OnPropertyChanged(nameof(SelectedHour));
            OnPropertyChanged(nameof(SelectedMinute));
            OnPropertyChanged(nameof(SelectedTimeMode));
            OnPropertyChanged(nameof(AvailableHours));
        }

        public ManualAchievementEditItem(AchievementDetail source, bool isUnlocked, DateTime? unlockTime)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            _isUnlocked = isUnlocked;
            _unlockTime = unlockTime;
            InitializeTimePickerFromUnlockTime();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
