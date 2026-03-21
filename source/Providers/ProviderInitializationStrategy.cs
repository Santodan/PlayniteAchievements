using System.Collections.Generic;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Epic;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.GOG;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.PSN;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Providers.Xbox;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers
{
    internal static class ProviderInitializationStrategy
    {
        public static List<IDataProvider> CreateProviders(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath,
            SteamSessionManager steamSessionManager,
            GogSessionManager gogSessionManager,
            EpicSessionManager epicSessionManager,
            PsnSessionManager psnSessionManager,
            XboxSessionManager xboxSessionManager,
            ExophaseSessionManager exophaseSessionManager,
            out ManualAchievementsProvider manualProvider)
        {
            manualProvider = new ManualAchievementsProvider(
                logger,
                settings,
                pluginUserDataPath,
                playniteApi,
                exophaseSessionManager);

            return new List<IDataProvider>
            {
                manualProvider,  // Explicit user overrides take priority.
                new ExophaseDataProvider(
                    logger,
                    settings,
                    playniteApi,
                    exophaseSessionManager),  // Can claim games from other providers.
                new SteamDataProvider(
                    logger,
                    settings,
                    playniteApi,
                    steamSessionManager,
                    pluginUserDataPath),
                new GogDataProvider(
                    logger,
                    settings,
                    playniteApi,
                    pluginUserDataPath,
                    gogSessionManager),
                new EpicDataProvider(
                    logger,
                    settings,
                    playniteApi,
                    epicSessionManager),
                new PsnDataProvider(
                    logger,
                    settings,
                    psnSessionManager),
                new XboxDataProvider(
                    logger,
                    settings,
                    xboxSessionManager),
                new RetroAchievementsDataProvider(
                    logger,
                    settings,
                    playniteApi,
                    pluginUserDataPath),
                new ShadPS4DataProvider(
                    logger,
                    settings,
                    playniteApi),
                new Rpcs3DataProvider(
                    logger,
                    settings,
                    playniteApi),
                new XeniaDataProvider(
                    logger,
                    settings,
                    playniteApi,
                    pluginUserDataPath)
            };
        }

        public static void RegisterAuthPrimers(
            ProviderRegistry providerRegistry,
            SteamSessionManager steamSessionManager,
            GogSessionManager gogSessionManager,
            EpicSessionManager epicSessionManager,
            PsnSessionManager psnSessionManager,
            XboxSessionManager xboxSessionManager,
            ExophaseSessionManager exophaseSessionManager)
        {
            providerRegistry.RegisterAuthPrimer("Steam", steamSessionManager.PrimeAuthenticationStateAsync);
            providerRegistry.RegisterAuthPrimer("GOG", gogSessionManager.PrimeAuthenticationStateAsync);
            providerRegistry.RegisterAuthPrimer("Epic", epicSessionManager.PrimeAuthenticationStateAsync);
            providerRegistry.RegisterAuthPrimer("PSN", psnSessionManager.PrimeAuthenticationStateAsync);
            providerRegistry.RegisterAuthPrimer("Xbox", xboxSessionManager.PrimeAuthenticationStateAsync);
            providerRegistry.RegisterAuthPrimer("Exophase", exophaseSessionManager.PrimeAuthenticationStateAsync);
        }
    }
}
