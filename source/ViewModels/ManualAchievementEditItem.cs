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
        public bool Unlocked => IsUnlocked;
        public double? GlobalPercentUnlocked => Source.GlobalPercentUnlocked;
        public int? Points => Source.Points;

        public bool IsUnlocked
        {
            get => _isUnlocked;
            set
            {
                if (_isUnlocked != value)
                {
                    _isUnlocked = value;
                    OnPropertyChanged(nameof(IsUnlocked));
                    OnPropertyChanged(nameof(Unlocked));
                    OnPropertyChanged(nameof(DisplayIconUrl));
                    OnPropertyChanged(nameof(DisplayIcon));
                    OnPropertyChanged(nameof(CanReveal));
                    OnPropertyChanged(nameof(IsHidden));
                    OnPropertyChanged(nameof(IsIconHidden));
                    OnPropertyChanged(nameof(IsTitleHidden));
                    OnPropertyChanged(nameof(IsDescriptionHidden));
                    OnPropertyChanged(nameof(DisplayNameResolved));
                    OnPropertyChanged(nameof(DescriptionResolved));
                    OnPropertyChanged(nameof(CanEditUnlockTime));

                    // Clear unlock time when locking
                    if (!value)
                    {
                        UnlockTime = null;
                    }
                }
            }
        }

        /// <summary>
        /// Whether an unlock timestamp is set for this row.
        /// </summary>
        public bool HasUnlockTime
        {
            get => _unlockTime.HasValue;
            set
            {
                if (value)
                {
                    if (!IsUnlocked)
                    {
                        IsUnlocked = true;
                    }

                    if (!_unlockTime.HasValue)
                    {
                        UnlockTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    UnlockTime = null;
                }
            }
        }

        public bool CanEditUnlockTime => IsUnlocked && HasUnlockTime;

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
        public string DisplayIcon => DisplayIconUrl;

        private static string DefaultIcon => AchievementIconResolver.GetDefaultIcon();

        /// <summary>
        /// Whether this hidden achievement can be revealed (hidden and not unlocked).
        /// </summary>
        public bool CanReveal => Hidden && !IsUnlocked;

        /// <summary>
        /// Whether the achievement info should be hidden (can reveal and not yet revealed).
        /// </summary>
        public bool IsHidden => CanReveal && !_isRevealed;
        public bool IsIconHidden => IsHidden;
        public bool IsTitleHidden => IsHidden;
        public bool IsDescriptionHidden => IsHidden;
        public string HiddenTitleSuffix => string.Empty;

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
                    OnPropertyChanged(nameof(IsIconHidden));
                    OnPropertyChanged(nameof(IsTitleHidden));
                    OnPropertyChanged(nameof(IsDescriptionHidden));
                    OnPropertyChanged(nameof(DisplayIconUrl));
                    OnPropertyChanged(nameof(DisplayIcon));
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
                    OnPropertyChanged(nameof(HasUnlockTime));
                    OnPropertyChanged(nameof(CanEditUnlockTime));
                    OnPropertyChanged(nameof(UnlockTimeLocal));
                    OnPropertyChanged(nameof(UnlockDate));
                    OnPropertyChanged(nameof(UnlockTimeOfDay));
                    InitializeTimePickerFromUnlockTime();

                    // Ensure unlocked state matches
                    if (value.HasValue && !_isUnlocked)
                    {
                        _isUnlocked = true;
                        OnPropertyChanged(nameof(IsUnlocked));
                        OnPropertyChanged(nameof(Unlocked));
                        OnPropertyChanged(nameof(DisplayIconUrl));
                        OnPropertyChanged(nameof(DisplayIcon));
                        OnPropertyChanged(nameof(CanReveal));
                        OnPropertyChanged(nameof(IsHidden));
                        OnPropertyChanged(nameof(IsIconHidden));
                        OnPropertyChanged(nameof(IsTitleHidden));
                        OnPropertyChanged(nameof(IsDescriptionHidden));
                        OnPropertyChanged(nameof(DisplayNameResolved));
                        OnPropertyChanged(nameof(DescriptionResolved));
                        OnPropertyChanged(nameof(CanEditUnlockTime));
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
        private string _timeText;
        private bool _isValidTime = true;
        private bool _isUpdatingFromText;

        // Cached time mode display strings for dropdown
        private static readonly string[] TimeModeDisplayNames = { "AM", "PM", "24hr" };

        /// <summary>
        /// Available time mode display strings.
        /// </summary>
        public IEnumerable<string> AvailableTimeModes => TimeModeDisplayNames;

        /// <summary>
        /// Combined time text for TextBox binding (HH:mm or h:mm depending on mode).
        /// </summary>
        public string TimeText
        {
            get => _timeText;
            set
            {
                if (_timeText != value)
                {
                    _timeText = value;
                    ValidateAndApplyTimeText();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsValidTime));
                    OnPropertyChanged(nameof(IsValidHour));
                    OnPropertyChanged(nameof(IsValidMinute));
                }
            }
        }

        /// <summary>
        /// Whether the current time text is valid.
        /// </summary>
        public bool IsValidTime => _isValidTime;

        /// <summary>
        /// Backward-compatible aliases used by save validation and bindings.
        /// </summary>
        public bool IsValidHour => _isValidTime;

        public bool IsValidMinute => _isValidTime;

        private void ValidateAndApplyTimeText()
        {
            if (_isUpdatingFromText) return;

            if (!TryParseTimeText(_timeText, _selectedTimeMode, out var parsedHour, out var parsedMinute))
            {
                _isValidTime = false;
                return;
            }

            _selectedHour = parsedHour;
            _selectedMinute = parsedMinute;
            _isValidTime = true;
            UpdateUnlockTimeFromPicker();
        }

        private bool TryParseTimeText(string input, TimeMode mode, out int hour, out int minute)
        {
            hour = 0;
            minute = 0;

            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var parts = input.Trim().Split(':');
            if (parts.Length != 2)
            {
                return false;
            }

            if (!int.TryParse(parts[0].Trim(), out hour) ||
                !int.TryParse(parts[1].Trim(), out minute))
            {
                return false;
            }

            if (minute < 0 || minute > 59)
            {
                return false;
            }

            var minHour = mode == TimeMode.TwentyFourHour ? 0 : 1;
            var maxHour = mode == TimeMode.TwentyFourHour ? 23 : 12;
            return hour >= minHour && hour <= maxHour;
        }

        private static string FormatTimeText(int hour, int minute, TimeMode mode)
        {
            if (mode == TimeMode.TwentyFourHour)
            {
                return $"{hour:D2}:{minute:D2}";
            }

            return $"{hour}:{minute:D2}";
        }

        private void SetTimeTextFromSelection()
        {
            _isUpdatingFromText = true;
            try
            {
                _timeText = FormatTimeText(_selectedHour, _selectedMinute, _selectedTimeMode);
                _isValidTime = true;
                OnPropertyChanged(nameof(TimeText));
                OnPropertyChanged(nameof(IsValidTime));
                OnPropertyChanged(nameof(IsValidHour));
                OnPropertyChanged(nameof(IsValidMinute));
            }
            finally
            {
                _isUpdatingFromText = false;
            }
        }

        /// <summary>
        /// Selected time mode display string for ComboBox binding.
        /// </summary>
        public string SelectedTimeModeText
        {
            get => _selectedTimeMode switch
            {
                TimeMode.TwentyFourHour => "24hr",
                TimeMode.PM => "PM",
                _ => "AM"
            };
            set
            {
                var newMode = value switch
                {
                    "24hr" => TimeMode.TwentyFourHour,
                    "PM" => TimeMode.PM,
                    _ => TimeMode.AM
                };

                if (_selectedTimeMode != newMode)
                {
                    var previousMode = _selectedTimeMode;
                    _selectedTimeMode = newMode;

                    // Convert hour value when switching modes
                    if (newMode == TimeMode.TwentyFourHour)
                    {
                        // Switching to 24hr: convert from 12hr
                        _selectedHour = Convert12To24Hour(_selectedHour, previousMode);
                    }
                    else if (previousMode == TimeMode.TwentyFourHour)
                    {
                        // Switching from 24hr to 12hr
                        Convert24To12Hour(_selectedHour, out _selectedHour, out _selectedTimeMode);
                    }
                    // Switching between AM and PM keeps hour/minute values as entered.
                    SetTimeTextFromSelection();

                    OnPropertyChanged(nameof(SelectedTimeModeText));
                    UpdateUnlockTimeFromPicker();
                }
            }
        }

        /// <summary>
        /// Gets the internal TimeMode value.
        /// </summary>
        private TimeMode SelectedTimeMode => _selectedTimeMode;

        private void UpdateUnlockTimeFromPicker()
        {
            if (!UnlockDate.HasValue || !_isValidTime) return;

            int hour24 = _selectedTimeMode == TimeMode.TwentyFourHour
                ? _selectedHour
                : Convert12To24Hour(_selectedHour, _selectedTimeMode);

            UnlockTimeLocal = UnlockDate.Value.Date + new TimeSpan(hour24, _selectedMinute, 0);
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
                _selectedHour = 12;
                _selectedMinute = 0;
                _selectedTimeMode = TimeMode.PM;
            }

            SetTimeTextFromSelection();
            OnPropertyChanged(nameof(SelectedTimeModeText));
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
