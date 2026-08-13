using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PlayniteAchievements.Services.ThemeMigration
{
    /// <summary>
    /// Runtime bridge called by the compatibility-patched StartPage view. It keeps
    /// StartPage decoupled from Playnite's private desktop types until a menu opens.
    /// </summary>
    public static class StartPageGameContextMenuBridge
    {
        private const string StartPageGameModelTypeName = "LandingPage.Models.GameModel";
        private const string PlayniteGameMenuTypeName = "Playnite.DesktopApp.Controls.GameMenu";
        private static readonly ILogger Logger = LogManager.GetLogger();
        private static bool initialized;

        public static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            EventManager.RegisterClassHandler(
                typeof(FrameworkElement),
                ContextMenuService.ContextMenuOpeningEvent,
                new ContextMenuEventHandler(StartPageRoot_ContextMenuOpening),
                true);
        }

        private static bool IsStartPageView(FrameworkElement element)
        {
            return string.Equals(
                element?.GetType().FullName,
                "LandingPage.Views.StartPageView",
                StringComparison.Ordinal);
        }

        private static void StartPageRoot_ContextMenuOpening(
            object sender,
            ContextMenuEventArgs e)
        {
            if (!(sender is FrameworkElement startPageRoot) ||
                !IsStartPageView(startPageRoot))
            {
                return;
            }

            var persistedSettings = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            if (persistedSettings?.UsePlayniteContextMenuOnStartPage != true)
            {
                return;
            }

            try
            {
                var target = FindGameTarget(e.OriginalSource as DependencyObject, out var game);
                if (target == null || game == null)
                {
                    return;
                }

                var menu = CreatePlayniteGameMenu(game, target);
                if (menu == null)
                {
                    return;
                }

                // Suppress StartPage's inherited Add Shelve menu before opening the
                // native Playnite menu explicitly for this cover.
                e.Handled = true;
                menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to open Playnite's game context menu from StartPage.");
            }
        }

        private static FrameworkElement FindGameTarget(DependencyObject source, out Game game)
        {
            game = null;
            var current = source;
            while (current != null)
            {
                if (current is FrameworkElement element &&
                    TryGetStartPageGame(element.DataContext, out game))
                {
                    return element;
                }

                current = GetParent(current);
            }

            return null;
        }

        private static bool TryGetStartPageGame(object dataContext, out Game game)
        {
            game = null;
            var modelType = dataContext?.GetType();
            if (!string.Equals(modelType?.FullName, StartPageGameModelTypeName, StringComparison.Ordinal))
            {
                return false;
            }

            game = modelType.GetProperty("Game", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(dataContext) as Game;
            return game != null;
        }

        private static DependencyObject GetParent(DependencyObject child)
        {
            if (child == null)
            {
                return null;
            }

            var visualParent = child is Visual || child is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : null;
            return visualParent ?? LogicalTreeHelper.GetParent(child);
        }

        private static ContextMenu CreatePlayniteGameMenu(Game game, FrameworkElement target)
        {
            var menuType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(PlayniteGameMenuTypeName, false))
                .FirstOrDefault(type => type != null);
            if (menuType == null || !typeof(ContextMenu).IsAssignableFrom(menuType))
            {
                Logger.Warn("Playnite's desktop GameMenu type was not available.");
                return null;
            }

            var menu = Activator.CreateInstance(menuType) as ContextMenu;
            if (menu == null)
            {
                return null;
            }

            menuType.GetProperty("ShowStartSection", BindingFlags.Instance | BindingFlags.Public)
                ?.SetValue(menu, true);

            var initializeForGame = menuType.GetMethod(
                "InitializeItems",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(Game) },
                null);
            if (initializeForGame == null)
            {
                Logger.Warn("Playnite's GameMenu initializer was not available.");
                return null;
            }

            // GameMenu's own Opened handler runs first and clears unsupported external
            // DataContexts. This handler then fills it through Playnite's Game overload.
            menu.Opened += (_, __) => initializeForGame.Invoke(menu, new object[] { game });
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.MousePoint;
            return menu;
        }
    }
}
