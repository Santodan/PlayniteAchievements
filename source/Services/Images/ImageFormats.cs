using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// The one list of image formats the plugin handles, plus the two questions callers actually
    /// ask about a file: may the user choose it, and does it animate.
    /// </summary>
    /// <remarks>
    /// This replaced four separately maintained copies of the same array that had drifted apart,
    /// so adding a format is a single edit here.
    /// <para>
    /// <see cref="All"/> and <see cref="Selectable"/> are deliberately different. Anything already
    /// on disk must stay recognizable regardless of what this machine can decode, or a cached file
    /// would become invisible to cache probing, size accounting, and clearing. Only the surfaces
    /// that hand a new file to the user consult <see cref="Selectable"/>.
    /// </para>
    /// </remarks>
    internal static class ImageFormats
    {
        private const string WebpExtension = ".webp";
        private const string GifExtension = ".gif";

        /// <summary>
        /// Every extension the plugin recognizes, independent of this machine's codecs. Use for
        /// disk-cache probing, prune globs, and size or clear accounting.
        /// </summary>
        internal static readonly string[] All =
        {
            ".png",
            ".jpg",
            ".jpeg",
            GifExtension,
            ".bmp",
            ".tif",
            ".tiff",
            WebpExtension
        };

        /// <summary>
        /// Extensions this machine can actually decode, so the user is never offered a format that
        /// would fail to render. Use for file pickers, drag-and-drop, and package validation.
        /// </summary>
        internal static IReadOnlyList<string> Selectable =>
            All.Where(IsSelectableExtension).ToList();

        internal static bool IsSupportedExtension(string extension)
        {
            return !string.IsNullOrWhiteSpace(extension) &&
                   All.Contains(extension.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        internal static bool HasSupportedExtension(string pathOrUri)
        {
            return IsSupportedExtension(GetExtension(pathOrUri));
        }

        internal static bool IsSelectableExtension(string extension)
        {
            if (!IsSupportedExtension(extension))
            {
                return false;
            }

            return !IsWebpExtension(extension) || WebpCodecProbe.IsSupported;
        }

        internal static bool HasSelectableExtension(string pathOrUri)
        {
            return IsSelectableExtension(GetExtension(pathOrUri));
        }

        /// <summary>
        /// An "Image Files (...)" filter for a WinForms open dialog, listing only formats this
        /// machine can decode.
        /// </summary>
        internal static string BuildOpenFileDialogFilter(bool includeAllFiles)
        {
            var patterns = string.Join(";", Selectable.Select(extension => "*" + extension));
            var filter = new StringBuilder()
                .Append("Image Files (").Append(patterns).Append(")|").Append(patterns);

            if (includeAllFiles)
            {
                filter.Append("|All Files (*.*)|*.*");
            }

            return filter.ToString();
        }

        /// <summary>
        /// True for the formats that can carry an animation, judged on extension alone. Callers
        /// that must decide before the bytes exist locally (choosing whether to preserve the
        /// original format on download) need this rather than <see cref="IsAnimatedFile"/>:
        /// re-encoding to PNG would flatten an animation that has not been fetched yet.
        /// </summary>
        internal static bool IsAnimationCandidate(string pathOrUri)
        {
            var extension = GetExtension(pathOrUri);
            return IsGifExtension(extension) || IsWebpExtension(extension);
        }

        /// <summary>
        /// True when the file on disk actually holds multiple frames. Used where a still image
        /// should keep its decode-time optimizations and only a real animation needs them
        /// suppressed.
        /// </summary>
        internal static bool IsAnimatedFile(string pathOrUri)
        {
            var extension = GetExtension(pathOrUri);

            // GIF stays extension-only: it is what the animation path has always assumed, and a
            // single-frame GIF already falls back to the still path further down.
            if (IsGifExtension(extension))
            {
                return true;
            }

            if (!IsWebpExtension(extension))
            {
                return false;
            }

            var localPath = TryResolveLocalPath(pathOrUri);
            return !string.IsNullOrWhiteSpace(localPath) && WebpAnimationInfo.IsAnimated(localPath);
        }

        internal static bool IsGifExtension(string extension)
        {
            return string.Equals(extension, GifExtension, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsWebpExtension(string extension)
        {
            return string.Equals(extension, WebpExtension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The extension of a local path or an absolute URI. Mirrors how the image services read
        /// it: a URI's path segment carries the extension, while query strings must not.
        /// </summary>
        internal static string GetExtension(string pathOrUri)
        {
            if (string.IsNullOrWhiteSpace(pathOrUri))
            {
                return string.Empty;
            }

            try
            {
                if (Uri.TryCreate(pathOrUri, UriKind.Absolute, out var uri) && !uri.IsFile)
                {
                    return Path.GetExtension(uri.AbsolutePath) ?? string.Empty;
                }
            }
            catch
            {
            }

            try
            {
                return Path.GetExtension(pathOrUri) ?? string.Empty;
            }
            catch
            {
                // Path.GetExtension throws on invalid path characters, which a malformed source
                // string can contain.
                return string.Empty;
            }
        }

        private static string TryResolveLocalPath(string pathOrUri)
        {
            if (string.IsNullOrWhiteSpace(pathOrUri))
            {
                return null;
            }

            try
            {
                if (Uri.TryCreate(pathOrUri, UriKind.Absolute, out var uri))
                {
                    if (!uri.IsFile)
                    {
                        return null;
                    }

                    return File.Exists(uri.LocalPath) ? uri.LocalPath : null;
                }

                return File.Exists(pathOrUri) ? pathOrUri : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
