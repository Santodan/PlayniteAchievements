// Runs the ACTUAL bundled slide storyboard, loaded out of the built plugin assembly, through the
// ACTUAL resolve/retarget/build logic from ToastNotificationService, against a real layered window --
// then reports how far the card really moved.
//
// The previous probe only proved that a hand-built storyboard on a hand-built transform group
// animates. That is not the thing that was broken. This one starts from the resource the plugin
// actually reads, so if the shipped path still animates nothing, it fails here.
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

internal static class SlideStoryboardProbe
{
    private const string SlideTargetPath =
        "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)";
    private const string LegacySlideTargetPath = "(Window.Top)";
    private const string BareSlideTargetPath = "(UIElement.RenderTransform).(TranslateTransform.Y)";

    private const string ResourceUri =
        "pack://application:,,,/PlayniteAchievements;component/Resources/NotificationResources.xaml";

    private static string _pluginDir;

    // Mirrors ToastNotificationService.BuildSlidePath / AnimatesSlide.
    private static PropertyPath BuildSlidePath()
    {
        return new PropertyPath(
            "(0).(1)[1].(2)",
            UIElement.RenderTransformProperty,
            TransformGroup.ChildrenProperty,
            TranslateTransform.YProperty);
    }

    private static bool AnimatesSlide(Timeline child)
    {
        var path = Storyboard.GetTargetProperty(child);
        if (path == null || string.IsNullOrEmpty(path.Path) || path.Path == LegacySlideTargetPath)
        {
            return true;
        }

        var parameters = path.PathParameters;
        if (parameters != null && parameters.Count > 0)
        {
            return parameters[parameters.Count - 1] == TranslateTransform.YProperty;
        }

        return path.Path == SlideTargetPath;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        _pluginDir = args.Length > 0
            ? args[0]
            : @"C:\Users\Justin\Desktop\PlayniteAchievements\source\bin\Debug";

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failed = 0;

        app.Startup += (s, e) =>
        {
            try
            {
                LoadPlugin();
                foreach (var key in new[]
                {
                    "PlayAch.Storyboard.ToastSlideIn",
                    "PlayAch.Storyboard.ToastSlideOut"
                })
                {
                    failed += RunOne(key);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("probe failed: " + ex);
                failed++;
            }
            finally
            {
                app.Shutdown();
            }
        };

        app.Run();
        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "PASS - the shipped storyboards move the card" : "FAIL (" + failed + ")");
        return failed == 0 ? 0 : 1;
    }

    private static void LoadPlugin()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var name = new AssemblyName(e.Name).Name;
            var candidate = Path.Combine(_pluginDir, name + ".dll");
            if (File.Exists(candidate))
            {
                return Assembly.LoadFrom(candidate);
            }

            var packages = Path.GetFullPath(Path.Combine(_pluginDir, "..", "..", "packages"));
            if (!Directory.Exists(packages))
            {
                return null;
            }

            foreach (var found in Directory.GetFiles(packages, name + ".dll", SearchOption.AllDirectories))
            {
                if (found.IndexOf("net4", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Assembly.LoadFrom(found);
                }
            }

            return null;
        };

        Assembly.LoadFrom(Path.Combine(_pluginDir, "PlayniteAchievements.dll"));
    }

    /// <summary>ToastNotificationService.ResolveSlideStoryboard, verbatim in its retarget rules.</summary>
    private static Storyboard Resolve(Storyboard authored, out double durationMs, out bool travels)
    {
        durationMs = 0;
        travels = true;

        var storyboard = authored.Clone();
        var resolved = 0d;
        var movesCard = false;
        foreach (var child in storyboard.Children)
        {
            if (Storyboard.GetTargetName(child) != null || Storyboard.GetTarget(child) != null)
            {
                continue;
            }

            if (AnimatesSlide(child))
            {
                Storyboard.SetTargetProperty(child, BuildSlidePath());
                movesCard = true;
            }

            if (child.Duration.HasTimeSpan)
            {
                resolved = Math.Max(resolved, child.Duration.TimeSpan.TotalMilliseconds);
            }
        }

        if (resolved <= 0)
        {
            return null;
        }

        travels = movesCard;
        durationMs = resolved;
        return storyboard;
    }

    /// <summary>ToastNotificationService.BuildSlideStoryboard, verbatim.</summary>
    private static Storyboard Build(Storyboard authored, FrameworkElement host, double fromDip, double toDip)
    {
        var storyboard = authored.Clone();
        foreach (var child in storyboard.Children)
        {
            if (Storyboard.GetTargetName(child) != null || Storyboard.GetTarget(child) != null)
            {
                continue;
            }

            Storyboard.SetTarget(child, host);
            var slide = child as DoubleAnimation;
            if (slide != null && !slide.From.HasValue && !slide.To.HasValue && AnimatesSlide(child))
            {
                slide.From = fromDip;
                slide.To = toDip;
            }
        }

        return storyboard;
    }

    private static int RunOne(string key)
    {
        var dictionary = new ResourceDictionary { Source = new Uri(ResourceUri, UriKind.Absolute) };
        var authored = dictionary[key] as Storyboard;
        if (authored == null)
        {
            Console.WriteLine("{0,-38} MISSING from the dictionary", key);
            return 1;
        }

        Console.WriteLine(
            "{0}\n  authored: children={1} frozen={2} targetPath={3}",
            key,
            authored.Children.Count,
            authored.IsFrozen,
            Storyboard.GetTargetProperty(authored.Children[0]) == null
                ? "(none)"
                : Storyboard.GetTargetProperty(authored.Children[0]).Path);

        double durationMs;
        bool travels;
        var resolved = Resolve(authored, out durationMs, out travels);
        Console.WriteLine(
            "  resolved: {0} durationMs={1:0} travels={2}",
            resolved == null ? "NULL (falls back to built-in)" : "ok",
            durationMs,
            travels);

        if (resolved == null)
        {
            // Not a failure by itself -- the plugin falls back -- but the bundled ones should resolve.
            Console.WriteLine("  => the bundled storyboard did not resolve; the plugin would use its built-in slide.");
            return 1;
        }

        // The real host shape.
        var slideTransform = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(1, 1));
        group.Children.Add(slideTransform);

        var card = new Border { Width = 442, Height = 138, Background = Brushes.SteelBlue };
        var host = new Grid
        {
            IsHitTestVisible = false,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            RenderTransform = group,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        host.Children.Add(card);

        var window = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Left = 100,
            Top = 100,
            Opacity = 0,
            Content = host,
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            const double travel = 178d;
            var storyboard = Build(resolved, host, travel, 0d);
            slideTransform.Y = travel;

            var minY = double.MaxValue;
            var maxY = double.MinValue;
            var frames = 0;
            var frame = new DispatcherFrame();
            EventHandler tick = null;
            tick = (s, e) =>
            {
                frames++;
                var y = slideTransform.Y;
                if (y < minY)
                {
                    minY = y;
                }

                if (y > maxY)
                {
                    maxY = y;
                }

                if (frames > 60)
                {
                    CompositionTarget.Rendering -= tick;
                    frame.Continue = false;
                }
            };

            CompositionTarget.Rendering += tick;
            storyboard.Begin(host, true);
            Dispatcher.PushFrame(frame);
            CompositionTarget.Rendering -= tick;

            var moved = maxY - minY;
            Console.WriteLine(
                "  ran:      frames={0} movedDip={1:0.0} finalY={2:0.00}", frames, moved, slideTransform.Y);

            if (moved < 1.0)
            {
                Console.WriteLine("  => THE CARD DID NOT MOVE. The storyboard animated no property.");
                return 1;
            }

            Console.WriteLine("  => moved.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  THREW " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                window.Close();
            }
            catch
            {
            }
        }
    }
}
