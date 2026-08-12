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

    /// <summary>One file the sweep intends to rewrite, sized and measured during the scan.</summary>
    internal sealed class ImageCompressionCandidate
    {
        internal string Path { get; set; }
        internal long Length { get; set; }
        internal int PixelWidth { get; set; }
        internal int PixelHeight { get; set; }
    }

    /// <summary>What a sweep would do, measured before anything is written.</summary>
    internal sealed class ImageCompressionEstimate
    {
        /// <summary>
        /// The files that qualify, carried forward so the compression pass does not have to walk the
        /// cache and re-read every header a second time.
        /// </summary>
        internal IReadOnlyList<ImageCompressionCandidate> Files { get; set; } =
            new List<ImageCompressionCandidate>();

        internal int Candidates => Files?.Count ?? 0;

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
        /// <remarks>
        /// Sizing a file means opening it, so the scan is I/O bound across many small reads and runs
        /// in parallel. Results are written into a fixed array by index rather than accumulated into
        /// a shared collection, which keeps the walk lock-free and the candidate order stable.
        /// </remarks>
        internal ImageCompressionEstimate Scan(
            ImageCompressionScope scope,
            int maxDimension,
            Action<int, int> reportProgress,
            CancellationToken cancel)
        {
            var files = EnumerateScopeFiles(scope);
            var total = files.Count;
            reportProgress?.Invoke(0, total);

            var scanned = new ImageCompressionCandidate[total];
            var processed = 0;

            // Oversubscribed on purpose: each item is a short blocking read of a few header bytes,
            // so threads spend most of their time waiting on the filesystem rather than on a core.
            // Measured against a 41k-file cache, 16 was the floor; beyond that it regressed.
            var options = new ParallelOptions
            {
                CancellationToken = cancel,
                MaxDegreeOfParallelism = Math.Max(4, Math.Min(16, Environment.ProcessorCount * 2))
            };

            Parallel.For(0, total, options, index =>
            {
                scanned[index] = TryPlanFile(files[index], maxDimension);
                reportProgress?.Invoke(Interlocked.Increment(ref processed), total);
            });

            var candidates = new List<ImageCompressionCandidate>();
            var estimate = new ImageCompressionEstimate();

            foreach (var candidate in scanned)
            {
                if (candidate == null)
                {
                    estimate.Skipped++;
                    continue;
                }

                candidates.Add(candidate);
                estimate.CurrentBytes += candidate.Length;
                estimate.EstimatedBytes += ImageCompressionPlan.EstimateCompressedBytes(
                    candidate.Length,
                    candidate.PixelWidth,
                    candidate.PixelHeight,
                    maxDimension);
            }

            estimate.Files = candidates;
            return estimate;
        }

        /// <summary>
        /// Rewrites every qualifying file smaller. Files that fail are counted and logged; one bad
        /// file never aborts the sweep.
        /// </summary>
        /// <remarks>
        /// Works from the candidate list the scan already produced, so the cache is walked and every
        /// header read exactly once per run rather than twice.
        /// </remarks>
        internal async Task<ImageCompressionResult> CompressAsync(
            IReadOnlyList<ImageCompressionCandidate> candidates,
            int maxDimension,
            Action<int, int> reportProgress,
            CancellationToken cancel)
        {
            var result = new ImageCompressionResult();
            var total = candidates?.Count ?? 0;
            reportProgress?.Invoke(0, total);

            for (var i = 0; i < total; i++)
            {
                if (cancel.IsCancellationRequested)
                {
                    result.Canceled = true;
                    break;
                }

                var candidate = candidates[i];

                try
                {
                    var compressed = EncodeSmaller(
                        candidate.Path,
                        candidate.PixelWidth,
                        candidate.PixelHeight,
                        maxDimension);

                    // Re-encoding can grow a file, most often a small PNG whose original encoder
                    // packed it better than WPF's. Keeping the original in that case is the whole
                    // point of measuring first.
                    if (compressed == null || compressed.Length >= candidate.Length)
                    {
                        result.Skipped++;
                        reportProgress?.Invoke(i + 1, total);
                        continue;
                    }

                    await _imageService
                        .ReplaceCachedImageBytesAsync(candidate.Path, compressed, cancel)
                        .ConfigureAwait(false);

                    result.Compressed++;
                    result.BytesBefore += candidate.Length;
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
                    _logger?.Warn(ex, $"Failed to compress cached image: {candidate.Path}");
                }

                reportProgress?.Invoke(i + 1, total);
            }

            return result;
        }

        /// <summary>
        /// Sizes one file and applies <see cref="ImageCompressionPlan"/>. Returns null when the file
        /// should be left alone.
        /// </summary>
        /// <remarks>
        /// Size and dimensions both come from a single open handle. Asking the filesystem for the
        /// length separately would double the per-file syscalls, which is the dominant cost when the
        /// scope holds tens of thousands of icons.
        /// </remarks>
        private ImageCompressionCandidate TryPlanFile(string path, int maxDimension)
        {
            // Cheap checks that need no file access come first: not opening the files that could
            // never be rewritten is the largest single saving in the scan.
            if (ImageFormats.IsAnimationCandidate(path) ||
                !ImageCompressionPlan.IsRewritableExtension(ImageFormats.GetExtension(path)))
            {
                return null;
            }

            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64,
                    useAsync: false))
                {
                    var length = stream.Length;
                    if (length <= 0)
                    {
                        return null;
                    }

                    if (!ImageHeaderDimensions.TryRead(stream, out var pixelWidth, out var pixelHeight))
                    {
                        return null;
                    }

                    if (ImageCompressionPlan.Decide(path, pixelWidth, pixelHeight, maxDimension) !=
                        ImageCompressionAction.Compress)
                    {
                        return null;
                    }

                    return new ImageCompressionCandidate
                    {
                        Path = path,
                        Length = length,
                        PixelWidth = pixelWidth,
                        PixelHeight = pixelHeight
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to inspect cached image: {path}");
                return null;
            }
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
