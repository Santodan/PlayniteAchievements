using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The recorded animation of one toast card across its on-screen lifetime: time-ordered
    /// samples (card rect relative to the game client rect, physical pixels) referencing deduped
    /// Deflate-compressed premultiplied-BGRA frames. Recorded live by the toast pipeline while the
    /// wave displays and blitted into that achievement's unlock clip at export time, re-timed to
    /// the clip's unlock anchor — so the clip shows only this card, with its genuine slide-in,
    /// GIF, countdown and slide-out motion, regardless of what the on-screen wave stacked around
    /// it. Rects are client-relative because the toast follows the game window on screen while
    /// the game content never moves inside the captured frame; relative coordinates cancel window
    /// motion but keep the slide animation.
    /// </summary>
    internal sealed class ToastOverlayTrack
    {
        /// <summary>Provider key of the unlock, for matching against pending clip requests.</summary>
        public string ProviderKey { get; set; }

        /// <summary>Achievement name of the unlock, for matching against pending clip requests.</summary>
        public string AchievementName { get; set; }

        /// <summary>Wall-clock time of the first sample (diagnostics only).</summary>
        public DateTime StartUtc { get; set; }

        /// <summary>Track length: last sample time plus one sample interval.</summary>
        public double DurationSeconds { get; set; }

        /// <summary>
        /// Constant translation (client-relative physical pixels) that moves the card's settled
        /// on-screen position to the synthetic single-toast corner, so the recorded motion of a
        /// stacked card lands where a genuine lone toast would sit.
        /// </summary>
        public int OffsetX { get; set; }

        /// <summary>See <see cref="OffsetX"/>.</summary>
        public int OffsetY { get; set; }

        /// <summary>Time-ordered samples; consecutive identical pixels share one frame.</summary>
        public List<Sample> Samples { get; } = new List<Sample>();

        /// <summary>Deduped pixel buffers referenced by <see cref="Sample.FrameIndex"/>.</summary>
        public List<Frame> Frames { get; } = new List<Frame>();

        /// <summary>One sampled instant of the card's animation.</summary>
        public struct Sample
        {
            /// <summary>Milliseconds since the first sample.</summary>
            public int ElapsedMs;

            /// <summary>Index into <see cref="Frames"/>; -1 when the frame was dropped (memory cap).</summary>
            public int FrameIndex;

            /// <summary>Card top-left relative to the game client rect, physical pixels.</summary>
            public int RelX;

            /// <summary>See <see cref="RelX"/>.</summary>
            public int RelY;

            /// <summary>Game client width at this tick, physical pixels (scales rects into video frames).</summary>
            public int ClientW;

            /// <summary>Game client height at this tick, physical pixels.</summary>
            public int ClientH;
        }

        /// <summary>
        /// One unique card image. Deflate round-trips premultiplied BGRA byte-exactly (a PNG pass
        /// through WPF's Bgra32 would unpremultiply and lose low-alpha precision).
        /// </summary>
        public sealed class Frame
        {
            public int Width { get; set; }

            public int Height { get; set; }

            /// <summary>DeflateStream-compressed premultiplied BGRA, stride = Width * 4.</summary>
            public byte[] Deflated { get; set; }

            /// <summary>Compresses a raw premultiplied-BGRA buffer.</summary>
            public static Frame FromRaw(byte[] raw, int width, int height)
            {
                using (var output = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        deflate.Write(raw, 0, raw.Length);
                    }

                    return new Frame { Width = width, Height = height, Deflated = output.ToArray() };
                }
            }

            /// <summary>Inflates back to the raw premultiplied-BGRA buffer (stride = Width * 4).</summary>
            public byte[] ToRaw()
            {
                var raw = new byte[Width * Height * 4];
                using (var input = new MemoryStream(Deflated, writable: false))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    var read = 0;
                    while (read < raw.Length)
                    {
                        var n = deflate.Read(raw, read, raw.Length - read);
                        if (n <= 0)
                        {
                            break;
                        }

                        read += n;
                    }
                }

                return raw;
            }
        }

        /// <summary>
        /// Index of the last sample at or before <paramref name="secondsIntoTrack"/>; -1 before the
        /// first sample. Binary search over the time-ordered sample list.
        /// </summary>
        public int FindSampleIndexAtOrBefore(double secondsIntoTrack)
        {
            var targetMs = (long)Math.Floor(secondsIntoTrack * 1000.0);
            if (Samples.Count == 0 || targetMs < Samples[0].ElapsedMs)
            {
                return -1;
            }

            var lo = 0;
            var hi = Samples.Count - 1;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo + 1) >> 1);
                if (Samples[mid].ElapsedMs <= targetMs)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo;
        }
    }
}
