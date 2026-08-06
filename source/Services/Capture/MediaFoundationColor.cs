using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Explicit colour signalling for the clip encoders' media types.
    ///
    /// Without these attributes Media Foundation picks its own conversion defaults and the H.264
    /// encoder writes no VUI colour information: the resulting MP4 has no <c>colr</c> box and
    /// <c>video_signal_type_present_flag = 0</c> in the SPS. Every decoder then guesses, and they do
    /// not guess alike — WPF's MediaElement and a standalone player disagree over whether the luma is
    /// full or limited range, so whichever one skips the 16-235 -> 0-255 expansion shows lifted blacks
    /// and the clip reads as noticeably brighter there than everywhere else. Players also pick BT.601
    /// below 720p and BT.709 above it when the matrix is unspecified, so a windowed capture can shift
    /// hue purely because of its frame height.
    ///
    /// Screen capture produces full-range RGB, and the encoded output is tagged limited-range BT.709
    /// (the convention every MP4 player handles correctly). Declaring both ends lets MF's converter
    /// perform the correct 0-255 -> 16-235 compression rather than an assumed one, and the pairing
    /// round-trips: the overlay re-encode decodes a limited-range BT.709 clip back to full-range RGB
    /// and re-tags the output the same way.
    /// </summary>
    internal static class MediaFoundationColor
    {
        /// <summary>
        /// Tags an RGB input type as full range (0-255), which is what desktop and game capture
        /// produce and what MF's decoder/converter hands back for RGB output types.
        /// </summary>
        public static void ApplyFullRangeRgbInput(MediaType inputType)
        {
            if (inputType == null)
            {
                return;
            }

            inputType.Set(MediaTypeAttributeKeys.VideoNominalRange, (int)NominalRange.Range0_255);
        }

        /// <summary>
        /// Tags an encoded video output type as limited-range BT.709 so the written stream carries
        /// unambiguous range, matrix, transfer and primaries.
        /// </summary>
        public static void ApplyBt709LimitedOutput(MediaType outputType)
        {
            if (outputType == null)
            {
                return;
            }

            outputType.Set(MediaTypeAttributeKeys.VideoNominalRange, (int)NominalRange.Range16_235);
            outputType.Set(MediaTypeAttributeKeys.YuvMatrix, (int)VideoTransferMatrix.Bt709);
            outputType.Set(MediaTypeAttributeKeys.TransferFunction, (int)VideoTransferFunction.Func709);
            outputType.Set(MediaTypeAttributeKeys.VideoPrimaries, (int)VideoPrimaries.Bt709);
        }
    }
}
