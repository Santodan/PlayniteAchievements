using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Metadata;
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
            var providers = registry.GetMetadataProviders() ?? Array.Empty<IMetadataProvider>();
            var rows = new List<MetadataChoiceRow> { new MetadataChoiceRow(settings, providers, null) };
            rows.AddRange((registry.GetSettingsViewProviderKeys() ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => new MetadataChoiceRow(settings, providers, key)));
            Rows = rows;
        }

        public IReadOnlyList<MetadataChoiceRow> Rows { get; }
    }

    /// <summary>
    /// One grid row. Cells hold option keys: <see cref="DefaultKey"/> follows the defaults row, an
    /// empty key is the platform's own data, and categories carry their mode as "Source|Mode".
    /// </summary>
    public sealed class MetadataChoiceRow : ObservableObject
    {
        public const string DefaultKey = "~";
        private const char ModeSeparator = '|';

        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action<MetadataChoices> _saveGame;
        private string _icons;
        private string _categories;
        private string _rarity;
        private string _capstoneArt;
        private string _trophyTypes;
        private string _points;

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
                    : FindOverride(settings.Persisted, providerKey))
        {
            _settings = settings;
        }

        /// <summary>
        /// One game's row: its "Default" follows the game's platform, and changes go to
        /// <paramref name="save"/>. Rarity is chosen per platform only.
        /// </summary>
        internal MetadataChoiceRow(
            IReadOnlyList<IMetadataProvider> providers,
            string servicingProviderKey,
            MetadataChoices stored,
            Action<MetadataChoices> save)
            : this(providers, servicingProviderKey ?? string.Empty, supportsRarity: false, stored)
        {
            _saveGame = save;
        }

        private MetadataChoiceRow(
            IReadOnlyList<IMetadataProvider> providers,
            string providerKey,
            bool supportsRarity,
            MetadataChoices stored)
        {
            ProviderKey = providerKey;
            IsDefaults = providerKey == null;
            DisplayName = IsDefaults ? ResourceProvider.GetString("LOCDefault") : ProviderRegistry.GetLocalizedName(providerKey);
            if (!IsDefaults)
            {
                ProviderRegistry.TryResolveProviderVisuals(providerKey, out var iconKey, out _);
                ProviderIconKey = iconKey;
            }

            SupportsRarity = supportsRarity;
            IconOptions = BuildSourceOptions(providers, MetadataField.Icons, "LOCPlayAch_Settings_Metadata_Source_Own");
            CapstoneArtOptions = BuildSourceOptions(providers, MetadataField.CapstoneArt, "LOCPlayAch_Settings_Metadata_Source_Own");
            TrophyTypeOptions = BuildSourceOptions(providers, MetadataField.TrophyTypes, "LOCPlayAch_Settings_Metadata_Source_Own");
            PointsOptions = BuildSourceOptions(providers, MetadataField.Points, "LOCPlayAch_Settings_Metadata_Source_Own");
            CategoryOptions = BuildCategoryOptions(providers);
            RarityOptions = WithDefault(new[]
            {
                new ChoiceOption(string.Empty, ResourceProvider.GetString("LOCPlayAch_Settings_Metadata_Source_Own")),
                new ChoiceOption(MetadataChoices.Exophase, ResourceProvider.GetString("LOCPlayAch_Provider_Exophase"))
            });

            _icons = ToKey(stored?.Icons);
            _categories = ToCategoryKey(stored);
            _rarity = SupportsRarity ? ToKey(stored?.Rarity) : DefaultKey;
            _capstoneArt = ToKey(stored?.CapstoneArt);
            _trophyTypes = ToKey(stored?.TrophyTypes);
            _points = ToKey(stored?.Points);
        }

        public string ProviderKey { get; }

        public bool IsDefaults { get; }

        public string DisplayName { get; }

        public string ProviderIconKey { get; }

        public string ProviderColorHex => IsDefaults ? null : ProviderRegistry.GetProviderColorHex(ProviderKey);

        public bool HasProviderIcon => !string.IsNullOrWhiteSpace(ProviderIconKey);

        public bool SupportsRarity { get; }

        public IReadOnlyList<ChoiceOption> IconOptions { get; }

        public IReadOnlyList<ChoiceOption> CategoryOptions { get; }

        public IReadOnlyList<ChoiceOption> RarityOptions { get; }

        public IReadOnlyList<ChoiceOption> CapstoneArtOptions { get; }

        public IReadOnlyList<ChoiceOption> TrophyTypeOptions { get; }

        public IReadOnlyList<ChoiceOption> PointsOptions { get; }

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

        private void Save()
        {
            var choices = new MetadataChoices
            {
                Icons = FromKey(_icons),
                Rarity = SupportsRarity ? FromKey(_rarity) : null,
                CapstoneArt = FromKey(_capstoneArt),
                TrophyTypes = FromKey(_trophyTypes),
                Points = FromKey(_points)
            };
            ApplyCategoryKey(choices, _categories);

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
                var defaults = MetadataChoices.CreateDefaults();
                persisted.MetadataDefaults = new MetadataChoices
                {
                    Icons = choices.Icons ?? defaults.Icons,
                    Categories = choices.Categories ?? defaults.Categories,
                    CategoryMode = choices.CategoryMode ?? defaults.CategoryMode,
                    Rarity = choices.Rarity ?? defaults.Rarity,
                    CapstoneArt = choices.CapstoneArt ?? defaults.CapstoneArt,
                    TrophyTypes = choices.TrophyTypes ?? defaults.TrophyTypes,
                    Points = choices.Points ?? defaults.Points
                };
                return;
            }

            var platforms = PersistedSettings.ClonePlatformChoices(persisted.MetadataPlatformChoices);
            platforms[ProviderKey] = choices;
            persisted.MetadataPlatformChoices = platforms;
        }

        private IReadOnlyList<ChoiceOption> BuildSourceOptions(
            IReadOnlyList<IMetadataProvider> providers,
            MetadataField field,
            string ownLabelKey)
        {
            var options = new List<ChoiceOption> { new ChoiceOption(string.Empty, ResourceProvider.GetString(ownLabelKey)) };
            options.AddRange(providers
                .Where(provider => (provider.SupportedFields & field) == field &&
                                   (IsDefaults || !provider.IsNativeFor(ProviderKey)))
                .Select(provider => new ChoiceOption(provider.Key, ResourceProvider.GetString(provider.DisplayNameKey))));
            return WithDefault(options);
        }

        private IReadOnlyList<ChoiceOption> BuildCategoryOptions(IReadOnlyList<IMetadataProvider> providers)
        {
            // Own, Own + SteamHunters (merge: the platform's categories kept, the source fills the
            // rest), SteamHunters (the source's replace the platform's).
            var own = ResourceProvider.GetString("LOCPlayAch_Settings_Metadata_Source_Own");
            var combined = ResourceProvider.GetString("LOCPlayAch_Settings_Metadata_SourceMode");
            var options = new List<ChoiceOption> { new ChoiceOption(string.Empty, own) };
            foreach (var provider in providers.Where(provider => (provider.SupportedFields & MetadataField.Categories) != 0))
            {
                var name = ResourceProvider.GetString(provider.DisplayNameKey);
                options.Add(new ChoiceOption(provider.Key + ModeSeparator + MetadataCategoryMode.Merge, string.Format(combined, own, name)));
                options.Add(new ChoiceOption(provider.Key + ModeSeparator + MetadataCategoryMode.Override, name));
            }

            return WithDefault(options);
        }

        private IReadOnlyList<ChoiceOption> WithDefault(IEnumerable<ChoiceOption> options)
        {
            return IsDefaults
                ? options.ToList()
                : new[] { new ChoiceOption(DefaultKey, ResourceProvider.GetString("LOCDefault")) }.Concat(options).ToList();
        }

        private static MetadataChoices FindOverride(PersistedSettings persisted, string providerKey)
        {
            MetadataChoices choices = null;
            persisted?.MetadataPlatformChoices?.TryGetValue(providerKey, out choices);
            return choices;
        }

        private static string ToKey(string value) => value ?? DefaultKey;

        private static string FromKey(string key) => key == null || key == DefaultKey ? null : key;

        private static string ToCategoryKey(MetadataChoices stored)
        {
            if (stored?.Categories == null)
            {
                return DefaultKey;
            }

            return stored.Categories.Length == 0
                ? string.Empty
                : stored.Categories + ModeSeparator + (stored.CategoryMode ?? MetadataCategoryMode.Merge);
        }

        private static void ApplyCategoryKey(MetadataChoices choices, string key)
        {
            if (key == null || key == DefaultKey)
            {
                return;
            }

            var parts = key.Split(ModeSeparator);
            choices.Categories = parts[0];
            choices.CategoryMode = parts.Length > 1 && Enum.TryParse(parts[1], out MetadataCategoryMode mode)
                ? mode
                : MetadataCategoryMode.Merge;
        }
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
    }
}
