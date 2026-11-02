using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.Metadata;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Views.Settings.Metadata
{
    /// <summary>
    /// Metadata settings: the Sources page. One grid whose first row holds the defaults and whose
    /// other rows override them per platform.
    /// </summary>
    public partial class MetadataSourcesSection : UserControl
    {
        public MetadataSourcesSection()
        {
            InitializeComponent();
        }

        internal MetadataSourcesSection(PlayniteAchievementsSettings settings, ProviderRegistry registry)
            : this()
        {
            DataContext = new MetadataSourcesViewModel(
                settings ?? throw new ArgumentNullException(nameof(settings)),
                registry ?? throw new ArgumentNullException(nameof(registry)));
        }
    }

    public sealed class MetadataSourcesViewModel
    {
        internal MetadataSourcesViewModel(PlayniteAchievementsSettings settings, ProviderRegistry registry)
        {
            Settings = settings;
            var providers = registry.GetMetadataProviders() ?? Array.Empty<IMetadataProvider>();
            var defaults = new MetadataChoiceRow(settings, providers, null);
            var rows = new List<MetadataChoiceRow> { defaults };
            rows.AddRange((registry.GetSettingsViewProviderKeys() ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => new MetadataChoiceRow(settings, providers, key)));
            Rows = rows;

            // Platform rows show what their "Default" stands for, which the defaults row decides.
            defaults.Saved += () =>
            {
                foreach (var row in rows.Skip(1))
                {
                    row.RefreshInherited();
                }
            };
        }

        public IReadOnlyList<MetadataChoiceRow> Rows { get; }

        /// <summary>The settings, for the console version choice above the grid.</summary>
        public PlayniteAchievementsSettings Settings { get; }
    }

    /// <summary>
    /// One grid row. Cells hold option keys: <see cref="DefaultKey"/> follows the defaults row, an
    /// empty key is the platform's own data, and any other key names a source.
    /// </summary>
    public sealed class MetadataChoiceRow : ObservableObject
    {
        public const string DefaultKey = "~";

        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action<MetadataChoices> _saveGame;
        private readonly Func<MetadataChoices> _inherited;
        private string _icons;
        private string _categories;
        private string _tags;
        private string _rarity;
        private string _capstoneArt;
        private string _trophyTypes;
        private string _points;
        private string _text;

        /// <summary>The defaults row (null <paramref name="providerKey"/>) or a platform's row, saved to settings.</summary>
        internal MetadataChoiceRow(
            PlayniteAchievementsSettings settings,
            IReadOnlyList<IMetadataProvider> providers,
            string providerKey)
            : this(
                providers,
                providerKey,
                providerKey == null || MetadataChoices.ExophaseRarityProviders.Contains(providerKey),
                providerKey == null
                    ? settings.Persisted?.MetadataDefaults ?? MetadataChoices.CreateDefaults()
                    : FindOverride(settings.Persisted, providerKey),
                providerKey == null
                    ? (Func<MetadataChoices>)null
                    : () => settings.Persisted?.ResolvePlatformDefaults(providerKey) ?? MetadataChoices.CreateDefaults())
        {
            _settings = settings;
        }

        /// <summary>
        /// One game's row: its "Default" follows <paramref name="inherited"/>, the game's platform
        /// choices, and changes go to <paramref name="save"/>. Rarity is chosen per platform only.
        /// </summary>
        internal MetadataChoiceRow(
            IReadOnlyList<IMetadataProvider> providers,
            string servicingProviderKey,
            MetadataChoices stored,
            Func<MetadataChoices> inherited,
            Action<MetadataChoices> save)
            : this(providers, servicingProviderKey ?? string.Empty, supportsRarity: false, stored, inherited)
        {
            _saveGame = save;
        }

        private MetadataChoiceRow(
            IReadOnlyList<IMetadataProvider> providers,
            string providerKey,
            bool supportsRarity,
            MetadataChoices stored,
            Func<MetadataChoices> inherited)
        {
            _inherited = inherited;
            ProviderKey = providerKey;
            IsDefaults = providerKey == null;
            DisplayName = IsDefaults ? ResourceProvider.GetString("LOCPlayAch_Common_Default") : ProviderRegistry.GetLocalizedName(providerKey);
            if (!IsDefaults)
            {
                ProviderRegistry.TryResolveProviderVisuals(providerKey, out var iconKey, out _);
                ProviderIconKey = iconKey;
            }

            SupportsSources = MetadataChoices.TakesSources(providerKey);
            SupportsRarity = supportsRarity && SupportsSources;
            SupportsTrophyTypes = MetadataChoices.TakesTrophyTypes(providerKey);
            SupportsPoints = MetadataChoices.TakesPoints(providerKey);
            IconOptions = BuildOptions(providers, MetadataField.Icons);
            CapstoneArtOptions = BuildOptions(providers, MetadataField.CapstoneArt);
            TrophyTypeOptions = BuildOptions(providers, MetadataField.TrophyTypes);
            PointsOptions = BuildOptions(providers, MetadataField.Points);
            TextOptions = BuildOptions(providers, MetadataField.Text);
            CategoryOptions = BuildOptions(providers, MetadataField.Categories);
            TagOptions = BuildOptions(providers, MetadataField.Tags);
            RarityOptions = WithDefault(new[]
            {
                new ChoiceOption(string.Empty, ResourceProvider.GetString("LOCPlayAch_Settings_Override_Off")),
                new ChoiceOption(MetadataChoices.Exophase, ResourceProvider.GetString("LOCPlayAch_Provider_Exophase"))
            });

            _icons = ToKey(stored?.Icons);
            _categories = ToKey(stored?.Categories);
            _tags = ToKey(stored?.Tags);
            _rarity = SupportsRarity ? ToKey(stored?.Rarity) : DefaultKey;
            _capstoneArt = ToKey(stored?.CapstoneArt);
            _trophyTypes = SupportsTrophyTypes ? ToKey(stored?.TrophyTypes) : DefaultKey;
            _points = SupportsPoints ? ToKey(stored?.Points) : DefaultKey;
            _text = ToKey(stored?.Text);
        }

        public string ProviderKey { get; }

        public bool IsDefaults { get; }

        public string DisplayName { get; }

        public string ProviderIconKey { get; }

        public string ProviderColorHex => IsDefaults ? null : ProviderRegistry.GetProviderColorHex(ProviderKey);

        public bool HasProviderIcon => !string.IsNullOrWhiteSpace(ProviderIconKey);

        /// <summary>False for a platform no source has, whose row shows a dash in every column.</summary>
        public bool SupportsSources { get; }

        public bool SupportsRarity { get; }

        /// <summary>False for a platform with trophy types of its own, which takes none from a source.</summary>
        public bool SupportsTrophyTypes { get; }

        /// <summary>False for a platform with points of its own, which takes none from a source.</summary>
        public bool SupportsPoints { get; }

        public IReadOnlyList<ChoiceOption> IconOptions { get; }

        public IReadOnlyList<ChoiceOption> CategoryOptions { get; }

        public IReadOnlyList<ChoiceOption> TagOptions { get; }

        public IReadOnlyList<ChoiceOption> RarityOptions { get; }

        public IReadOnlyList<ChoiceOption> CapstoneArtOptions { get; }

        public IReadOnlyList<ChoiceOption> TrophyTypeOptions { get; }

        public IReadOnlyList<ChoiceOption> PointsOptions { get; }

        public IReadOnlyList<ChoiceOption> TextOptions { get; }

        /// <summary>Raised after a choice is saved.</summary>
        internal event Action Saved;

        // What each field's "Default" stands for, shown muted in the closed dropdown.
        public string InheritedIcons => InheritedLabel(IconOptions, choices => choices.Icons);

        public string InheritedCategories => InheritedLabel(CategoryOptions, choices => choices.Categories);

        public string InheritedTags => InheritedLabel(TagOptions, choices => choices.Tags);

        public string InheritedRarity => InheritedLabel(RarityOptions, choices => choices.Rarity);

        public string InheritedCapstoneArt => InheritedLabel(CapstoneArtOptions, choices => choices.CapstoneArt);

        public string InheritedTrophyTypes => InheritedLabel(TrophyTypeOptions, choices => choices.TrophyTypes);

        public string InheritedPoints => InheritedLabel(PointsOptions, choices => choices.Points);

        public string InheritedText => InheritedLabel(TextOptions, choices => choices.Text);

        /// <summary>Updates what "Default" stands for after the choices it follows changed.</summary>
        internal void RefreshInherited()
        {
            OnPropertyChanged(nameof(InheritedIcons));
            OnPropertyChanged(nameof(InheritedCategories));
            OnPropertyChanged(nameof(InheritedTags));
            OnPropertyChanged(nameof(InheritedRarity));
            OnPropertyChanged(nameof(InheritedCapstoneArt));
            OnPropertyChanged(nameof(InheritedTrophyTypes));
            OnPropertyChanged(nameof(InheritedPoints));
            OnPropertyChanged(nameof(InheritedText));
        }

        /// <summary>
        /// The label of the option the inherited choices pick; Off when no option matches.
        /// </summary>
        private string InheritedLabel(IReadOnlyList<ChoiceOption> options, Func<MetadataChoices, string> keyOf)
        {
            var inherited = _inherited?.Invoke();
            if (inherited == null)
            {
                return null;
            }

            var key = keyOf(inherited) ?? string.Empty;
            return (options.FirstOrDefault(option => !option.IsInherited && option.Key == key) ??
                    options.FirstOrDefault(option => option.Key.Length == 0))?.Label;
        }

        public string Icons
        {
            get => _icons;
            set { _icons = value; Save(); OnPropertyChanged(); }
        }

        public string Categories
        {
            get => _categories;
            set { _categories = value; Save(); OnPropertyChanged(); }
        }

        public string Tags
        {
            get => _tags;
            set { _tags = value; Save(); OnPropertyChanged(); }
        }

        public string Rarity
        {
            get => _rarity;
            set { _rarity = value; Save(); OnPropertyChanged(); }
        }

        public string CapstoneArt
        {
            get => _capstoneArt;
            set { _capstoneArt = value; Save(); OnPropertyChanged(); }
        }

        public string TrophyTypes
        {
            get => _trophyTypes;
            set { _trophyTypes = value; Save(); OnPropertyChanged(); }
        }

        public string Points
        {
            get => _points;
            set { _points = value; Save(); OnPropertyChanged(); }
        }

        public string Text
        {
            get => _text;
            set { _text = value; Save(); OnPropertyChanged(); }
        }

        private void Save()
        {
            var choices = new MetadataChoices
            {
                Icons = SupportsSources ? FromKey(_icons) : null,
                Rarity = SupportsRarity ? FromKey(_rarity) : null,
                CapstoneArt = SupportsSources ? FromKey(_capstoneArt) : null,
                TrophyTypes = SupportsTrophyTypes ? FromKey(_trophyTypes) : null,
                Points = SupportsPoints ? FromKey(_points) : null,
                Text = SupportsSources ? FromKey(_text) : null,
                Categories = SupportsSources ? FromKey(_categories) : null,
                Tags = SupportsSources ? FromKey(_tags) : null
            };

            if (_saveGame != null)
            {
                _saveGame(choices);
                return;
            }

            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            if (IsDefaults)
            {
                // The setter fills any unset field from the built-in defaults.
                persisted.MetadataDefaults = choices;
                Saved?.Invoke();
                return;
            }

            var platforms = PersistedSettings.ClonePlatformChoices(persisted.MetadataPlatformChoices);
            platforms[ProviderKey] = choices;
            persisted.MetadataPlatformChoices = platforms;
            Saved?.Invoke();
        }

        /// <summary>
        /// Off, or each source that supplies <paramref name="field"/>. Every row offers the same
        /// sources: one native to the platform (PlayStation on PSN) is the platform's own data, so
        /// choosing it changes nothing. The Exophase own-platform source is not offered: it applies
        /// on its own, by the Rarity choice.
        /// </summary>
        private IReadOnlyList<ChoiceOption> BuildOptions(IReadOnlyList<IMetadataProvider> providers, MetadataField field)
        {
            var options = new List<ChoiceOption>
            {
                new ChoiceOption(string.Empty, ResourceProvider.GetString("LOCPlayAch_Settings_Override_Off"))
            };
            options.AddRange(providers
                .Where(provider => (provider.SupportedFields & field) != 0 &&
                                   !string.Equals(provider.Key, MetadataChoices.Exophase, StringComparison.OrdinalIgnoreCase))
                .Select(provider => new ChoiceOption(provider.Key, ResourceProvider.GetString(provider.DisplayNameKey))));
            return WithDefault(options);
        }

        private IReadOnlyList<ChoiceOption> WithDefault(IEnumerable<ChoiceOption> options)
        {
            return IsDefaults
                ? options.ToList()
                : new[] { new ChoiceOption(DefaultKey, ResourceProvider.GetString("LOCPlayAch_Common_Default")) }.Concat(options).ToList();
        }

        private static MetadataChoices FindOverride(PersistedSettings persisted, string providerKey)
        {
            MetadataChoices choices = null;
            persisted?.MetadataPlatformChoices?.TryGetValue(providerKey, out choices);
            return choices;
        }

        private static string ToKey(string value) => value ?? DefaultKey;

        private static string FromKey(string key) => key == null || key == DefaultKey ? null : key;
    }

    public sealed class ChoiceOption
    {
        public ChoiceOption(string key, string label)
        {
            Key = key;
            Label = string.IsNullOrWhiteSpace(label) ? key : label;
        }

        public string Key { get; }

        public string Label { get; }

        /// <summary>The "Default" option, which stands for an inherited value.</summary>
        public bool IsInherited => Key == MetadataChoiceRow.DefaultKey;
    }
}
