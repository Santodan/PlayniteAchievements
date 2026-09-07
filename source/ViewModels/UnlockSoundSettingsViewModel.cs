using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Sound;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The six per-tier rows of the unlock sound table: each shows where its sound currently comes
    /// from and lets the user point it at their own file. Edits write straight into the live
    /// persisted settings (the section's Cancel restores them) and, debounced, re-apply the sound
    /// service so the host preloads the new set.
    /// </summary>
    internal sealed class UnlockSoundSettingsViewModel : ObservableObject, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly UnlockSoundService _sounds;
        private readonly ILogger _logger;
        private readonly DispatcherTimer _applyDebounceTimer;

        public UnlockSoundSettingsViewModel(
            PlayniteAchievementsSettings settings,
            UnlockSoundService sounds,
            ILogger logger)
        {
            _settings = settings;
            _sounds = sounds;
            _logger = logger;
            _applyDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _applyDebounceTimer.Tick += (s, e) =>
            {
                _applyDebounceTimer.Stop();
                ApplyNow();
            };

            Rows = new ObservableCollection<UnlockSoundRowItem>();
            foreach (var tier in UnlockSoundTierExtensions.All)
            {
                Rows.Add(new UnlockSoundRowItem(this, tier));
            }

            Refresh();
        }

        public ObservableCollection<UnlockSoundRowItem> Rows { get; }

        /// <summary>Re-reads every row's custom path from settings and re-resolves its source.</summary>
        public void Refresh()
        {
            var sounds = _settings?.Persisted?.UnlockSounds;
            var resolver = _sounds?.Resolver;
            foreach (var row in Rows)
            {
                row.Load(sounds?.GetPath(row.Tier), resolver?.Resolve(row.Tier));
            }
        }

        /// <summary>Plays the tier's currently resolved sound at the configured volume.</summary>
        public void Test(UnlockSoundRowItem row)
        {
            if (row == null || _sounds == null)
            {
                return;
            }

            try
            {
                _sounds.Play(row.Tier, force: true);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Test unlock sound failed.");
            }
        }

        internal void SetCustomPath(UnlockSoundTier tier, string path)
        {
            _settings?.Persisted?.UnlockSounds?.SetPath(tier, path);
            Refresh();
            ScheduleApply();
        }

        /// <summary>Volume and path edits re-apply the service after the user pauses typing.</summary>
        internal void ScheduleApply()
        {
            _applyDebounceTimer.Stop();
            _applyDebounceTimer.Start();
        }

        public void Dispose()
        {
            _applyDebounceTimer.Stop();
        }

        private void ApplyNow()
        {
            try
            {
                _sounds?.ApplySettings();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Applying unlock sound settings from the settings page failed.");
            }
        }
    }

    internal sealed class UnlockSoundRowItem : ObservableObject
    {
        private readonly UnlockSoundSettingsViewModel _owner;
        private string _customPath;
        private string _sourceLabel;
        private string _resolvedPath;

        public UnlockSoundRowItem(UnlockSoundSettingsViewModel owner, UnlockSoundTier tier)
        {
            _owner = owner;
            Tier = tier;
            TierLabel = ResourceProvider.GetString(TierLabelKey(tier));
        }

        public UnlockSoundTier Tier { get; }

        public string TierLabel { get; }

        /// <summary>The user's own file for this tier; blank means "use the theme or default".</summary>
        public string CustomPath
        {
            get => _customPath;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (string.Equals(normalized, _customPath, StringComparison.Ordinal))
                {
                    return;
                }

                _owner.SetCustomPath(Tier, normalized);
            }
        }

        public bool HasCustomPath => !string.IsNullOrWhiteSpace(_customPath);

        /// <summary>Localized Custom / Theme / Default / None.</summary>
        public string SourceLabel
        {
            get => _sourceLabel;
            private set => SetValue(ref _sourceLabel, value);
        }

        public string ResolvedPath
        {
            get => _resolvedPath;
            private set => SetValue(ref _resolvedPath, value);
        }

        internal void Load(string customPath, ResolvedUnlockSound resolved)
        {
            _customPath = string.IsNullOrWhiteSpace(customPath) ? null : customPath;
            OnPropertyChanged(nameof(CustomPath));
            OnPropertyChanged(nameof(HasCustomPath));
            SourceLabel = ResourceProvider.GetString(SourceLabelKey(resolved?.Source ?? UnlockSoundSource.None));
            ResolvedPath = resolved?.Path;
        }

        private static string TierLabelKey(UnlockSoundTier tier)
        {
            switch (tier)
            {
                case UnlockSoundTier.Uncommon: return "LOCPlayAch_Rarity_Uncommon";
                case UnlockSoundTier.Rare: return "LOCPlayAch_Rarity_Rare";
                case UnlockSoundTier.UltraRare: return "LOCPlayAch_Rarity_UltraRare";
                case UnlockSoundTier.Hidden: return "LOCPlayAch_Filter_Hidden";
                case UnlockSoundTier.Capstone: return "LOCPlayAch_Dynamic_Capstone";
                default: return "LOCPlayAch_Rarity_Common";
            }
        }

        private static string SourceLabelKey(UnlockSoundSource source)
        {
            switch (source)
            {
                case UnlockSoundSource.Custom: return "LOCPlayAch_Common_Custom";
                case UnlockSoundSource.Theme: return "LOCPlayAch_Settings_Style_FireTheme";
                case UnlockSoundSource.Default: return "LOCPlayAch_Common_Default";
                default: return "LOCPlayAch_Common_None";
            }
        }
    }
}
