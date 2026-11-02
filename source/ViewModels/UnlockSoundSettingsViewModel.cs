using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Sound;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The six per-tier rows of the unlock sound table for one scope (global, a platform or a
    /// game): each shows where its sound currently comes from and lets the user point it at their
    /// own file. A scope that follows an inherited pack shows that pack read-only. Edits write
    /// through the scope (a settings scope into the live persisted settings, which the section's
    /// Cancel restores) and, debounced, re-apply the sound service so the host preloads the new set.
    /// </summary>
    internal sealed class UnlockSoundSettingsViewModel : ObservableObject, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly UnlockSoundService _sounds;
        private readonly ILogger _logger;
        private readonly DispatcherTimer _applyDebounceTimer;
        private readonly Func<string, string> _importFile;
        private readonly Action<UnlockSoundSettings> _pruneUnreferenced;
        private UnlockSoundScope _scope;
        private bool _isEditable = true;
        private string _testProviderKey;
        private Guid _testGameId;

        /// <param name="importFile">Copies a picked file into managed storage and returns the copy's path; null stores picks as they are.</param>
        /// <param name="pruneUnreferenced">Removes managed copies no pack references after the given pack was written; null keeps them.</param>
        public UnlockSoundSettingsViewModel(
            PlayniteAchievementsSettings settings,
            UnlockSoundService sounds,
            ILogger logger,
            Func<string, string> importFile = null,
            Action<UnlockSoundSettings> pruneUnreferenced = null)
        {
            _settings = settings;
            _sounds = sounds;
            _logger = logger;
            _importFile = importFile;
            _pruneUnreferenced = pruneUnreferenced;
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

        /// <summary>False while the scope follows an inherited pack, which the rows then show read-only.</summary>
        public bool IsEditable
        {
            get => _isEditable;
            private set => SetValue(ref _isEditable, value);
        }

        /// <summary>
        /// Points the rows at a scope. <paramref name="testProviderKey"/> and
        /// <paramref name="testGameId"/> are what the Test button resolves through, so it plays
        /// what an unlock in this scope would.
        /// </summary>
        public void SetScope(UnlockSoundScope scope, bool editable, string testProviderKey, Guid testGameId)
        {
            _scope = scope;
            _testProviderKey = testProviderKey;
            _testGameId = testGameId;
            IsEditable = editable;
            Refresh();
        }

        /// <summary>The pack the rows show: the scope's effective pack, or the global pack without a scope.</summary>
        public UnlockSoundSettings CurrentSounds => _scope?.EffectiveSounds ?? _settings?.Persisted?.UnlockSounds;

        /// <summary>What each tier of the shown pack plays now.</summary>
        public System.Collections.Generic.IReadOnlyList<ResolvedUnlockSound> ResolveAll()
        {
            return _sounds?.Resolver?.ResolveAll(CurrentSounds);
        }

        /// <summary>
        /// Re-reads every row's custom path from the scope and re-resolves its source, its badge
        /// and the theme files it could be tested against. Cheap enough to run whole: six tiers,
        /// and the only disk work is the existence probes the resolution already does.
        /// </summary>
        public void Refresh()
        {
            var sounds = CurrentSounds;
            var resolver = _sounds?.Resolver;
            foreach (var row in Rows)
            {
                row.Load(
                    sounds?.GetPath(row.Tier),
                    resolver?.Resolve(row.Tier, sounds),
                    resolver?.FindThemeCandidates(row.Tier),
                    CreateBadge(row.Tier));
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
                _sounds.Play(row.Tier, _testProviderKey, _testGameId, force: true);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Test unlock sound failed.");
            }
        }

        /// <summary>
        /// Plays one theme-supplied file, whichever mode's theme shipped it and whether or not the
        /// theme step is currently in the chain, so both themes can be checked from one mode.
        /// </summary>
        public void TestFile(string path)
        {
            if (_sounds == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                _sounds.PlayFile(path);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Test theme unlock sound failed.");
            }
        }

        /// <summary>Raised after a row's file changed the scope's pack.</summary>
        public event EventHandler SoundsChanged;

        /// <summary>
        /// Sets the tier to a file the user picked: a copy in managed storage, so the tier keeps
        /// playing after the original moves. Throws when the file is not a usable sound.
        /// </summary>
        internal void PickFile(UnlockSoundTier tier, string path)
        {
            if (!IsEditable || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            SetCustomPath(tier, _importFile != null ? _importFile(path) : path);
        }

        internal void SetCustomPath(UnlockSoundTier tier, string path)
        {
            if (!IsEditable)
            {
                Refresh();
                return;
            }

            if (_scope == null)
            {
                _settings?.Persisted?.UnlockSounds?.SetPath(tier, path);
            }
            else
            {
                var pack = _scope.OwnSounds?.Clone();
                if (pack == null)
                {
                    Refresh();
                    return;
                }

                pack.SetPath(tier, path);
                _scope.Write(pack);
            }

            try
            {
                _pruneUnreferenced?.Invoke(CurrentSounds);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Removing unreferenced unlock sound copies failed.");
            }

            Refresh();
            ScheduleApply();
            SoundsChanged?.Invoke(this, EventArgs.Empty);
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

        /// <summary>
        /// The tier's badge, drawn from the user's own rarity appearance so the table matches the
        /// notification it describes. Hidden has no rarity of its own and no badge in the set, so it
        /// borrows the plugin's hidden-achievement art; a failure anywhere leaves the row's badge
        /// empty rather than breaking the page.
        /// </summary>
        private ImageSource CreateBadge(UnlockSoundTier tier)
        {
            try
            {
                var persisted = _settings?.Persisted;
                switch (tier)
                {
                    case UnlockSoundTier.Capstone:
                        return RarityAppearanceHelper.CreateCompletedBadgePreview(persisted);
                    case UnlockSoundTier.Hidden:
                        return LoadImage(AchievementIconResolver.GetHiddenFallbackIcon());
                    case UnlockSoundTier.UltraRare:
                        return RarityAppearanceHelper.CreateBadgePreview(RarityTier.UltraRare, persisted);
                    case UnlockSoundTier.Rare:
                        return RarityAppearanceHelper.CreateBadgePreview(RarityTier.Rare, persisted);
                    case UnlockSoundTier.Uncommon:
                        return RarityAppearanceHelper.CreateBadgePreview(RarityTier.Uncommon, persisted);
                    default:
                        return RarityAppearanceHelper.CreateBadgePreview(RarityTier.Common, persisted);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Unlock sound badge for tier {tier} could not be built.");
                return null;
            }
        }

        /// <summary>
        /// Decodes the hidden cover, which arrives as a pack URI or as the user's own file path, so
        /// the badge column carries one image type throughout. Frozen: it is built off the six-row
        /// refresh and never mutated afterwards.
        /// </summary>
        private static ImageSource LoadImage(string uriOrPath)
        {
            if (string.IsNullOrWhiteSpace(uriOrPath))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(uriOrPath, UriKind.RelativeOrAbsolute);
            image.EndInit();
            if (image.CanFreeze)
            {
                image.Freeze();
            }

            return image;
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

    /// <summary>
    /// One theme-supplied file a tier can be tested against, labelled by the mode whose theme ships
    /// it so a tester can tell the two apart when both provide the tier.
    /// </summary>
    internal sealed class ThemeUnlockSoundTestItem
    {
        public ThemeUnlockSoundTestItem(string modeName, string path)
        {
            Path = path;
            Label = $"{ModeLabel(modeName)}: {System.IO.Path.GetFileName(path)}";
        }

        public string Path { get; }

        public string Label { get; }

        private static string ModeLabel(string modeName)
        {
            return string.Equals(modeName, UnlockSoundResolver.FullscreenModeName, StringComparison.OrdinalIgnoreCase)
                ? ResourceProvider.GetString("LOCPlayAch_Common_Fullscreen")
                : ResourceProvider.GetString("LOCPlayAch_Common_Desktop");
        }
    }

    internal sealed class UnlockSoundRowItem : ObservableObject
    {
        private readonly UnlockSoundSettingsViewModel _owner;
        private string _customPath;
        private string _sourceLabel;
        private ImageSource _badgeImage;

        public UnlockSoundRowItem(UnlockSoundSettingsViewModel owner, UnlockSoundTier tier)
        {
            _owner = owner;
            Tier = tier;
            TierLabel = ResourceProvider.GetString(TierLabelKey(tier));
            ThemeCandidates = new ObservableCollection<ThemeUnlockSoundTestItem>();
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

        /// <summary>The tier's rarity badge, so the table reads as the rarities it configures.</summary>
        public ImageSource BadgeImage
        {
            get => _badgeImage;
            private set => SetValue(ref _badgeImage, value);
        }

        /// <summary>Theme files this tier can be tested against, across both Playnite modes.</summary>
        public ObservableCollection<ThemeUnlockSoundTestItem> ThemeCandidates { get; }

        public bool HasThemeCandidates => ThemeCandidates.Count > 0;

        internal void Load(
            string customPath,
            ResolvedUnlockSound resolved,
            System.Collections.Generic.IReadOnlyList<ThemeUnlockSoundCandidate> themeCandidates,
            ImageSource badgeImage)
        {
            _customPath = string.IsNullOrWhiteSpace(customPath) ? null : customPath;
            OnPropertyChanged(nameof(CustomPath));
            OnPropertyChanged(nameof(HasCustomPath));
            SourceLabel = ResourceProvider.GetString(SourceLabelKey(resolved?.Source ?? UnlockSoundSource.None));
            BadgeImage = badgeImage;

            ThemeCandidates.Clear();
            foreach (var candidate in themeCandidates ?? Array.Empty<ThemeUnlockSoundCandidate>())
            {
                ThemeCandidates.Add(new ThemeUnlockSoundTestItem(candidate.ModeName, candidate.Path));
            }

            OnPropertyChanged(nameof(HasThemeCandidates));
        }

        /// <summary>The localization key naming a sound tier, shared with the Workshop sound pack preview.</summary>
        internal static string TierLabelKey(UnlockSoundTier tier)
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
