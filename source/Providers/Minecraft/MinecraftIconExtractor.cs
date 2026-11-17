using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// Resolves an advancement's display item to its item or block texture, through the item's
    /// model when it has one and by name otherwise, and writes it to the plugin's icon cache,
    /// cropped to the first animation frame and scaled up with nearest neighbour so the pixel art
    /// stays sharp.
    /// </summary>
    internal sealed class MinecraftIconExtractor
    {
        public const int IconSize = 64;

        private static readonly string[] TextureFolders = { "item", "block", "items", "blocks" };

        // Items whose texture is a variant, and blocks whose plain name has no texture.
        private static readonly string[] TextureSuffixes = { string.Empty, "_00", "_standby", "_side", "_front", "_top" };

        private readonly string _pluginUserDataPath;
        private string _resolvedForFolder;
        private readonly ILogger _logger;
        private readonly Dictionary<IReadOnlyList<MinecraftResourceSource>, Dictionary<string, string>> _resolved =
            new Dictionary<IReadOnlyList<MinecraftResourceSource>, Dictionary<string, string>>();

        public MinecraftIconExtractor(string pluginUserDataPath, ILogger logger)
        {
            _pluginUserDataPath = pluginUserDataPath;
            _logger = logger;
        }

        // Read on every lookup, so a move of the icon cache takes effect at once.
        private string CacheFolder => string.IsNullOrWhiteSpace(_pluginUserDataPath)
            ? null
            : Path.Combine(IconCacheLocation.CurrentRootOrDefault(_pluginUserDataPath), "minecraft");

        /// <summary>
        /// The cached icon file for a namespaced item id, or null when no source has its texture.
        /// <paramref name="sourcesByPriority"/> is ascending; the last source with the texture wins.
        /// </summary>
        public string GetIconPath(string itemId, float? customModelData, IReadOnlyList<MinecraftResourceSource> sourcesByPriority)
        {
            var cacheFolder = CacheFolder;
            if (cacheFolder == null || string.IsNullOrWhiteSpace(itemId) || sourcesByPriority == null)
            {
                return null;
            }

            // Paths resolved before the icon cache moved name the old folder.
            if (!string.Equals(_resolvedForFolder, cacheFolder, StringComparison.OrdinalIgnoreCase))
            {
                _resolved.Clear();
                _resolvedForFolder = cacheFolder;
            }

            // Lookups are per source list: each instance has its own game jar and mods.
            if (!_resolved.TryGetValue(sourcesByPriority, out var resolved))
            {
                resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _resolved[sourcesByPriority] = resolved;
            }

            var key = customModelData.HasValue ? $"{itemId}#{customModelData.Value}" : itemId;
            if (resolved.TryGetValue(key, out var known))
            {
                return known;
            }

            var path = Resolve(itemId, customModelData, sourcesByPriority);
            resolved[key] = path;
            return path;
        }

        private string Resolve(string itemId, float? customModelData, IReadOnlyList<MinecraftResourceSource> sources)
        {
            var separator = itemId.IndexOf(':');
            var ns = separator > 0 ? itemId.Substring(0, separator) : "minecraft";
            var name = separator > 0 ? itemId.Substring(separator + 1) : itemId;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
            {
                return null;
            }

            var rendered = TryRenderBlock(ns, name, customModelData, sources);
            if (rendered != null)
            {
                return rendered;
            }

            var entries = MinecraftModelResolver.ResolveTextureEntries(ns, name, customModelData, sources)
                .Concat(CandidateEntries(ns, name))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                for (var i = sources.Count - 1; i >= 0; i--)
                {
                    var source = sources[i];
                    if (!source.Textures.Contains(entry))
                    {
                        continue;
                    }

                    // Cached by texture, so two items drawn with the same texture share one file.
                    var relative = entry.Substring("assets/".Length).Replace('/', Path.DirectorySeparatorChar);
                    var target = Path.Combine(CacheFolder, relative);
                    if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= source.StampUtc)
                    {
                        return target;
                    }

                    var bytes = source.ReadBytes(entry);
                    if (bytes != null && TryWriteIcon(bytes, target))
                    {
                        return target;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The item drawn as an inventory block (see <see cref="MinecraftBlockRenderer"/>) when its
        /// model has element boxes, cached by model id; null for flat items or when no face could be
        /// textured, so the flat lookup runs instead.
        /// </summary>
        private string TryRenderBlock(string ns, string name, float? customModelData, IReadOnlyList<MinecraftResourceSource> sources)
        {
            var model = MinecraftModelResolver.ResolveBlockModel(ns, name, customModelData, sources);
            if (model?.ModelId == null)
            {
                return null;
            }

            var relative = model.ModelId.Replace(':', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar) + ".png";
            var target = Path.Combine(CacheFolder, "rendered", relative);
            var newestSource = sources.Count == 0 ? DateTime.MinValue : sources.Max(source => source.StampUtc);
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= newestSource)
            {
                return target;
            }

            var decoded = new Dictionary<string, MinecraftTexture>(StringComparer.OrdinalIgnoreCase);
            (MinecraftTexture, bool)? Load(MinecraftBlockModelPart part, string reference)
            {
                var entry = MinecraftModelResolver.ResolveFaceTexture(part, reference);
                if (entry == null)
                {
                    return null;
                }

                if (!decoded.TryGetValue(entry, out var texture))
                {
                    var source = sources.LastOrDefault(candidate => candidate.Textures.Contains(entry));
                    var bytes = source?.ReadBytes(entry);
                    texture = bytes == null ? null : DecodeFirstFrame(bytes);
                    decoded[entry] = texture;
                }

                var foliage = entry.Contains("leaves") || entry.Contains("vine") || entry.Contains("lily_pad");
                return texture == null ? ((MinecraftTexture, bool)?)null : (texture, foliage);
            }

            var pixels = MinecraftBlockRenderer.Render(
                model.Parts
                    .Select(part => new MinecraftRenderPart
                    {
                        Elements = part.Elements,
                        Transformation = part.Transformation,
                        ResolveTexture = reference => Load(part, reference)
                    })
                    .ToList(),
                model.RotationX,
                model.RotationY,
                IconSize);
            if (pixels == null)
            {
                return null;
            }

            try
            {
                var bitmap = BitmapSource.Create(IconSize, IconSize, 96, 96, PixelFormats.Bgra32, null, pixels, IconSize * 4);
                bitmap.Freeze();
                return TryWritePng(bitmap, target) ? target : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                _logger?.Debug(ex, $"[Minecraft] Could not render block icon '{model.ModelId}'.");
                return null;
            }
        }

        /// <summary>Texture names guessed from the item id, for items without a resolvable model.</summary>
        private static IEnumerable<string> CandidateEntries(string ns, string name)
        {
            foreach (var suffix in TextureSuffixes)
            {
                foreach (var folder in TextureFolders)
                {
                    yield return $"assets/{ns}/textures/{folder}/{name}{suffix}.png".ToLowerInvariant();
                }
            }
        }

        private bool TryWriteIcon(byte[] png, string target)
        {
            BitmapSource scaled;
            try
            {
                scaled = ScaleNearest(png, IconSize);
            }
            catch (Exception ex) when (ex is IOException || ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException)
            {
                _logger?.Debug(ex, $"[Minecraft] Could not decode texture for '{target}'.");
                return false;
            }

            return scaled != null && TryWritePng(scaled, target);
        }

        private bool TryWritePng(BitmapSource image, string target)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var temp = target + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(stream);
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(temp, target);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException)
            {
                _logger?.Debug(ex, $"[Minecraft] Could not write icon '{target}'.");
                return false;
            }
        }

        /// <summary>
        /// Decodes a texture, keeps the top square (animated textures stack frames vertically) and
        /// scales it to <paramref name="size"/> pixels with nearest neighbour.
        /// </summary>
        internal static BitmapSource ScaleNearest(byte[] png, int size)
        {
            var frame = DecodeFirstFrame(png);
            if (frame == null)
            {
                return null;
            }

            var width = frame.Width;
            var height = frame.Height;
            var sourceStride = width * 4;
            var sourcePixels = frame.Pixels;
            var targetStride = size * 4;
            var targetPixels = new byte[targetStride * size];
            for (var y = 0; y < size; y++)
            {
                var sourceY = y * height / size;
                for (var x = 0; x < size; x++)
                {
                    var sourceX = x * width / size;
                    Buffer.BlockCopy(sourcePixels, sourceY * sourceStride + sourceX * 4, targetPixels, y * targetStride + x * 4, 4);
                }
            }

            var result = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, targetPixels, targetStride);
            result.Freeze();
            return result;
        }

        /// <summary>
        /// A texture's top square as BGRA pixels (animated textures stack frames vertically); null
        /// when the bytes are not a readable image.
        /// </summary>
        internal static MinecraftTexture DecodeFirstFrame(byte[] png)
        {
            try
            {
                BitmapSource decoded;
                using (var input = new MemoryStream(png))
                {
                    var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    decoded = decoder.Frames.FirstOrDefault();
                }

                if (decoded == null || decoded.PixelWidth <= 0 || decoded.PixelHeight <= 0)
                {
                    return null;
                }

                var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
                var width = converted.PixelWidth;
                var height = Math.Min(converted.PixelHeight, width);
                var pixels = new byte[width * 4 * height];
                converted.CopyPixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
                return new MinecraftTexture { Pixels = pixels, Width = width, Height = height };
            }
            catch (Exception ex) when (ex is IOException || ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException || ex is FileFormatException)
            {
                return null;
            }
        }
    }
}
