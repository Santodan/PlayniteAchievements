namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure plane arithmetic for a decoded NV12 surface: how many luma rows it is allocated over,
    /// which is where its chroma plane begins. Media Foundation exposes no direct accessor, so the
    /// answer is derived from <c>IMF2DBuffer::GetContiguousLength</c> and cross-checked against the
    /// buffer's own capacity. Kept free of Media Foundation so it unit-tests directly.
    /// </summary>
    internal static class Nv12LayoutMath
    {
        /// <summary>
        /// The allocated luma row count for a decoded frame, or 0 when no layout this pass can
        /// address fits the reported numbers (the caller then leaves the clip without its card,
        /// rather than writing one with torn colour).
        /// <para>
        /// Implementations disagree on what the contiguous length counts: some pack rows to the
        /// frame width, others keep the surface pitch. Both are tried, and a candidate is accepted
        /// only if it describes a surface the buffer can hold. A candidate whose pitch-by-rows
        /// product accounts for the reported length exactly wins, so the two divisors can never be
        /// ambiguous; that is the padded case, and it is the one the frame-width divisor alone gets
        /// wrong — a 1972-wide frame at pitch 1984 reporting 3237888 bytes is 1088 rows, which
        /// 1972 x 3/2 does not divide at all.
        /// </para>
        /// </summary>
        public static int AlignedHeight(int frameW, int frameH, int pitch, long contiguousLength, long maxLength)
        {
            if (frameW <= 0 || frameH <= 0 || pitch < frameW || contiguousLength <= 0)
            {
                return 0;
            }

            var fromWidth = CandidateHeight(frameW, frameH, pitch, contiguousLength, maxLength);
            var fromPitch = pitch == frameW
                ? 0
                : CandidateHeight(pitch, frameH, pitch, contiguousLength, maxLength);

            if (IsExact(fromPitch, pitch, contiguousLength))
            {
                return fromPitch;
            }

            if (IsExact(fromWidth, pitch, contiguousLength))
            {
                return fromWidth;
            }

            // Neither accounts for the length exactly; the frame-width reading is the historical
            // one and stays the default.
            if (fromWidth > 0 || fromPitch > 0)
            {
                return fromWidth > 0 ? fromWidth : fromPitch;
            }

            // Some decoders count rows at the coded width: the frame width padded up by an amount
            // that varies by decoder, narrower than the pitch. The row count then comes from the
            // allocated surface, and the counted width must land between the frame width and the
            // pitch. A 2020-wide frame at pitch 2048 reporting 3316224 of 3342336 bytes is 1088
            // rows of 2032; a 406-wide one at pitch 448 reporting 119808 of 129024 is 192 of 416.
            return FromAllocatedRows(frameW, frameH, pitch, contiguousLength, maxLength);
        }

        /// <summary>
        /// The row count the allocated surface implies at <paramref name="pitch"/>, accepted only
        /// when it divides evenly, covers the frame, and the reported length divided over it gives
        /// a whole row width between the frame width and the pitch; otherwise 0.
        /// </summary>
        private static int FromAllocatedRows(
            int frameW, int frameH, int pitch, long contiguousLength, long maxLength)
        {
            var pitchUnits = (long)pitch * 3;
            if (maxLength <= 0 || maxLength * 2 % pitchUnits != 0)
            {
                return 0;
            }

            var rows = maxLength * 2 / pitchUnits;
            if (rows < frameH || rows > int.MaxValue)
            {
                return 0;
            }

            var rowUnits = rows * 3;
            if (contiguousLength * 2 % rowUnits != 0)
            {
                return 0;
            }

            var countedWidth = contiguousLength * 2 / rowUnits;
            return countedWidth > frameW && countedWidth < pitch ? (int)rows : 0;
        }

        /// <summary>Whether a candidate's own surface is exactly the length that was reported.</summary>
        private static bool IsExact(int alignedH, int pitch, long contiguousLength)
        {
            return alignedH > 0 && (long)pitch * alignedH * 3 / 2 == contiguousLength;
        }

        /// <summary>
        /// The row count <paramref name="contiguousLength"/> implies when its rows are
        /// <paramref name="rowBytes"/> wide, or 0 when that does not divide evenly, falls short of
        /// the frame, or describes a surface larger than the buffer.
        /// </summary>
        private static int CandidateHeight(
            int rowBytes, int frameH, int pitch, long contiguousLength, long maxLength)
        {
            var planeBytes = (long)rowBytes * 3 / 2;
            if (planeBytes <= 0 || contiguousLength % planeBytes != 0)
            {
                return 0;
            }

            var alignedH = contiguousLength / planeBytes;
            if (alignedH < frameH || alignedH > int.MaxValue)
            {
                return 0;
            }

            return (long)pitch * alignedH * 3 / 2 <= maxLength ? (int)alignedH : 0;
        }
    }
}
