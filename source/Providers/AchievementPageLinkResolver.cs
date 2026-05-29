using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers
{
    public sealed class AchievementPageLinkResolver
    {
        private readonly IReadOnlyList<IDataProvider> _providers;

        public AchievementPageLinkResolver(IReadOnlyList<IDataProvider> providers)
        {
            _providers = providers ?? Array.Empty<IDataProvider>();
        }

        public bool CanResolve(AchievementPageLinkContext context)
        {
            var provider = ResolveProvider(context);
            return provider?.CanResolveAchievementPageUrl(context) == true;
        }

        public async Task<string> ResolveUrlAsync(
            AchievementPageLinkContext context,
            CancellationToken cancel)
        {
            var provider = ResolveProvider(context);
            if (provider == null || !provider.CanResolveAchievementPageUrl(context))
            {
                return null;
            }

            var url = await provider.GetAchievementPageUrlAsync(context, cancel).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        }

        internal IAchievementPageLinkProvider ResolveProvider(AchievementPageLinkContext context)
        {
            if (context == null)
            {
                return null;
            }

            var sourceProviderKey = ResolveSourceProviderKey(context);
            if (!string.IsNullOrWhiteSpace(sourceProviderKey))
            {
                var provider = FindLinkProviderByKey(sourceProviderKey);
                if (provider != null)
                {
                    return provider;
                }
            }

            if (!string.IsNullOrWhiteSpace(context.CachedProviderKey))
            {
                return null;
            }

            var game = context.Game;
            return _providers
                .OfType<IAchievementPageLinkProvider>()
                .FirstOrDefault(provider =>
                {
                    var dataProvider = provider as IDataProvider;
                    return dataProvider?.IsCapable(game) == true &&
                           provider.CanResolveAchievementPageUrl(context);
                });
        }

        internal static string ResolveSourceProviderKey(AchievementPageLinkContext context)
        {
            var cachedProviderKey = NormalizeProviderKey(context?.CachedProviderKey);
            var manualSourceKey = NormalizeProviderKey(context?.ManualLink?.SourceKey);

            if (string.Equals(cachedProviderKey, "Manual", StringComparison.OrdinalIgnoreCase))
            {
                return manualSourceKey;
            }

            if (string.IsNullOrWhiteSpace(cachedProviderKey) &&
                !string.IsNullOrWhiteSpace(manualSourceKey))
            {
                return manualSourceKey;
            }

            return cachedProviderKey;
        }

        private IAchievementPageLinkProvider FindLinkProviderByKey(string providerKey)
        {
            var normalized = NormalizeProviderKey(providerKey);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return _providers
                .Where(provider => provider != null &&
                                   string.Equals(provider.ProviderKey, normalized, StringComparison.OrdinalIgnoreCase))
                .OfType<IAchievementPageLinkProvider>()
                .FirstOrDefault();
        }

        private static string NormalizeProviderKey(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
