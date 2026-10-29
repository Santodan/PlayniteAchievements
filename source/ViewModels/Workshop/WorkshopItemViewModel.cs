using Newtonsoft.Json.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Workshop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Workshop
{
    /// <summary>One Workshop item as the browser lists it: the index entry plus local state.</summary>
    public sealed class WorkshopItemViewModel : ObservableObject
    {
        private string _previewPath;
        private string _coverPath;
        private string _readme;
        private bool _isInstalled;
        private bool _hasUpdate;
        private bool _isApplied;
        private string _localGameName;
        private bool _isMine;
        private IReadOnlyList<WorkshopUseLine> _uses = Array.Empty<WorkshopUseLine>();

        public WorkshopItemViewModel(WorkshopItem item)
        {
            Item = item ?? throw new ArgumentNullException(nameof(item));
            SearchText = string.Join(" ", new[]
            {
                item.Name, item.Description, item.Author, item.Game?.Name, string.Join(" ", item.Tags ?? new List<string>())
            }.Where(text => !string.IsNullOrWhiteSpace(text))).ToLowerInvariant();
            TagSet = new HashSet<string>((item.Tags ?? new List<string>()).Select(WorkshopTags.Normalize));
        }

        /// <summary>Normalized tags for exact-match tag filtering.</summary>
        public HashSet<string> TagSet { get; }

        public WorkshopItem Item { get; }

        public string Id => Item.Id;
        public WorkshopItemKind Kind => Item.Kind;
        public string Name => Item.Name;
        public string Description => Item.Description;
        public string Author => Item.Author;
        public string Version => Item.Version;
        public string Updated => Item.Updated;
        /// <summary>
        /// The Workshop's counts, read through the shared store so Browse and the Library page
        /// always agree. Null until a list hands the store over, which is also what keeps the
        /// counts and the like button out when the service cannot be reached.
        /// </summary>
        public WorkshopStatsStore Stats
        {
            get => _stats;
            set
            {
                _stats = value;
                RaiseStatsChanged();
            }
        }

        private WorkshopStatsStore _stats;

        /// <summary>
        /// Re-reads the counts and the liked state from the store. The command's own state is
        /// raised too: a row binds its heart before the service has answered, and a button whose
        /// command could not execute at bind time stays disabled until it is told otherwise.
        /// </summary>
        public void RaiseStatsChanged()
        {
            _toggleLikeCommand?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(Installs));
            OnPropertyChanged(nameof(LikeCount));
            OnPropertyChanged(nameof(IsLiked));
            OnPropertyChanged(nameof(CanLike));
            OnPropertyChanged(nameof(HasStats));
        }

        /// <summary>How many installs the Workshop has recorded for the item.</summary>
        public long Installs => _stats?.InstallsFor(Id) ?? 0;

        public long LikeCount => _stats?.LikesFor(Id) ?? 0;

        public bool IsLiked => _stats?.IsLiked(Id) ?? false;

        /// <summary>True once the service has answered; until then no count is shown at all.</summary>
        public bool CanLike => _stats?.IsAvailable ?? false;

        /// <summary>True once the service has answered, so the install count can be shown.</summary>
        public bool HasStats => CanLike;

        /// <summary>Turns this install's like for the item on or off.</summary>
        public AsyncCommand ToggleLikeCommand => _toggleLikeCommand ?? (_toggleLikeCommand = new AsyncCommand(
            async _ =>
            {
                var store = _stats;
                if (store != null)
                {
                    await store.ToggleLikeAsync(Id, System.Threading.CancellationToken.None).ConfigureAwait(false);
                }
            },
            _ => CanLike));

        private AsyncCommand _toggleLikeCommand;

        public string GameName => Item.Game?.Name;
        public string Tags => string.Join(", ", Item.Tags ?? new List<string>());
        public bool HasTags => Item.Tags != null && Item.Tags.Count > 0;
        public bool HasGame => Item.Game != null;
        public string FolderUrl => Item.Urls?.Folder;
        public string KindLabel => KindLabelFor(Kind);
        public bool IsBundle => Kind == WorkshopItemKind.Bundle;
        public string SearchText { get; }

        /// <summary>Sortable date; the index writes yyyy-MM-dd.</summary>
        public DateTime UpdatedDate =>
            DateTime.TryParse(Item.Updated, out var date) ? date : DateTime.MinValue;

        public string PreviewPath
        {
            get => _previewPath;
            set => SetValue(ref _previewPath, value, nameof(PreviewPath), nameof(HasPreview), nameof(ThumbnailPath), nameof(HasThumbnail));
        }

        public bool HasPreview => !string.IsNullOrWhiteSpace(_previewPath);

        /// <summary>The sharer's optional cover image as a cached local file; null until fetched or when the item has none.</summary>
        public string CoverPath
        {
            get => _coverPath;
            set => SetValue(ref _coverPath, value, nameof(CoverPath), nameof(HasCover), nameof(ThumbnailPath), nameof(HasThumbnail));
        }

        public bool HasCover => !string.IsNullOrWhiteSpace(_coverPath);

        /// <summary>The list thumbnail: the cover when the item has one, otherwise the preview.</summary>
        public string ThumbnailPath => HasCover ? _coverPath : _previewPath;

        /// <summary>True when the item has a cover or a preview to show; otherwise the kind glyph stands in.</summary>
        public bool HasThumbnail => HasCover || HasPreview;

        /// <summary>The IcoFont glyph standing in for an item with neither a cover nor a preview.</summary>
        public string KindGlyph => KindGlyphFor(Kind);

        public static string KindGlyphFor(WorkshopItemKind kind)
        {
            switch (kind)
            {
                case WorkshopItemKind.Colors: return ""; // paint
                case WorkshopItemKind.NotificationStyle: return ""; // notification
                case WorkshopItemKind.ScreenshotFrame: return ""; // picture
                case WorkshopItemKind.ShowcasePage: return ""; // dashboard-web
                case WorkshopItemKind.UnlockSounds: return ""; // music-note
                case WorkshopItemKind.Bundle: return ""; // box
                default: return ""; // trophy
            }
        }

        public string Readme
        {
            get => _readme;
            set => SetValue(ref _readme, value, nameof(Readme), nameof(HasReadme));
        }

        public bool HasReadme => !string.IsNullOrWhiteSpace(_readme);

        /// <summary>True when the item is in the library; for game data, when any game has it applied.</summary>
        public bool IsInstalled
        {
            get => _isInstalled;
            set => SetValue(ref _isInstalled, value, nameof(IsInstalled), nameof(ActionLabel), nameof(CanInstall));
        }

        /// <summary>For a look in the library, true when a setting or game uses it, as the Library page shows.</summary>
        public bool IsApplied
        {
            get => _isApplied;
            set => SetValue(ref _isApplied, value, nameof(IsApplied), nameof(InstalledLabel), nameof(ActionLabel));
        }

        /// <summary>The installed state's label: game data is applied to a game; a look is applied when something uses it, else in the library.</summary>
        public string InstalledLabel => ResourceProvider.GetString(Kind == WorkshopItemKind.GameCustomData || _isApplied
            ? "LOCPlayAch_Library_Applied"
            : "LOCPlayAch_Library_InLibrary");

        public bool HasUpdate
        {
            get => _hasUpdate;
            set => SetValue(ref _hasUpdate, value, nameof(HasUpdate), nameof(ActionLabel), nameof(CanInstall));
        }

        /// <summary>For game data, the library game it has been applied to or matched, when there is one.</summary>
        public string LocalGameName
        {
            get => _localGameName;
            set => SetValue(ref _localGameName, value, nameof(LocalGameName), nameof(IsInLibrary));
        }

        public bool IsInLibrary => Kind != WorkshopItemKind.GameCustomData || !string.IsNullOrWhiteSpace(_localGameName);

        /// <summary>True when this install's submitter key published the item.</summary>
        public bool IsMine
        {
            get => _isMine;
            set => SetValue(ref _isMine, value);
        }

        /// <summary>True when this extension is too old to import the item.</summary>
        public bool RequiresNewerPlugin => WorkshopIdentityStore.IsNewer(Item.MinPluginVersion, PluginManifest.Version);

        public string RequiresNewerPluginText =>
            string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_RequiresNewerPlugin"), Item.MinPluginVersion);

        /// <summary>
        /// Install, Update, or the installed label when there is nothing newer. Looks in the
        /// library are reinstalled from the Library page; game data on a game is reset from that
        /// game's Manage Achievements.
        /// </summary>
        public string ActionLabel => HasUpdate
            ? ResourceProvider.GetString("LOCPlayAch_Workshop_Update")
            : IsInstalled
                ? Kind == WorkshopItemKind.GameCustomData
                    ? ResourceProvider.GetString("LOCPlayAch_Workshop_ApplyToGames")
                    : InstalledLabel
                : ResourceProvider.GetString("LOCPlayAch_Workshop_Install");

        /// <summary>Game data can always go onto more games; a look installs once, then updates.</summary>
        public bool CanInstall => !RequiresNewerPlugin && (Kind == WorkshopItemKind.GameCustomData || !IsInstalled || HasUpdate);

        /// <summary>Where the item is used, as the Library page lists it: each game for game data, each setting or game for a look.</summary>
        public IReadOnlyList<WorkshopUseLine> Uses
        {
            get => _uses;
            set => SetValue(ref _uses, value ?? Array.Empty<WorkshopUseLine>(), nameof(Uses), nameof(HasUses));
        }

        public bool HasUses => _uses.Count > 0;

        /// <summary>A readable line from the manifest's `contents` counts.</summary>
        public string ContentsSummary => SummarizeContents(Item.Contents, Kind);

        public static string KindLabelFor(WorkshopItemKind kind)
        {
            switch (kind)
            {
                case WorkshopItemKind.Colors: return ResourceProvider.GetString("LOCPlayAch_Workshop_Kind_Colors");
                case WorkshopItemKind.NotificationStyle: return ResourceProvider.GetString("LOCPlayAch_Common_Notifications");
                case WorkshopItemKind.ScreenshotFrame: return ResourceProvider.GetString("LOCPlayAch_Workshop_Kind_ScreenshotFrame");
                case WorkshopItemKind.ShowcasePage: return ResourceProvider.GetString("LOCPlayAch_Workshop_Kind_ShowcasePage");
                case WorkshopItemKind.UnlockSounds: return ResourceProvider.GetString("LOCPlayAch_Workshop_Share_Sounds");
                case WorkshopItemKind.Bundle: return ResourceProvider.GetString("LOCPlayAch_Workshop_Kind_Bundle");
                default: return ResourceProvider.GetString("LOCPlayAch_Workshop_Kind_GameCustomData");
            }
        }

        private static string SummarizeContents(JObject contents, WorkshopItemKind kind)
        {
            if (contents == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            void Count(string key, string labelKey)
            {
                var token = contents[key];
                if (token == null)
                {
                    return;
                }

                var label = ResourceProvider.GetString(labelKey);

                if (token.Type == JTokenType.Integer && token.Value<int>() > 0)
                {
                    parts.Add(label + ": " + token.Value<int>().ToString("N0", FormattingCulture.Current));
                }
                else if (token.Type == JTokenType.Boolean && token.Value<bool>())
                {
                    parts.Add(label);
                }
            }

            switch (kind)
            {
                case WorkshopItemKind.GameCustomData:
                    Count("achievementIcons", "LOCPlayAch_ManageAchievements_Tab_CustomIcons");
                    Count("customAchievements", "LOCPlayAch_Workshop_Contents_CustomAchievements");
                    Count("categories", "LOCPlayAch_ManageAchievements_Tab_Category");
                    Count("capstones", "LOCPlayAch_Tagging_Area_Capstones");
                    Count("achievementOverrides", "LOCPlayAch_Workshop_Contents_Overrides");
                    Count("notes", "LOCPlayAch_Tagging_Area_Notes");
                    Count("achievementOrder", "LOCPlayAch_ManageAchievements_Tab_AchievementOrder");
                    Count("metadataLinks", "LOCPlayAch_ManageAchievements_Overview_MetadataLinks");
                    break;
                case WorkshopItemKind.UnlockSounds:
                    if (contents["slots"] is JArray slots)
                    {
                        parts.Add(string.Join(", ", slots.Select(s => s.ToString())));
                    }

                    break;
                case WorkshopItemKind.Bundle:
                    if (contents["parts"] is JArray bundleParts)
                    {
                        parts.Add(string.Join(", ", bundleParts.Select(s => s.ToString())));
                    }

                    break;
                case WorkshopItemKind.ShowcasePage:
                    Count("widgets", "LOCPlayAch_Workshop_Contents_Widgets");
                    Count("images", "LOCPlayAch_Workshop_Contents_Images");
                    break;
                case WorkshopItemKind.Colors:
                    // The same labels the color set preview gives these groups.
                    Count("rarityColors", "LOCPlayAch_Column_Rarity");
                    Count("providerColors", "LOCPlayAch_Settings_Appearance_ProviderColors");
                    Count("resourceOverrides", "LOCPlayAch_Settings_Appearance_AdvancedResources");
                    break;
                default:
                    Count("toastTemplate", "LOCPlayAch_Workshop_Contents_CustomTemplate");
                    Count("frameTemplate", "LOCPlayAch_Workshop_Contents_CustomFrameTemplate");
                    Count("images", "LOCPlayAch_Workshop_Contents_Images");
                    Count("kindStyles", "LOCPlayAch_Workshop_Contents_KindStyles");
                    break;
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>One place a Workshop item is used: the place, and the version and state there.</summary>
    public sealed class WorkshopUseLine
    {
        public WorkshopUseLine(string label, string state)
        {
            Label = label;
            State = state;
        }

        public string Label { get; }

        public string State { get; }

        public bool HasState => !string.IsNullOrWhiteSpace(State);
    }
}
