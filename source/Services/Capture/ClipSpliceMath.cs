using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Works out which parts of an exported clip have to be re-encoded to composite the toast, and which
    /// can be stream-copied untouched. Pure arithmetic over keyframe positions, so it unit-tests without
    /// Media Foundation.
    ///
    /// Re-encoding the whole clip makes every frame second-generation at the same bitrate as the capture,
    /// which cannot preserve what it was given — the encoder spends bits describing the first encode's
    /// blocking and ringing as if they were detail. Copying the spans the card never covers keeps them
    /// exactly as captured, at exactly the bitrate the user chose.
    ///
    /// The asymmetry that drives the plan: a copied span must *begin* on a keyframe, because that is
    /// where a decoder can start, but it may end anywhere — dropping the tail of a group costs nothing,
    /// since no earlier frame depends on a later one. A re-encoded span is the reverse: it may begin
    /// anywhere, because it produces its own keyframe, but must end on a keyframe so the copy that
    /// follows has one to start from.
    /// </summary>
    internal static class ClipSpliceMath
    {
        /// <summary>One run of the output, taken either straight from the source or through the encoder.</summary>
        internal struct Span
        {
            /// <summary>Inclusive start on the source clip's timeline, in 100-ns units.</summary>
            public long Start;

            /// <summary>Exclusive end on the source clip's timeline, in 100-ns units.</summary>
            public long End;

            /// <summary>Whether this run has to go through the encoder (it carries the card, or starts mid-group).</summary>
            public bool Reencode;

            public override string ToString()
            {
                return (Reencode ? "reencode[" : "copy[") +
                    (Start / 10_000_000.0).ToString("0.000") + ".." +
                    (End / 10_000_000.0).ToString("0.000") + "]";
            }
        }

        /// <summary>
        /// Plans the output as an ordered, gapless run of spans covering
        /// [<paramref name="clipStart"/>, <paramref name="clipEnd"/>).
        /// </summary>
        /// <param name="keyframes">
        /// Keyframe positions on the source timeline, ascending. The source is expected to start with one.
        /// </param>
        /// <param name="clipStart">Where the finished clip begins — the keyframe lead the export snapped back over.</param>
        /// <param name="clipEnd">Where the finished clip ends.</param>
        /// <param name="toastStart">First instant the card is drawn.</param>
        /// <param name="toastEnd">Last instant the card is drawn.</param>
        /// <returns>
        /// The spans in order, or an empty list when the inputs describe nothing to write. A caller that
        /// gets a single re-encode span covering everything has learned that splicing buys nothing here.
        /// </returns>
        public static List<Span> Plan(
            IReadOnlyList<long> keyframes, long clipStart, long clipEnd, long toastStart, long toastEnd)
        {
            var spans = new List<Span>();
            if (keyframes == null || clipEnd <= clipStart)
            {
                return spans;
            }

            // The runs that must go through the encoder, before merging.
            var forced = new List<Span>();

            // The clip starts mid-group unless its first instant happens to be a keyframe: those frames
            // cannot be copied, because a decoder joining there has nothing to start from.
            if (!IsKeyframe(keyframes, clipStart))
            {
                forced.Add(new Span
                {
                    Start = clipStart,
                    End = Math.Min(clipEnd, NextKeyframeAfter(keyframes, clipStart, clipEnd)),
                    Reencode = true,
                });
            }

            // The card's own span. It may begin exactly where the card does, since the encoder emits a
            // keyframe there, but has to run to a keyframe so the copy after it can start.
            if (toastEnd >= toastStart && toastStart < clipEnd && toastEnd >= clipStart)
            {
                var start = Math.Max(clipStart, toastStart);
                var end = Math.Min(clipEnd, NextKeyframeAfter(keyframes, toastEnd, clipEnd));
                if (end > start)
                {
                    forced.Add(new Span { Start = start, End = end, Reencode = true });
                }
            }

            forced.Sort((a, b) => a.Start.CompareTo(b.Start));

            // Merge runs that touch or overlap, then fill the gaps with copies.
            var cursor = clipStart;
            foreach (var span in Merge(forced))
            {
                if (span.Start > cursor)
                {
                    spans.Add(new Span { Start = cursor, End = span.Start, Reencode = false });
                }

                spans.Add(span);
                cursor = span.End;
            }

            if (cursor < clipEnd)
            {
                spans.Add(new Span { Start = cursor, End = clipEnd, Reencode = false });
            }

            return spans;
        }

        /// <summary>How much of the plan avoids the encoder, as a fraction of the clip. For reporting.</summary>
        public static double CopiedFraction(IReadOnlyList<Span> spans)
        {
            if (spans == null || spans.Count == 0)
            {
                return 0;
            }

            long copied = 0;
            long total = 0;
            foreach (var span in spans)
            {
                var length = Math.Max(0, span.End - span.Start);
                total += length;
                if (!span.Reencode)
                {
                    copied += length;
                }
            }

            return total > 0 ? copied / (double)total : 0;
        }

        private static List<Span> Merge(List<Span> ordered)
        {
            var merged = new List<Span>();
            foreach (var span in ordered)
            {
                if (merged.Count > 0 && span.Start <= merged[merged.Count - 1].End)
                {
                    var last = merged[merged.Count - 1];
                    if (span.End > last.End)
                    {
                        last.End = span.End;
                        merged[merged.Count - 1] = last;
                    }

                    continue;
                }

                merged.Add(span);
            }

            return merged;
        }

        private static bool IsKeyframe(IReadOnlyList<long> keyframes, long time)
        {
            for (var i = 0; i < keyframes.Count; i++)
            {
                if (keyframes[i] == time)
                {
                    return true;
                }

                if (keyframes[i] > time)
                {
                    break;
                }
            }

            return false;
        }

        /// <summary>
        /// The first keyframe strictly after <paramref name="time"/>, or <paramref name="fallback"/> when
        /// none follows — in which case everything from there on has to be re-encoded anyway.
        /// </summary>
        private static long NextKeyframeAfter(IReadOnlyList<long> keyframes, long time, long fallback)
        {
            for (var i = 0; i < keyframes.Count; i++)
            {
                if (keyframes[i] > time)
                {
                    return keyframes[i];
                }
            }

            return fallback;
        }
    }
}
