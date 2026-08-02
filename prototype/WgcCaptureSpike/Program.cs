using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using static WgcCaptureSpike.NativeInterop;

namespace WgcCaptureSpike
{
    /// <summary>
    /// WGC per-window capture feasibility spike. Captures a chosen window (occluded/unfocused is the
    /// point) to a PNG and logs diagnostics against the go/no-go gates in
    /// docs/notes/hdr-occlusion-capture.md.
    ///
    /// Usage:
    ///   WgcCaptureSpike --title "Elden Ring" [--hdr auto|on|off] [--white 4.0] [--out shot.png]
    ///   WgcCaptureSpike --foreground --delay 5      (alt-tab to / cover the target during the countdown)
    ///
    /// To exercise OCCLUSION: target a window by --title, then cover it with another window before
    /// (or during) capture. To exercise UNFOCUSED: keep another window focused while capturing by title.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var opts = Options.Parse(args);
                Log($"=== WGC capture spike ===");
                Log($"OS build: {GetOsBuild()} (GATE 8: need 1903+ = build 18362+; borderless needs 20348+/Win11)");
                Log($".NET runtime: {Environment.Version}, 64-bit process: {Environment.Is64BitProcess} (GATE 1: net462 x64)");

                var hwnd = ResolveTarget(opts);
                if (hwnd == IntPtr.Zero)
                {
                    Log("No target window resolved. Use --title <substring> or --foreground.");
                    return 2;
                }

                LogTarget(hwnd);

                bool hdr;
                switch (opts.HdrMode)
                {
                    case "on":
                        hdr = true;
                        break;
                    case "off":
                        hdr = false;
                        break;
                    default:
                        hdr = HdrDisplayDetector.IsHdrActive(hwnd);
                        break;
                }
                Log($"HDR (mode={opts.HdrMode}) resolved to: {hdr} (GATE 5)");

                var capture = new WgcCapture();
                var result = capture.Capture(hwnd, hdr, opts.ManualWhite, opts.WarmupMs, Log);

                var outPath = Path.GetFullPath(opts.OutPath);
                result.Bitmap.Save(outPath, ImageFormat.Png);
                result.Bitmap.Dispose();

                Log("");
                Log("=== RESULT ===");
                Log($"Saved: {outPath}");
                Log($"Size: {result.Width}x{result.Height}, format: {result.PixelFormat}");
                Log($"HDR: {result.Hdr}, max linear channel: {result.MaxLinearChannel:0.###} " +
                    $"(GATE 5: >1.0 confirms real HDR content)");
                Log($"Border disabled: {result.BorderDisabled} (GATE 6)");
                Log($"Elapsed: {result.ElapsedMs} ms (GATE 7: target < ~500 ms)");
                Log("");
                Log("Inspect the PNG: does it show the TARGET window content (not a covering window),");
                Log("correctly exposed (not blown out on HDR)? Record go/no-go in the planning doc.");
                return 0;
            }
            catch (Exception ex)
            {
                Log("");
                Log("!!! CAPTURE FAILED !!!");
                Log(ex.ToString());
                Log("");
                Log("If this is an InvalidCastException / 'interface not supported' at CreateForWindow,");
                Log("that is GATE 1 (net462 WinRT interop) failing -> WGC no-go, fall back to Branch B.");
                return 1;
            }
        }

        private static IntPtr ResolveTarget(Options opts)
        {
            if (!string.IsNullOrEmpty(opts.Title))
            {
                var matches = FindWindowsByTitle(opts.Title);
                if (matches.Count == 0)
                {
                    Log($"No visible window title contains '{opts.Title}'.");
                    return IntPtr.Zero;
                }

                Log($"Matched {matches.Count} window(s) for '{opts.Title}':");
                foreach (var m in matches)
                {
                    Log($"  hwnd=0x{m.Item1.ToInt64():X}  '{m.Item2}'");
                }

                // Prefer an exact (case-insensitive) title match over a substring hit, so --title
                // "Steam" targets the Steam window and not "spike-steam2.png ... - VS Code".
                foreach (var m in matches)
                {
                    if (string.Equals(m.Item2, opts.Title, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"  -> exact-title match: 0x{m.Item1.ToInt64():X}");
                        return m.Item1;
                    }
                }

                return matches[0].Item1;
            }

            // --foreground: countdown so the user can alt-tab to / cover the target.
            if (opts.Foreground)
            {
                for (var s = opts.DelaySeconds; s > 0; s--)
                {
                    Log($"Capturing the foreground window in {s}s... (switch/cover now)");
                    Thread.Sleep(1000);
                }

                return GetForegroundWindow();
            }

            return IntPtr.Zero;
        }

        private static void LogTarget(IntPtr hwnd)
        {
            GetWindowRect(hwnd, out var rect);
            var iconic = IsIconic(hwnd);
            var foreground = GetForegroundWindow();
            Log($"Target hwnd=0x{hwnd.ToInt64():X}, title='{GetTitle(hwnd)}'");
            Log($"  rect={rect}, minimized(IsIconic)={iconic} " +
                $"(GATE: minimized is a NON-goal; expect failure/stale if true)");
            Log($"  foreground hwnd=0x{foreground.ToInt64():X} '{GetTitle(foreground)}' " +
                $"-> target is {(foreground == hwnd ? "FOCUSED" : "UNFOCUSED (GATE 3)")}");
        }

        private static List<Tuple<IntPtr, string>> FindWindowsByTitle(string substring)
        {
            var results = new List<Tuple<IntPtr, string>>();
            var self = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h))
                {
                    return true;
                }

                var title = GetTitle(h);
                if (!string.IsNullOrEmpty(title) &&
                    title.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    results.Add(Tuple.Create(h, title));
                }

                return true;
            }, IntPtr.Zero);
            return results;
        }

        private static string GetTitle(IntPtr hwnd)
        {
            var buffer = new char[512];
            var len = GetWindowTextW(hwnd, buffer, buffer.Length);
            return len > 0 ? new string(buffer, 0, len) : string.Empty;
        }

        private static string GetOsBuild()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var build = key?.GetValue("CurrentBuildNumber")?.ToString();
                    var ubr = key?.GetValue("UBR")?.ToString();
                    var product = key?.GetValue("ProductName")?.ToString();
                    return $"{product} build {build}.{ubr}";
                }
            }
            catch
            {
                return Environment.OSVersion.ToString();
            }
        }

        private static void Log(string message) => Console.WriteLine(message);

        private sealed class Options
        {
            public string Title;
            public bool Foreground;
            public int DelaySeconds = 5;
            public string HdrMode = "auto";
            public float ManualWhite;
            public int WarmupMs = 350;
            public string OutPath = "wgc-capture.png";

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (var i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--title":
                            o.Title = Next(args, ref i);
                            break;
                        case "--foreground":
                            o.Foreground = true;
                            break;
                        case "--delay":
                            o.DelaySeconds = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                            break;
                        case "--hdr":
                            o.HdrMode = Next(args, ref i);
                            break;
                        case "--white":
                            o.ManualWhite = float.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                            break;
                        case "--warmup":
                            o.WarmupMs = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                            break;
                        case "--out":
                            o.OutPath = Next(args, ref i);
                            break;
                    }
                }

                return o;
            }

            private static string Next(string[] args, ref int i)
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value after {args[i]}");
                }

                return args[++i];
            }
        }
    }
}
