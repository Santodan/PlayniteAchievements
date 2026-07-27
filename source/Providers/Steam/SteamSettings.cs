using Newtonsoft.Json;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PlayniteAchievements.Providers.Steam
{
    public enum SteamExistingGameImportBehavior
    {
        OverwriteExisting = 0,
        SkipExisting = 1
    }

    /// <summary>
    /// Steam-specific provider settings.
    /// </summary>
    public sealed class SteamAccountSettings : PlayniteAchievements.Common.ObservableObject
    {
        private string _accountId = Guid.NewGuid().ToString("N");
        private string _displayName = string.Empty;
        private string _steamUserId = string.Empty;
        private string _steamWebApiKey = string.Empty;

        public string AccountId
        {
            get => _accountId;
            set => SetValue(ref _accountId, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim());
        }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (SetValueAndReturn(ref _displayName, value?.Trim() ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayLabel));
                }
            }
        }

        public string SteamUserId
        {
            get => _steamUserId;
            set
            {
                if (SetValueAndReturn(ref _steamUserId, value?.Trim() ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayLabel));
                }
            }
        }

        public string SteamWebApiKey
        {
            get => _steamWebApiKey;
            set => SetValue(ref _steamWebApiKey, value?.Trim() ?? string.Empty);
        }

        [JsonIgnore]
        public string DisplayLabel
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DisplayName))
                {
                    return DisplayName;
                }

                if (!string.IsNullOrWhiteSpace(SteamUserId))
                {
                    return SteamUserId;
                }

                return "Steam Account";
            }
        }

        public SteamAccountSettings Clone()
        {
            return new SteamAccountSettings
            {
                AccountId = AccountId,
                DisplayName = DisplayName,
                SteamUserId = SteamUserId,
                SteamWebApiKey = SteamWebApiKey
            };
        }
    }

    public sealed class SteamAccountOption
    {
        public SteamAccountOption(string accountId, string displayName)
        {
            AccountId = accountId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }

        public string AccountId { get; }

        public string DisplayName { get; }
    }

    public class SteamSettings : ProviderSettingsBase
    {
        private string _steamUserId;
        private string _defaultSteamAccountId = string.Empty;
        private List<SteamAccountSettings> _steamAccounts = new List<SteamAccountSettings>();
        private bool _includeFamilySharedGames = true;
        private string _importedGameMetadataSourceId = string.Empty;
        private SteamExistingGameImportBehavior _existingGameImportBehavior = SteamExistingGameImportBehavior.OverwriteExisting;
        private bool _useSteamHuntersForCategories;
        private ObservableCollection<SteamIgnoredFriend> _ignoredFriends = new ObservableCollection<SteamIgnoredFriend>();

        /// <inheritdoc />
        public override string ProviderKey => "Steam";

        public bool UseSteamHuntersForCategories
        {
            get => _useSteamHuntersForCategories;
            set => SetValue(ref _useSteamHuntersForCategories, value);
        }

        public ObservableCollection<SteamIgnoredFriend> IgnoredFriends
        {
            get => _ignoredFriends;
            set => SetValue(ref _ignoredFriends, value ?? new ObservableCollection<SteamIgnoredFriend>());
        }

        public HashSet<string> GetIgnoredSteamIds()
        {
            return new HashSet<string>(
                IgnoredFriends.Where(friend => !string.IsNullOrWhiteSpace(friend?.SteamId))
                    .Select(friend => friend.SteamId.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        public bool IsFriendIgnored(string steamId)
        {
            return !string.IsNullOrWhiteSpace(steamId) &&
                   IgnoredFriends.Any(friend =>
                       string.Equals(friend?.SteamId, steamId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public void AddIgnoredFriend(string steamId, string displayName, string avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return;
            }

            var normalizedId = steamId.Trim();
            var existing = IgnoredFriends.FirstOrDefault(friend =>
                string.Equals(friend?.SteamId, normalizedId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.DisplayName = string.IsNullOrWhiteSpace(displayName) ? existing.DisplayName : displayName.Trim();
                existing.AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? existing.AvatarUrl : avatarUrl.Trim();
                return;
            }

            IgnoredFriends.Add(new SteamIgnoredFriend
            {
                SteamId = normalizedId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedId : displayName.Trim(),
                AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim(),
                IgnoredUtc = DateTime.UtcNow
            });
            OnPropertyChanged(nameof(IgnoredFriends));
        }

        public bool RemoveIgnoredFriend(string steamId)
        {
            var existing = IgnoredFriends.FirstOrDefault(friend =>
                string.Equals(friend?.SteamId, steamId?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                return false;
            }

            IgnoredFriends.Remove(existing);
            OnPropertyChanged(nameof(IgnoredFriends));
            return true;
        }

        /// <summary>
        /// Legacy field used only for backward-compat migration and the IsAuthenticated quick-check.
        /// Do NOT write to this to modify account credentials — use SteamAccountSettings directly.
        /// Populated by SyncLegacyFieldsFromDefaultAccount() and EnsureSteamAccountsInitialized().
        /// </summary>
        public string SteamUserId
        {
            get
            {
                EnsureSteamAccountsInitialized();
                return _steamUserId;
            }
            set
            {
                // Store value only. Never propagate to any account — doing so would
                // overwrite whichever account happens to be default at write-time,
                // causing cross-account contamination in multi-account mode.
                SetValue(ref _steamUserId, value?.Trim() ?? string.Empty);
            }
        }

        public string DefaultSteamAccountId
        {
            get
            {
                EnsureSteamAccountsInitialized();
                return _defaultSteamAccountId;
            }
            set
            {
                var normalized = value?.Trim() ?? string.Empty;
                if (SetValue(ref _defaultSteamAccountId, normalized))
                {
                    EnsureSteamAccountsInitialized();
                    SyncLegacyFieldsFromDefaultAccount();
                }
            }
        }

        public List<SteamAccountSettings> SteamAccounts
        {
            get
            {
                EnsureSteamAccountsInitialized();
                return _steamAccounts;
            }
            set
            {
                var normalizedAccounts = value ?? new List<SteamAccountSettings>();
                if (SetValue(ref _steamAccounts, normalizedAccounts))
                {
                    // If the account list is intentionally cleared by the user,
                    // clear legacy fields first so backward-compat migration does
                    // not immediately recreate a removed API-key account.
                    if (_steamAccounts.Count == 0)
                    {
                        _defaultSteamAccountId = string.Empty;
                        _steamUserId = string.Empty;
                        _steamWebApiKey = string.Empty;
                    }

                    EnsureSteamAccountsInitialized();
                    SyncLegacyFieldsFromDefaultAccount();
                }
            }
        }

        public bool IncludeFamilySharedGames
        {
            get => _includeFamilySharedGames;
            set => SetValue(ref _includeFamilySharedGames, value);
        }

        public string ImportedGameMetadataSourceId
        {
            get => _importedGameMetadataSourceId;
            set => SetValue(ref _importedGameMetadataSourceId, value ?? string.Empty);
        }

        public SteamExistingGameImportBehavior ExistingGameImportBehavior
        {
            get => _existingGameImportBehavior;
            set => SetValue(ref _existingGameImportBehavior, value);
        }

        private string _steamWebApiKey = string.Empty;

        /// <summary>
        /// Optional Steam Web API developer key used to fetch localized achievement descriptions
        /// (including hidden ones). Leave blank to use the active Steam session token automatically.
        /// Obtain a key at https://steamcommunity.com/dev/apikey
        /// </summary>
        public string SteamWebApiKey
        {
            get
            {
                EnsureSteamAccountsInitialized();
                return _steamWebApiKey;
            }
            set
            {
                // Store value only. Never propagate to any account.
                SetValue(ref _steamWebApiKey, value?.Trim() ?? string.Empty);
            }
        }

        public SteamAccountSettings GetDefaultAccount()
        {
            EnsureSteamAccountsInitialized();
            return _steamAccounts.FirstOrDefault(account =>
                account != null &&
                string.Equals(account.AccountId, _defaultSteamAccountId, StringComparison.OrdinalIgnoreCase));
        }

        public SteamAccountSettings GetAccountById(string accountId)
        {
            EnsureSteamAccountsInitialized();
            var normalizedId = accountId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                return null;
            }

            return _steamAccounts.FirstOrDefault(account =>
                account != null &&
                string.Equals(account.AccountId, normalizedId, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<SteamAccountOption> GetSelectableAccountOptions(bool includeAutomatic)
        {
            EnsureSteamAccountsInitialized();
            var options = new List<SteamAccountOption>();

            if (includeAutomatic)
            {
                options.Add(new SteamAccountOption(string.Empty, "Automatic (default Steam account)"));
            }

            foreach (var account in _steamAccounts)
            {
                if (account == null || string.IsNullOrWhiteSpace(account.AccountId))
                {
                    continue;
                }

                options.Add(new SteamAccountOption(account.AccountId, account.DisplayLabel));
            }

            return options;
        }

        private void EnsureSteamAccountsInitialized()
        {
            if (_steamAccounts == null)
            {
                _steamAccounts = new List<SteamAccountSettings>();
            }

            _steamAccounts = _steamAccounts
                .Where(account => account != null)
                .Select(account =>
                {
                    account.AccountId = string.IsNullOrWhiteSpace(account.AccountId)
                        ? Guid.NewGuid().ToString("N")
                        : account.AccountId.Trim();
                    account.DisplayName = account.DisplayName?.Trim() ?? string.Empty;
                    account.SteamUserId = account.SteamUserId?.Trim() ?? string.Empty;
                    account.SteamWebApiKey = account.SteamWebApiKey?.Trim() ?? string.Empty;
                    return account;
                })
                .GroupBy(account => account.AccountId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            // Backward-compat migration from legacy single-account fields.
            if (_steamAccounts.Count == 0 &&
                (!string.IsNullOrWhiteSpace(_steamUserId) || !string.IsNullOrWhiteSpace(_steamWebApiKey)))
            {
                _steamAccounts.Add(new SteamAccountSettings
                {
                    AccountId = Guid.NewGuid().ToString("N"),
                    DisplayName = "Default",
                    SteamUserId = _steamUserId?.Trim() ?? string.Empty,
                    SteamWebApiKey = _steamWebApiKey?.Trim() ?? string.Empty
                });
            }

            if (_steamAccounts.Count == 0)
            {
                _defaultSteamAccountId = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(_defaultSteamAccountId) ||
                !_steamAccounts.Any(account => string.Equals(account.AccountId, _defaultSteamAccountId, StringComparison.OrdinalIgnoreCase)))
            {
                _defaultSteamAccountId = _steamAccounts[0].AccountId;
            }

            SyncLegacyFieldsFromDefaultAccount();
        }

        private void SyncLegacyFieldsFromDefaultAccount()
        {
            var defaultAccount = _steamAccounts.FirstOrDefault(account =>
                account != null &&
                string.Equals(account.AccountId, _defaultSteamAccountId, StringComparison.OrdinalIgnoreCase));

            if (defaultAccount == null)
            {
                _steamUserId = string.Empty;
                _steamWebApiKey = string.Empty;
                return;
            }

            _steamUserId = defaultAccount.SteamUserId?.Trim() ?? string.Empty;
            _steamWebApiKey = defaultAccount.SteamWebApiKey?.Trim() ?? string.Empty;
        }
    }

    public sealed class SteamIgnoredFriend
    {
        public string SteamId { get; set; }
        public string DisplayName { get; set; }
        public string AvatarUrl { get; set; }
        public DateTime IgnoredUtc { get; set; }
    }
}
