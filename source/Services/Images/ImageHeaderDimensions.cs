using System.IO;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Reads pixel dimensions straight out of a PNG or JPEG header.
    /// </summary>
    /// <remarks>
    /// Written for the compression sweep, which has to size tens of thousands of cached files before
    /// it can report an estimate. Going through <c>BitmapFrame.Create</c> spins up a WIC decoder per
    /// file, which is roughly twice the cost of reading the handful of header bytes that actually
    /// carry the dimensions.
    /// <para>
    /// Only the two formats the sweep can rewrite are handled; anything else reports false and the
    /// caller skips the file, which it would have done anyway.
    /// </para>
    /// </remarks>
    internal static class ImageHeaderDimensions
    {
        // PNG: 8-byte signature, then the IHDR chunk whose first two fields are width and height as
        // big-endian 32-bit integers, putting them at a fixed offset.
        private const int PngHeaderLength = 24;
        private const int PngWidthOffset = 16;

        internal static bool TryRead(Stream stream, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            if (stream == null || !stream.CanRead || !stream.CanSeek)
            {
                return false;
            }

            var header = new byte[PngHeaderLength];
            var read = stream.Read(header, 0, header.Length);
            if (read < 4)
            {
                return false;
            }

            if (IsPng(header, read))
            {
                pixelWidth = ReadBigEndianInt32(header, PngWidthOffset);
                pixelHeight = ReadBigEndianInt32(header, PngWidthOffset + 4);
                return pixelWidth > 0 && pixelHeight > 0;
            }

            if (IsJpeg(header))
            {
                return TryReadJpeg(stream, out pixelWidth, out pixelHeight);
            }

            return false;
        }

        private static bool IsPng(byte[] header, int read)
        {
            return read >= PngHeaderLength &&
                   header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
        }

        private static bool IsJpeg(byte[] header)
        {
            return header[0] == 0xFF && header[1] == 0xD8;
        }

        /// <summary>
        /// Walks the JPEG marker chain to the start-of-frame segment, which carries the dimensions.
        /// </summary>
        private static bool TryReadJpeg(Stream stream, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;
            stream.Position = 2;

            var segment = new byte[5];
            while (true)
            {
                var current = stream.ReadByte();
                if (current < 0)
                {
                    return false;
                }

                if (current != 0xFF)
                {
                    continue;
                }

                // Runs of 0xFF are legal padding before the marker code itself.
                int marker;
                do
                {
                    marker = stream.ReadByte();
                    if (marker < 0)
                    {
                        return false;
                    }
                }
                while (marker == 0xFF);

                // Standalone markers carry no length field, so there is nothing to skip past.
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    continue;
                }

                if (stream.Read(segment, 0, 2) < 2)
                {
                    return false;
                }

                var length = (segment[0] << 8) | segment[1];

                if (IsStartOfFrame(marker))
                {
                    // Segment body is precision(1), height(2), width(2).
                    if (stream.Read(segment, 0, 5) < 5)
                    {
                        return false;
                    }

                    pixelHeight = (segment[1] << 8) | segment[2];
                    pixelWidth = (segment[3] << 8) | segment[4];
                    return pixelWidth > 0 && pixelHeight > 0;
                }

                if (length < 2)
                {
                    return false;
                }

                stream.Position += length - 2;
            }
        }

        /// <summary>
        /// True for the SOFn markers that describe frame geometry. 0xC4, 0xC8 and 0xCC sit in the
        /// same numeric range but define Huffman tables, arithmetic coding conditioning, and a JPEG
        /// extension rather than a frame.
        /// </summary>
        private static bool IsStartOfFrame(int marker)
        {
            return marker >= 0xC0 && marker <= 0xCF &&
                   marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
        }

        private static int ReadBigEndianInt32(byte[] buffer, int offset)
        {
            return (buffer[offset] << 24) |
                   (buffer[offset + 1] << 16) |
                   (buffer[offset + 2] << 8) |
                   buffer[offset + 3];
        }
    }
}
