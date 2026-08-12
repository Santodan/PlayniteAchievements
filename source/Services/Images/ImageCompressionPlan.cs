using System;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// What the compression sweep should do with one cached file.
    /// </summary>
    internal enum ImageCompressionAction
    {
        /// <summary>Re-encode the file smaller, keeping its current path and format.</summary>
        Compress = 0,

        /// <summary>Already at or below the cap; leave the bytes untouched.</summary>
        SkipUnderCap = 1,

        /// <summary>No encoder that can write the file back in its current format.</summary>
        SkipUnsupportedFormat = 2,

        /// <summary>Re-encoding through WPF would flatten the animation to one frame.</summary>
        SkipAnimated = 3
    }

    /// <summary>
    /// The whole policy for the "compress cached images" maintenance sweep: which files may be
    /// rewritten, what size they become, and how much that is expected to save.
    /// </summary>
    /// <remarks>
    /// Kept free of I/O so the rules are directly testable. The caller supplies the dimensions it
    /// read from the file header; nothing here touches the disk.
    /// <para>
    /// The sweep only ever downscales in place, so a compressed file keeps the exact path and
    /// extension it already had. That is what lets the persisted icon paths in the database stay
    /// valid, and what makes a later refresh treat the file as already cached instead of
    /// downloading the original again.
    /// </para>
    /// </remarks>
    internal static class ImageCompressionPlan
    {
        /// <summary>Max-dimension caps offered to the user, smallest first.</summary>
        internal static readonly int[] SelectableMaxDimensions = { 64, 128, 256, 512 };

        /// <summary>The cap selected when the maintenance section is first shown.</summary>
        internal const int DefaultMaxDimension = 128;

        /// <summary>
        /// JPEG quality used when a JPEG is rewritten. High enough that the downscale, not the
        /// quantization, is what reclaims the space.
        /// </summary>
        internal const int JpegQualityLevel = 90;

        /// <summary>
        /// Decides what to do with one cached file.
        /// </summary>
        /// <param name="pathOrUri">Path of the cached file; only its extension is read.</param>
        /// <param name="pixelWidth">Width from the file header.</param>
        /// <param name="pixelHeight">Height from the file header.</param>
        /// <param name="maxDimension">Cap applied to the longer edge.</param>
        internal static ImageCompressionAction Decide(
            string pathOrUri,
            int pixelWidth,
            int pixelHeight,
            int maxDimension)
        {
            // Order matters: an animated file must be reported as animated even when it is also
            // under the cap, so the summary tells the truth about why it was left alone.
            if (ImageFormats.IsAnimationCandidate(pathOrUri))
            {
                return ImageCompressionAction.SkipAnimated;
            }

            if (!IsRewritableExtension(ImageFormats.GetExtension(pathOrUri)))
            {
                return ImageCompressionAction.SkipUnsupportedFormat;
            }

            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                return ImageCompressionAction.SkipUnsupportedFormat;
            }

            if (maxDimension <= 0 || Math.Max(pixelWidth, pixelHeight) <= maxDimension)
            {
                return ImageCompressionAction.SkipUnderCap;
            }

            return ImageCompressionAction.Compress;
        }

        /// <summary>
        /// True for the formats this machine can both decode and encode, so a file can be rewritten
        /// without its extension changing.
        /// </summary>
        /// <remarks>
        /// WebP is deliberately absent even where the optional OS codec makes it decodable: WPF on
        /// net462 ships no WebP encoder, so a rewritten file would have to change extension, which
        /// would strand the path persisted in the database.
        /// </remarks>
        internal static bool IsRewritableExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            var trimmed = extension.Trim();
            return trimmed.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsJpegExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            var trimmed = extension.Trim();
            return trimmed.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Scales the longer edge down to the cap and the shorter edge with it, so the aspect ratio
        /// survives. Both results are at least 1 pixel.
        /// </summary>
        internal static void ComputeTargetSize(
            int pixelWidth,
            int pixelHeight,
            int maxDimension,
            out int targetWidth,
            out int targetHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0 || maxDimension <= 0)
            {
                targetWidth = Math.Max(1, pixelWidth);
                targetHeight = Math.Max(1, pixelHeight);
                return;
            }

            var longestEdge = Math.Max(pixelWidth, pixelHeight);
            if (longestEdge <= maxDimension)
            {
                targetWidth = pixelWidth;
                targetHeight = pixelHeight;
                return;
            }

            var scale = (double)maxDimension / longestEdge;
            targetWidth = Math.Max(1, (int)Math.Round(pixelWidth * scale));
            targetHeight = Math.Max(1, (int)Math.Round(pixelHeight * scale));
        }

        /// <summary>
        /// Projects the post-compression size of one file from its pixel-area reduction, for the
        /// estimate shown before anything is written.
        /// </summary>
        /// <remarks>
        /// Encoded size tracks pixel count closely enough for a pre-flight figure and costs no
        /// decode. The maintenance summary reports measured bytes once the sweep has actually run,
        /// so this only ever has to be in the right ballpark.
        /// </remarks>
        internal static long EstimateCompressedBytes(
            long currentBytes,
            int pixelWidth,
            int pixelHeight,
            int maxDimension)
        {
            if (currentBytes <= 0)
            {
                return 0;
            }

            ComputeTargetSize(pixelWidth, pixelHeight, maxDimension, out var targetWidth, out var targetHeight);

            var currentArea = (double)pixelWidth * pixelHeight;
            var targetArea = (double)targetWidth * targetHeight;
            if (currentArea <= 0 || targetArea >= currentArea)
            {
                return currentBytes;
            }

            var estimated = (long)Math.Round(currentBytes * (targetArea / currentArea));
            return Math.Max(1, estimated);
        }
    }
}
