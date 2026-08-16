using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The recorded animation of one toast card across its on-screen lifetime: time-ordered
    /// samples referencing deduped Deflate-compressed premultiplied-BGRA frames. Recorded live by
    /// the toast pipeline while the wave displays and blitted into that achievement's unlock clip
    /// at export time, re-timed to the clip's unlock anchor — so the clip shows only this card,
    /// with its genuine slide-in, GIF, countdown and slide-out motion, regardless of what the
    /// on-screen wave stacked around it.
    ///
    /// The composited position is synthesized at export rather than replayed from measured screen
    /// geometry: the card sits at the corner a genuine lone toast would occupy (computed from the
    /// stored alignment, gap, and the current frame's own pixel size) plus the slide transform's
    /// recorded per-sample offset. Live window motion — placement corrections, the follow loop,
    /// stacking — therefore cannot reach the clip; only the intended slide motion does.
    /// </summary>
    internal sealed class ToastOverlayTrack
    {
        public Guid CaptureCorrelationId { get; set; }

        /// <summary>Provider key of the unlock, retained for diagnostics.</summary>
        public string ProviderKey { get; set; }

        /// <summary>Achievement name of the unlock, retained for diagnostics.</summary>
        public string AchievementName { get; set; }

        /// <summary>Wall-clock time of the first sample (diagnostics only).</summary>
        public DateTime StartUtc { get; set; }

        /// <summary>Track length: last sample time plus one sample interval.</summary>
        public double DurationSeconds { get; set; }

        /// <summary>Whether the toast corner is on the client rect's right edge.</summary>
        public bool AlignRight { get; set; }

        /// <summary>Whether the toast corner is on the client rect's bottom edge.</summary>
        public bool AlignBottom { get; set; }

        /// <summary>
        /// The corner inset in DIPs (the visible-body gap less the card's glow margin), as the live
        /// placer uses it; scaled by <see cref="MonitorScale"/> in the corner math.
        /// </summary>
        public double GapDip { get; set; }

        /// <summary>The anchor monitor's scale, for turning <see cref="GapDip"/> physical.</summary>
        public double MonitorScale { get; set; }

        /// <summary>
        /// The card's shadow/glow halo as a difference layer: with-effects render minus
        /// effects-stripped render, premultiplied BGRA, captured once per wave at full effect
        /// opacity. The halo's shape is the blur of the card's static alpha silhouette, so it never
        /// changes across ticks — only its opacity animates, recorded per sample as
        /// <see cref="Sample.GlowScale"/> and composited at export as frame + layer × scale. This
        /// is what lets the per-tick capture skip the software blur (the dominant render cost).
        /// Null when the card carries no effects.
        /// </summary>
        public Frame ShadowLayer { get; set; }

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

            /// <summary>
            /// The slide transform's value at this tick, physical pixels, sub-pixel precision.
            /// Zero while the card rests; the slide-in and slide-out are the only nonzero spans.
            /// </summary>
            public double SlideXPhys;

            /// <summary>See <see cref="SlideXPhys"/>.</summary>
            public double SlideYPhys;

            /// <summary>
            /// Multiplier for <see cref="ToastOverlayTrack.ShadowLayer"/> at this tick: the glow
            /// effect's animated opacity relative to the opacity the layer was captured at, times
            /// the slide host's opacity. Interpolated at export, so the glow pulse plays at the
            /// clip's full frame rate even when pixel frames repeat.
            /// </summary>
            public double GlowScale;

            /// <summary>Game client width at this tick, physical pixels (scales rects into video frames).</summary>
            public int ClientW;

            /// <summary>Game client height at this tick, physical pixels.</summary>
            public int ClientH;
        }

        /// <summary>
        /// One card image, stored either whole (a keyframe) or as the XOR against the frame before it
        /// in <see cref="Frames"/> (a delta). Deflate round-trips premultiplied BGRA byte-exactly (a PNG
        /// pass through WPF's Bgra32 would unpremultiply and lose low-alpha precision).
        ///
        /// Deltas exist because the countdown bar advances about a pixel per frame, so nearly every
        /// sample is a unique image while almost none of it changed. The XOR of two consecutive card
        /// renders is a field of zeros around the few rows that moved, which Deflate crushes: measured
        /// 27-34x smaller than whole frames for a static card background, and 5x smaller when the
        /// background itself animates. Whole frames would exhaust the recorder's per-track budget partway
        /// through a clip on a full-bleed photographic background, freezing the card.
        ///
        /// A delta always references the immediately preceding entry. That holds because one recorder
        /// item owns one track and only ever appends to it, so its frame indices are sequential.
        /// </summary>
        public sealed class Frame
        {
            public int Width { get; set; }

            public int Height { get; set; }

            /// <summary>
            /// DeflateStream-compressed payload, stride = Width * 4: premultiplied BGRA for a keyframe,
            /// or its XOR against the previous frame's pixels when <see cref="IsDelta"/>.
            /// </summary>
            public byte[] Deflated { get; set; }

            /// <summary>
            /// True when <see cref="Deflated"/> holds an XOR against the previous frame rather than
            /// whole pixels. Reconstruction runs through
            /// <see cref="ToastOverlayTrack.TryReconstructFrame"/>.
            /// </summary>
            public bool IsDelta { get; set; }

            /// <summary>
            /// Compresses a payload: whole premultiplied-BGRA pixels when <paramref name="isDelta"/> is
            /// false, else their XOR against the previous frame (which the caller computes, since it
            /// already holds the previous pixels for the dedup comparison).
            /// </summary>
            public static Frame Compress(byte[] payload, int width, int height, bool isDelta)
            {
                using (var output = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        deflate.Write(payload, 0, payload.Length);
                    }

                    return new Frame
                    {
                        Width = width,
                        Height = height,
                        Deflated = output.ToArray(),
                        IsDelta = isDelta,
                    };
                }
            }

            /// <summary>Compresses whole pixels as a keyframe.</summary>
            public static Frame FromRaw(byte[] raw, int width, int height)
            {
                return Compress(raw, width, height, isDelta: false);
            }

            /// <summary>
            /// Inflates the payload: the raw premultiplied-BGRA pixels for a keyframe, or the XOR mask
            /// for a delta. Returns null when the payload is missing (a compression failure).
            /// </summary>
            public byte[] Inflate()
            {
                if (Deflated == null || Width <= 0 || Height <= 0)
                {
                    return null;
                }

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

            /// <summary>Inflates a keyframe back to raw premultiplied BGRA (stride = Width * 4).</summary>
            public byte[] ToRaw()
            {
                return Inflate();
            }

            /// <summary>
            /// XORs this delta's mask into <paramref name="target"/>, turning the previous frame's pixels
            /// into this frame's. False when the payload is missing or sized for a different card.
            /// </summary>
            public bool TryApplyTo(byte[] target)
            {
                var mask = Inflate();
                if (mask == null || target == null || mask.Length != target.Length)
                {
                    return false;
                }

                for (var i = 0; i < mask.Length; i++)
                {
                    target[i] ^= mask[i];
                }

                return true;
            }
        }

        /// <summary>
        /// Materializes frame <paramref name="frameIndex"/>'s raw premultiplied BGRA into
        /// <paramref name="buffer"/>, advancing from whatever <paramref name="buffer"/> already holds
        /// when that is this frame's base, else replaying from the nearest keyframe at or before it.
        /// <paramref name="bufferIndex"/> tracks which frame <paramref name="buffer"/> currently holds
        /// (-1 for none) and is left at -1 if reconstruction fails partway.
        ///
        /// Export walks samples forward, so the common case is a single inflate-and-XOR onto the frame
        /// already in hand. False means the chain is broken (a frame whose compression failed); the
        /// caller should hold its previous overlay rather than drop the card.
        /// </summary>
        public bool TryReconstructFrame(int frameIndex, ref byte[] buffer, ref int bufferIndex)
        {
            if (frameIndex < 0 || frameIndex >= Frames.Count)
            {
                return false;
            }

            if (buffer != null && bufferIndex == frameIndex)
            {
                return true;
            }

            // Walk back to the frame this replay starts from: the one after the buffer's current
            // contents when that sits directly behind us on the chain, else a keyframe.
            var start = frameIndex;
            while (start > 0 &&
                Frames[start] != null && Frames[start].IsDelta &&
                !(buffer != null && bufferIndex == start - 1))
            {
                start--;
            }

            byte[] current;
            var startFrame = Frames[start];
            if (startFrame == null)
            {
                bufferIndex = -1;
                return false;
            }

            if (startFrame.IsDelta)
            {
                // Resuming from the buffer: copy it so a failure mid-replay cannot corrupt the caller's
                // last good frame.
                current = (byte[])buffer.Clone();
                if (!startFrame.TryApplyTo(current))
                {
                    bufferIndex = -1;
                    return false;
                }
            }
            else
            {
                current = startFrame.ToRaw();
                if (current == null)
                {
                    bufferIndex = -1;
                    return false;
                }
            }

            for (var i = start + 1; i <= frameIndex; i++)
            {
                var frame = Frames[i];
                if (frame == null)
                {
                    bufferIndex = -1;
                    return false;
                }

                if (!frame.IsDelta)
                {
                    var whole = frame.ToRaw();
                    if (whole == null)
                    {
                        bufferIndex = -1;
                        return false;
                    }

                    current = whole;
                    continue;
                }

                if (!frame.TryApplyTo(current))
                {
                    bufferIndex = -1;
                    return false;
                }
            }

            buffer = current;
            bufferIndex = frameIndex;
            return true;
        }

        /// <summary>
        /// Index of the last sample at or before <paramref name="secondsIntoTrack"/>; -1 before the
        /// first sample. Binary search over the time-ordered sample list.
        ///
        /// The query time is rounded to the millisecond the samples are stored in, not floored: a frame
        /// instant of 16.667 ms floored to 16 would miss a sample stored at 17 and resolve to the one
        /// before it, so an output frame rate matching the sample rate would still duplicate one
        /// position (and skip one) per second purely from the two quantizations disagreeing.
        /// </summary>
        public int FindSampleIndexAtOrBefore(double secondsIntoTrack)
        {
            var targetMs = (long)Math.Round(secondsIntoTrack * 1000.0);
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
