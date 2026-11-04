using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Writes images into shared packages in a format every reader accepts. A stored icon keeps
    /// its source bytes, so a file named .png can hold an icon (.ico), bitmap or TIFF that WPF
    /// shows but the Workshop rejects; those are converted to PNG, everything else is copied.
    /// </summary>
    internal static class PackageImageFormat
    {
        /// <summary>
        /// True when the leading bytes are PNG, JPEG, GIF, WebP or WebM: the formats a package
        /// carries as they are.
        /// </summary>
        internal static bool IsPortable(byte[] head)
        {
            if (head == null)
            {
                return false;
            }

            return StartsWith(head, 0, 0x89, 0x50, 0x4E, 0x47)
                || StartsWith(head, 0, 0xFF, 0xD8, 0xFF)
                || StartsWith(head, 0, 0x47, 0x49, 0x46, 0x38)
                || (StartsWith(head, 0, 0x52, 0x49, 0x46, 0x46) && StartsWith(head, 8, 0x57, 0x45, 0x42, 0x50))
                || StartsWith(head, 0, 0x1A, 0x45, 0xDF, 0xA3);
        }

        /// <summary>
        /// Copies <paramref name="sourcePath"/> into <paramref name="destination"/>, converting it to
        /// PNG when it is not a portable format. A multi-size file (an icon) contributes its largest
        /// image. A file WPF cannot decode is copied as it is, as before, rather than failing the
        /// whole package.
        /// </summary>
        internal static void CopyPortable(string sourcePath, Stream destination)
        {
            var bytes = File.ReadAllBytes(sourcePath);
            var converted = IsPortable(bytes) ? null : TryEncodePng(bytes);
            var output = converted ?? bytes;
            destination.Write(output, 0, output.Length);
        }

        /// <summary>
        /// The extension a package entry for <paramref name="sourcePath"/> carries: its own
        /// <paramref name="extension"/> when the file is already portable, ".png" otherwise, which
        /// is what <see cref="CopyPortable"/> writes for it.
        /// </summary>
        internal static string EntryExtension(string sourcePath, string extension)
        {
            return IsPortableFile(sourcePath) ? extension : ".png";
        }

        /// <summary>True when the file at <paramref name="path"/> starts as a portable format; false when unreadable.</summary>
        internal static bool IsPortableFile(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var head = new byte[16];
                    var read = stream.Read(head, 0, head.Length);
                    Array.Resize(ref head, read);
                    return IsPortable(head);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static byte[] TryEncodePng(byte[] bytes)
        {
            try
            {
                // Encoded in memory: a zip entry stream cannot seek.
                using (var input = new MemoryStream(bytes, writable: false))
                using (var output = new MemoryStream())
                {
                    var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames
                        .OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight)
                        .FirstOrDefault();
                    if (frame == null)
                    {
                        return null;
                    }

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frame));
                    encoder.Save(output);
                    return output.ToArray();
                }
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is FileFormatException || ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }

        private static bool StartsWith(byte[] data, int offset, params byte[] signature)
        {
            if (data.Length < offset + signature.Length)
            {
                return false;
            }

            for (var i = 0; i < signature.Length; i++)
            {
                if (data[offset + i] != signature[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
