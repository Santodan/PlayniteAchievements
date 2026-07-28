using System;
using System.Collections.Generic;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Localization;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Resolves the localized defaults for the four editable notification header strings and
    /// re-normalizes un-customized stored values at startup. Uses the same known-default
    /// mechanism as tag names, with one storage difference: an unedited stored header is set
    /// back to null (= inherit the current language) instead of being rewritten to the
    /// current-language string, which makes the value self-relocalizing from then on.
    /// </summary>
    public sealed class NotificationHeaderTextService
    {
        public const string UnlockHeaderId = nameof(NotificationHeaderTextSettings.UnlockHeader);
        public const string FriendUnlockHeaderFormatId = nameof(NotificationHeaderTextSettings.FriendUnlockHeaderFormat);
        public const string CompletionHeaderId = nameof(NotificationHeaderTextSettings.CompletionHeader);
        public const string FriendCompletionHeaderFormatId = nameof(NotificationHeaderTextSettings.FriendCompletionHeaderFormat);

        private const string UnlockHeaderKey = "LOCPlayAch_Toast_AchievementUnlocked";
        private const string FriendUnlockedKey = "LOCPlayAch_Toast_FriendUnlocked";
        private const string CongratulationsKey = "LOCPlayAch_Toast_Congratulations";
        private const string CompletedTheGameKey = "LOCPlayAch_Toast_CompletedTheGame";

        private readonly string _localizationDirectory;
        private readonly ILogger _logger;
        private readonly Lazy<LocalizedDefaultStringCatalog> _catalog;

        public NotificationHeaderTextService(string localizationDirectory, ILogger logger = null)
        {
            _localizationDirectory = localizationDirectory;
            _logger = logger;
            _catalog = new Lazy<LocalizedDefaultStringCatalog>(BuildCatalog);
        }

        /// <summary>
        /// Current-language default for the own-unlock header line.
        /// </summary>
        public static string GetDefaultUnlockHeader() =>
            ResourceProvider.GetString(UnlockHeaderKey);

        /// <summary>
        /// Current-language default for the friend-unlock header format ({0} = friend name).
        /// </summary>
        public static string GetDefaultFriendUnlockHeaderFormat() =>
            ResourceProvider.GetString(FriendUnlockedKey);

        /// <summary>
        /// Current-language default for the own game-completion header line.
        /// </summary>
        public static string GetDefaultCompletionHeader() =>
            ResourceProvider.GetString(CongratulationsKey);

        /// <summary>
        /// Current-language default for the friend game-completion header format
        /// ({0} = friend name). Composed the way the templates historically rendered it:
        /// the friend's name followed by the localized "completed the game" text.
        /// </summary>
        public static string GetDefaultFriendCompletionHeaderFormat() =>
            "{0} " + ResourceProvider.GetString(CompletedTheGameKey);

        /// <summary>
        /// Returns the value to persist for an edited header: null when the edit is blank or
        /// equals the current-language default (= keep inheriting), otherwise the trimmed edit.
        /// </summary>
        public static string NormalizeForStore(string edited, string currentDefault)
        {
            if (string.IsNullOrWhiteSpace(edited))
            {
                return null;
            }

            edited = edited.Trim();
            return string.Equals(edited, currentDefault?.Trim(), StringComparison.Ordinal)
                ? null
                : edited;
        }

        /// <summary>
        /// True when <paramref name="format"/> is usable as a friend header format: non-blank,
        /// contains a {0} placeholder, and formats without throwing.
        /// </summary>
        public static bool IsValidHeaderFormat(string format)
        {
            if (string.IsNullOrWhiteSpace(format) || !format.Contains("{0}"))
            {
                return false;
            }

            try
            {
                string.Format(format, "x");
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// Normalizes every stored header (global style and each per-provider copy) that still
        /// matches a shipped default in any language back to null so it follows the current
        /// language. Returns true when any value changed and settings should be persisted.
        /// </summary>
        public bool RelocalizeDefaultHeaderTexts(PersistedSettings settings)
        {
            if (settings == null)
            {
                return false;
            }

            var changed = RelocalizeStyle(settings.NotificationStyle);
            foreach (var style in settings.ProviderNotificationStyles.Values)
            {
                changed |= RelocalizeStyle(style);
            }

            return changed;
        }

        private bool RelocalizeStyle(NotificationStyleSettings style)
        {
            var texts = style?.HeaderTexts;
            if (texts == null)
            {
                return false;
            }

            var changed = false;
            if (IsStoredDefault(UnlockHeaderId, texts.UnlockHeader))
            {
                texts.UnlockHeader = null;
                changed = true;
            }

            if (IsStoredDefault(FriendUnlockHeaderFormatId, texts.FriendUnlockHeaderFormat))
            {
                texts.FriendUnlockHeaderFormat = null;
                changed = true;
            }

            if (IsStoredDefault(CompletionHeaderId, texts.CompletionHeader))
            {
                texts.CompletionHeader = null;
                changed = true;
            }

            if (IsStoredDefault(FriendCompletionHeaderFormatId, texts.FriendCompletionHeaderFormat))
            {
                texts.FriendCompletionHeaderFormat = null;
                changed = true;
            }

            return changed;
        }

        private bool IsStoredDefault(string definitionId, string stored)
        {
            return !string.IsNullOrWhiteSpace(stored) && _catalog.Value.IsKnownDefault(definitionId, stored);
        }

        private LocalizedDefaultStringCatalog BuildCatalog()
        {
            var definitions = new List<LocalizedDefaultDefinition>
            {
                new LocalizedDefaultDefinition
                {
                    Id = UnlockHeaderId,
                    ResourceKeys = new[] { UnlockHeaderKey },
                    Compose = values => values[0]
                },
                new LocalizedDefaultDefinition
                {
                    Id = FriendUnlockHeaderFormatId,
                    ResourceKeys = new[] { FriendUnlockedKey },
                    Compose = values => values[0]
                },
                new LocalizedDefaultDefinition
                {
                    Id = CompletionHeaderId,
                    ResourceKeys = new[] { CongratulationsKey },
                    Compose = values => values[0]
                },
                new LocalizedDefaultDefinition
                {
                    Id = FriendCompletionHeaderFormatId,
                    ResourceKeys = new[] { CompletedTheGameKey },
                    Compose = values => "{0} " + values[0]
                }
            };

            return new LocalizedDefaultStringCatalog(_localizationDirectory, definitions, _logger);
        }
    }
}
