using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using System;
using System.Globalization;

namespace PlayniteAchievements.Services.Friends
{
    /// <summary>
    /// Single source of truth for how a friend's name is displayed. Precedence: the user's
    /// manual plugin rename, then the <see cref="FriendNameDisplayMode"/> applied to the
    /// provider profile (persona) name and provider-assigned nickname, then the fallback id.
    /// </summary>
    public static class FriendDisplayNameResolver
    {
        private const string CombinedFormat = "{0} ({1})";

        public static string Resolve(
            string manualNickname,
            string personaName,
            string providerNickname,
            FriendNameDisplayMode mode,
            string fallback = null)
        {
            var manual = TrimOrNull(manualNickname);
            if (manual != null)
            {
                return manual;
            }

            var persona = TrimOrNull(personaName) ?? TrimOrNull(fallback);
            var nickname = TrimOrNull(providerNickname);

            // Degenerate cases collapse to a single value; the equality guard prevents
            // "Nick (Nick)" when a persona lookup failed and both fields hold the nickname.
            if (nickname == null || persona == null ||
                string.Equals(nickname, persona, StringComparison.OrdinalIgnoreCase))
            {
                return persona ?? nickname;
            }

            switch (mode)
            {
                case FriendNameDisplayMode.Nickname:
                    return nickname;
                case FriendNameDisplayMode.PersonaAndNickname:
                    return string.Format(CultureInfo.CurrentCulture, CombinedFormat, persona, nickname);
                default:
                    return persona;
            }
        }

        public static string Resolve(FriendSettingsEntry entry, FriendNameDisplayMode mode)
        {
            if (entry == null)
            {
                return null;
            }

            return Resolve(entry.Nickname, entry.DisplayName, entry.ProviderNickname, mode, entry.ExternalUserId);
        }

        public static string Resolve(FriendIdentity identity, FriendSettingsEntry entryOrNull, FriendNameDisplayMode mode)
        {
            if (identity == null)
            {
                return Resolve(entryOrNull, mode);
            }

            return Resolve(
                entryOrNull?.Nickname,
                TrimOrNull(identity.DisplayName) ?? entryOrNull?.DisplayName,
                TrimOrNull(identity.ProviderNickname) ?? entryOrNull?.ProviderNickname,
                mode,
                identity.ExternalUserId);
        }

        private static string TrimOrNull(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
