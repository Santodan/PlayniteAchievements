using System;

namespace PlayniteAchievements.Providers.Steam.Local
{
    internal static class SteamIdHelper
    {
        private const ulong SteamId64Base = 76561197960265728UL;

        public static bool TryGetAccountId3(string steamId64, out uint accountId3)
        {
            accountId3 = 0;
            if (!ulong.TryParse((steamId64 ?? string.Empty).Trim(), out var parsed) ||
                parsed < SteamId64Base)
            {
                return false;
            }

            var difference = parsed - SteamId64Base;
            if (difference > uint.MaxValue)
            {
                return false;
            }

            accountId3 = (uint)difference;
            return true;
        }
    }
}
