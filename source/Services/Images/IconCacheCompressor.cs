using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using PlayniteAchievements.Services.Friends;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// The groups of cached imagery the user can compress independently.
    /// </summary>
    internal enum ImageCompressionScope
    {
        /// <summary>Owned-game achievement icons, the bulk of the cache.</summary>
        AchievementIcons = 0,

        /// <summary>Provider-supplied default category art.</summary>
        CategoryDefaults = 1,

        /// <summary>Friend avatars plus unowned-game covers, icons, and achievement icons.</summary>
        FriendImages = 2,

        /// <summary>User-supplied icon and category overrides.</summary>
        CustomIcons = 3
    }

    /// <summary>What a sweep would do, measured before anything is written.</summary>
    internal sealed class ImageCompressionEstimate
    {
        internal int Candidates { get; set; }
        internal int Skipped { get; set; }

        /// <summary>Size on disk of the candidate files only.</summary>
        internal long CurrentBytes { get; set; }

        /// <summary>Projected size of those same files after compression.</summary>
        internal long EstimatedBytes { get; set; }

        internal long EstimatedSavedBytes => Math.Max(0, CurrentBytes - EstimatedBytes);
    }

    /// <summary>What a sweep actually did, measured from the files it wrote.</summary>
    internal sealed class ImageCompressionResult
    {
        internal int Compressed { get; set; }
        internal int Skipped { get; set; }
        internal int Failed { get; set; }
        internal bool Canceled { get; set; }

        /// <summary>Size of the rewritten files before the sweep.</summary>
        internal long BytesBefore { get; set; }

        /// <summary>Size of those same files afterwards.</summary>
        internal long BytesAfter { get; set; }

        internal long SavedBytes => Math.Max(0, BytesBefore - BytesAfter);
    }

    /// <summary>
    /// Downscales oversized files already in the icon cache, in place.
    /// </summary>
    /// <remarks>
    /// Icons are cached at whatever resolution the provider served, which on a large library runs to
    /// well over a gigabyte even though most icons are small: a minority of high-resolution files
    /// holds the majority of the bytes. This reclaims that space without touching anything else.
    /// <para>
    /// Every rewrite keeps the file's existing path and format, so the icon paths persisted in the
    /// database stay valid and <see cref="DiskImageService.GetOrDownloadIconToPathAsync"/> still
    /// short-circuits on the existing file rather than fetching the original again. The policy for
    /// which files qualify lives in <see cref="ImageCompressionPlan"/>.
    /// </para>
    /// </remarks>
    internal sealed class IconCacheCompressor
    {
        private readonly DiskImageService _imageService;
        private readonly ILogger _logger;

        internal IconCacheCompressor(DiskImageService imageService, ILogger logger)
        {
            _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
            _logger = logger;
        }

        /// <summary>
        /// Measures what a sweep would change, without writing anything.
        /// </summary>
        internal ImageCompressionEstimate Scan(
            ImageCompressionScope scope,
            int maxDimension,
            Action<int, int> reportProgress,
            CancellationToken cancel)
        {
            var estimate = new ImageCompressionEstimate();
            var files = EnumerateScopeFiles(scope);
            var total = files.Count;
            reportProgress?.Invoke(0, total);

            for (var i = 0; i < total; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var file = files[i];

                if (TryPlanFile(file, maxDimension, out var length, out var width, out var height))
                {
                    estimate.Candidates++;
                    estimate.CurrentBytes += length;
                    estimate.EstimatedBytes += ImageCompressionPlan.EstimateCompressedBytes(
                        length,
                        width,
                        height,
                        maxDimension);
                }
                else
                {
                    estimate.Skipped++;
                }

                reportProgress?.Invoke(i + 1, total);
            }

            return estimate;
        }

        /// <summary>
        /// Rewrites every qualifying file smaller. Files that fail are counted and logged; one bad
        /// file never aborts the sweep.
        /// </summary>
        internal async Task<ImageCompressionResult> CompressAsync(
            ImageCompressionScope scope,
            int maxDimension,
            Action<int, int> reportProgress,
            CancellationToken cancel)
        {
            var result = new ImageCompressionResult();
            var files = EnumerateScopeFiles(scope);
            var total = files.Count;
            reportProgress?.Invoke(0, total);

            for (var i = 0; i < total; i++)
            {
                if (cancel.IsCancellationRequested)
                {
                    result.Canceled = true;
                    break;
                }

                var file = files[i];
                if (!TryPlanFile(file, maxDimension, out var originalLength, out var width, out var height))
                {
                    result.Skipped++;
                    reportProgress?.Invoke(i + 1, total);
                    continue;
                }

                try
                {
                    var compressed = EncodeSmaller(file, width, height, maxDimension);

                    // Re-encoding can grow a file, most often a small PNG whose original encoder
                    // packed it better than WPF's. Keeping the original in that case is the whole
                    // point of measuring first.
                    if (compressed == null || compressed.Length >= originalLength)
                    {
                        result.Skipped++;
                        reportProgress?.Invoke(i + 1, total);
                        continue;
                    }

                    await _imageService
                        .ReplaceCachedImageBytesAsync(file, compressed, cancel)
                        .ConfigureAwait(false);

                    result.Compressed++;
                    result.BytesBefore += originalLength;
                    result.BytesAfter += compressed.Length;
                }
                catch (OperationCanceledException)
                {
                    result.Canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    _logger?.Warn(ex, $"Failed to compress cached image: {file}");
                }

                reportProgress?.Invoke(i + 1, total);
            }

            return result;
        }

        /// <summary>
        /// Reads the file header and applies <see cref="ImageCompressionPlan"/>. Returns true only
        /// when the file should be rewritten, along with the numbers needed to do it.
        /// </summary>
        private bool TryPlanFile(
            string path,
            int maxDimension,
            out long length,
            out int pixelWidth,
            out int pixelHeight)
        {
            length = 0;
            pixelWidth = 0;
            pixelHeight = 0;

            // Cheap checks that need no file access come first: on a cache of tens of thousands of
            // files, not opening the ones that could never be rewritten is most of the scan cost.
            if (ImageFormats.IsAnimationCandidate(path) ||
                !ImageCompressionPlan.IsRewritableExtension(ImageFormats.GetExtension(path)))
            {
                return false;
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0)
                {
                    return false;
                }

                length = info.Length;
                if (!TryReadDimensions(path, out pixelWidth, out pixelHeight))
                {
                    return false;
                }

                return ImageCompressionPlan.Decide(path, pixelWidth, pixelHeight, maxDimension) ==
                       ImageCompressionAction.Compress;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to inspect cached image: {path}");
                return false;
            }
        }

        /// <summary>
        /// Reads pixel dimensions from the file header without decoding the image, the same way
        /// <see cref="DiskImageService.EnsureIconSquareAsync"/> does.
        /// </summary>
        private static bool TryReadDimensions(string path, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var frame = BitmapFrame.Create(
                    stream,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);

                pixelWidth = frame.PixelWidth;
                pixelHeight = frame.PixelHeight;
            }

            return pixelWidth > 0 && pixelHeight > 0;
        }

        /// <summary>
        /// Decodes the file at the reduced size and re-encodes it in its own format, returning the
        /// new bytes. Nothing is written to disk here.
        /// </summary>
        private static byte[] EncodeSmaller(string path, int pixelWidth, int pixelHeight, int maxDimension)
        {
            ImageCompressionPlan.ComputeTargetSize(
                pixelWidth,
                pixelHeight,
                maxDimension,
                out var targetWidth,
                out var targetHeight);

            if (targetWidth >= pixelWidth && targetHeight >= pixelHeight)
            {
                return null;
            }

            var originalBytes = File.ReadAllBytes(path);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

            // Constraining only the longer edge lets the decoder derive the other one, which keeps
            // the aspect ratio exact instead of rounding both edges independently.
            if (pixelWidth >= pixelHeight)
            {
                bitmap.DecodePixelWidth = targetWidth;
            }
            else
            {
                bitmap.DecodePixelHeight = targetHeight;
            }

            using (var source = new MemoryStream(originalBytes, writable: false))
            {
                bitmap.StreamSource = source;
                bitmap.EndInit();
            }

            bitmap.Freeze();

            BitmapEncoder encoder;
            if (ImageCompressionPlan.IsJpegExtension(ImageFormats.GetExtension(path)))
            {
                encoder = new JpegBitmapEncoder { QualityLevel = ImageCompressionPlan.JpegQualityLevel };
            }
            else
            {
                encoder = new PngBitmapEncoder();
            }

            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var output = new MemoryStream())
            {
                encoder.Save(output);
                return output.ToArray();
            }
        }

        /// <summary>
        /// Every file belonging to a scope. Scopes are disjoint, so a file is never swept twice by
        /// running each of them.
        /// </summary>
        private List<string> EnumerateScopeFiles(ImageCompressionScope scope)
        {
            var files = new List<string>();
            var cacheRoot = _imageService?.GetCacheDirectoryPath();
            if (string.IsNullOrWhiteSpace(cacheRoot) || !Directory.Exists(cacheRoot))
            {
                return files;
            }

            try
            {
                switch (scope)
                {
                    case ImageCompressionScope.FriendImages:
                        AddFilesUnder(files, Path.Combine(cacheRoot, FriendImageCacheFolders.Avatars));
                        AddFilesUnder(files, Path.Combine(cacheRoot, FriendImageCacheFolders.Games));
                        break;

                    case ImageCompressionScope.AchievementIcons:
                        AddPerGameSubfolder(files, cacheRoot, AchievementIconCachePathBuilder.ModeFolderName);
                        break;

                    case ImageCompressionScope.CategoryDefaults:
                        AddPerGameSubfolder(files, cacheRoot, AchievementIconCachePathBuilder.DefaultCategoryFolderName);
                        break;

                    case ImageCompressionScope.CustomIcons:
                        AddPerGameSubfolder(files, cacheRoot, AchievementIconCachePathBuilder.CustomFolderName);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to enumerate icon cache for scope {scope}.");
            }

            return files;
        }

        /// <summary>
        /// Collects one named subfolder from every per-game folder. The friend folders are
        /// top-level siblings of the per-game folders and carry no such subfolder, so they are
        /// naturally excluded. The retired <c>128</c> compressed-mode folder is skipped for the
        /// same reason: it is not the folder being asked for.
        /// </summary>
        private static void AddPerGameSubfolder(List<string> files, string cacheRoot, string subfolderName)
        {
            foreach (var gameFolder in Directory.EnumerateDirectories(cacheRoot))
            {
                AddFilesUnder(files, Path.Combine(gameFolder, subfolderName));
            }
        }

        private static void AddFilesUnder(List<string> files, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return;
            }

            files.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
        }
    }
}
