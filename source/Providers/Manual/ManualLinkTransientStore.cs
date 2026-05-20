using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Providers.Manual
{
    internal static class ManualLinkTransientStore
    {
        internal sealed class Snapshot
        {
            public Snapshot(ManualAchievementLink link)
            {
                Link = link?.Clone();
            }

            public ManualAchievementLink Link { get; }
        }

        public static Snapshot Register(Guid playniteGameId, ManualAchievementLink link, ManualSettings settings = null)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            var links = ResolveLinks(settings);
            links.TryGetValue(playniteGameId, out var previous);
            links[playniteGameId] = link.Clone();

            return new Snapshot(previous);
        }

        public static void Restore(Guid playniteGameId, Snapshot snapshot, ManualSettings settings = null)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            var links = ResolveLinks(settings);

            if (snapshot?.Link != null)
            {
                links[playniteGameId] = snapshot.Link.Clone();
            }
            else
            {
                links.Remove(playniteGameId);
            }
        }

        public static void Remove(Guid playniteGameId, ManualSettings settings = null)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            ResolveLinks(settings).Remove(playniteGameId);
        }

        private static Dictionary<Guid, ManualAchievementLink> ResolveLinks(ManualSettings settings)
        {
            var manualSettings = settings ?? ProviderRegistry.Settings<ManualSettings>();
            if (manualSettings.AchievementLinks == null)
            {
                manualSettings.AchievementLinks = new Dictionary<Guid, ManualAchievementLink>();
            }

            return manualSettings.AchievementLinks;
        }
    }
}
