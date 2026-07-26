namespace PlayniteAchievements.Models.Settings
{
    using System;
    using System.Collections.Generic;

    public sealed class ResourceOverrideDescriptor
    {
        public ResourceOverrideDescriptor(
            string resourceKey,
            string displayName,
            ResourceOverrideValueKind valueKind,
            string playniteResourceKey,
            params string[] fallbackPlayniteResourceKeys)
        {
            ResourceKey = resourceKey;
            DisplayName = displayName;
            ValueKind = valueKind;
            PlayniteResourceKey = playniteResourceKey;
            FallbackPlayniteResourceKeys = fallbackPlayniteResourceKeys ?? Array.Empty<string>();
        }

        public string ResourceKey { get; }
        public string DisplayName { get; }
        public ResourceOverrideValueKind ValueKind { get; }
        public string PlayniteResourceKey { get; }
        public IReadOnlyList<string> FallbackPlayniteResourceKeys { get; }
    }
}
