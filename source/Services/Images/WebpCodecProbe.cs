using System;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Logging;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Reports whether this machine can decode WebP. WPF decodes through WIC, and WebP support
    /// comes from an optional OS component (the Webp Image Extension) rather than from the
    /// framework, so the answer varies per machine and must be measured rather than assumed.
    /// </summary>
    /// <remarks>
    /// The result gates every surface that offers WebP to the user. It matters because the
    /// notification templates bind a path string directly to <c>Image.Source</c> so animated
    /// images play; that goes through WPF's built-in string-to-ImageSource conversion, which has
    /// no failure path of its own, so an undecodable file throws while the surface renders.
    /// Keeping WebP out of the pickers on machines without the codec is what prevents that.
    /// </remarks>
    internal static class WebpCodecProbe
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(WebpCodecProbe));

        // A 34-byte lossless 1x1 WebP. Embedded rather than written to disk so the probe needs no
        // file system access and cannot be defeated by a missing or unwritable cache directory.
        private const string ProbeImageBase64 = "UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==";

        private static readonly Lazy<bool> ProbeResult =
            new Lazy<bool>(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Forces the reported answer regardless of the real codec state. Exists so the
        /// codec-absent behavior is reachable on a machine that does have the codec installed;
        /// leave null outside tests and diagnostics.
        /// </summary>
        internal static bool? SupportOverride { get; set; }

        /// <summary>
        /// True when WIC decoded the embedded probe image. Evaluated once and cached.
        /// </summary>
        internal static bool IsSupported => SupportOverride ?? ProbeResult.Value;

        private static bool Probe()
        {
            try
            {
                var bytes = Convert.FromBase64String(ProbeImageBase64);

                // BitmapDecoder, not BitmapImage: BitmapImage fails on a WebP StreamSource even
                // when the codec is present, so it would report false on a capable machine.
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    var decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    // OnLoad decodes eagerly, but reading a frame is what proves a decoder was
                    // actually matched rather than the container merely being recognized.
                    var supported = decoder?.Frames != null &&
                                    decoder.Frames.Count > 0 &&
                                    decoder.Frames[0].PixelWidth > 0;

                    if (supported)
                    {
                        Logger?.Info(
                            $"[Webp] Decoding is available via '{decoder.CodecInfo?.FriendlyName}'.");
                    }
                    else
                    {
                        Logger?.Info("[Webp] Decoding is unavailable: the probe image produced no frames.");
                    }

                    return supported;
                }
            }
            catch (Exception ex)
            {
                Logger?.Info(
                    $"[Webp] Decoding is unavailable on this machine ({ex.GetType().Name}); " +
                    "WebP will not be offered as an image format.");
                return false;
            }
        }
    }
}
