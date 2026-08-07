using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Reads animation structure out of a WebP file by walking its RIFF container.
    /// </summary>
    /// <remarks>
    /// WIC decodes animated WebP and reports the frame count, but exposes no metadata for it
    /// (<c>BitmapFrame.Metadata</c> is null), so per-frame durations are unreachable through the
    /// metadata queries an animated GIF uses. They are read here instead, straight from the
    /// <c>ANMF</c> chunk headers.
    /// </remarks>
    internal static class WebpAnimationInfo
    {
        // A frame this short is treated as unset and replaced by the default. Matches the floor the
        // GIF path applies, so both formats share one effective minimum.
        private const int MinimumFrameDurationMilliseconds = 20;
        private const int DefaultFrameDurationMilliseconds = 100;

        private const int RiffHeaderLength = 12;
        private const int ChunkHeaderLength = 8;
        private const int AnmfHeaderLength = 16;
        private const int AnmfDurationOffset = 12;

        // VP8X flag bits live in the first payload byte; 0x02 marks an animation.
        private const byte AnimationFlag = 0x02;

        /// <summary>
        /// True when the file is a WebP whose header declares an animation. Reads only the leading
        /// chunk header, so it is cheap enough for a per-load check.
        /// </summary>
        internal static bool IsAnimated(string path)
        {
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return IsAnimated(stream);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads each frame's display duration in milliseconds, in file order. Returns false for a
        /// still WebP, a non-WebP, or a file whose container does not parse.
        /// </summary>
        internal static bool TryReadFrameDurations(string path, out List<int> durations)
        {
            durations = null;

            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return TryReadFrameDurations(stream, out durations);
                }
            }
            catch
            {
                durations = null;
                return false;
            }
        }

        private static bool IsAnimated(Stream stream)
        {
            if (!TryReadRiffHeader(stream))
            {
                return false;
            }

            // The spec requires VP8X first when it is present at all, and animation requires VP8X,
            // so a different leading chunk is conclusive: the file is a still image.
            var header = new byte[ChunkHeaderLength];
            if (!TryReadExactly(stream, header, ChunkHeaderLength) ||
                ReadTag(header, 0) != "VP8X")
            {
                return false;
            }

            var flags = new byte[1];
            return TryReadExactly(stream, flags, 1) && (flags[0] & AnimationFlag) != 0;
        }

        private static bool TryReadFrameDurations(Stream stream, out List<int> durations)
        {
            durations = null;

            if (!TryReadRiffHeader(stream))
            {
                return false;
            }

            var animated = false;
            var collected = new List<int>();
            var header = new byte[ChunkHeaderLength];

            while (TryReadExactly(stream, header, ChunkHeaderLength))
            {
                var tag = ReadTag(header, 0);
                var payloadLength = ReadUInt32LittleEndian(header, 4);

                // A length that overruns the file means a truncated or crafted container; stop
                // rather than trusting anything past it.
                if (payloadLength > int.MaxValue)
                {
                    break;
                }

                var remaining = (long)payloadLength;

                if (tag == "VP8X" && remaining >= 1)
                {
                    var flags = new byte[1];
                    if (!TryReadExactly(stream, flags, 1))
                    {
                        break;
                    }

                    animated = (flags[0] & AnimationFlag) != 0;
                    remaining -= 1;
                }
                else if (tag == "ANMF" && remaining >= AnmfHeaderLength)
                {
                    var anmf = new byte[AnmfHeaderLength];
                    if (!TryReadExactly(stream, anmf, AnmfHeaderLength))
                    {
                        break;
                    }

                    collected.Add(NormalizeDuration(ReadUInt24LittleEndian(anmf, AnmfDurationOffset)));
                    remaining -= AnmfHeaderLength;
                }

                // Chunk payloads are padded to an even length, and the pad byte is not counted in
                // the size field.
                if ((payloadLength & 1) != 0)
                {
                    remaining += 1;
                }

                if (remaining > 0 && !TrySkip(stream, remaining))
                {
                    break;
                }
            }

            if (!animated || collected.Count == 0)
            {
                return false;
            }

            durations = collected;
            return true;
        }

        /// <summary>
        /// Applies the shared floor. A zero duration is what encoders write when they leave timing
        /// to the viewer, so it becomes the default rather than a zero-length frame.
        /// </summary>
        private static int NormalizeDuration(int duration)
        {
            return duration < MinimumFrameDurationMilliseconds
                ? DefaultFrameDurationMilliseconds
                : duration;
        }

        private static bool TryReadRiffHeader(Stream stream)
        {
            var header = new byte[RiffHeaderLength];
            return TryReadExactly(stream, header, RiffHeaderLength) &&
                   ReadTag(header, 0) == "RIFF" &&
                   ReadTag(header, 8) == "WEBP";
        }

        private static string ReadTag(byte[] buffer, int offset)
        {
            return Encoding.ASCII.GetString(buffer, offset, 4);
        }

        private static uint ReadUInt32LittleEndian(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] |
                          (buffer[offset + 1] << 8) |
                          (buffer[offset + 2] << 16) |
                          (buffer[offset + 3] << 24));
        }

        private static int ReadUInt24LittleEndian(byte[] buffer, int offset)
        {
            return buffer[offset] |
                   (buffer[offset + 1] << 8) |
                   (buffer[offset + 2] << 16);
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);
                if (chunk <= 0)
                {
                    return false;
                }

                read += chunk;
            }

            return true;
        }

        private static bool TrySkip(Stream stream, long count)
        {
            if (count <= 0)
            {
                return true;
            }

            // Seek would silently succeed past the end, which would turn a truncated file into an
            // endless read loop; compare against the known length instead.
            if (stream.CanSeek)
            {
                var target = stream.Position + count;
                if (target > stream.Length)
                {
                    return false;
                }

                stream.Position = target;
                return true;
            }

            var scratch = new byte[Math.Min(count, 8192)];
            while (count > 0)
            {
                var want = (int)Math.Min(count, scratch.Length);
                if (!TryReadExactly(stream, scratch, want))
                {
                    return false;
                }

                count -= want;
            }

            return true;
        }
    }
}
