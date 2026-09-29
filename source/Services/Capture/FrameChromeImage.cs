namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The screenshot frame's chrome rendered over transparency for one achievement: the theme
    /// frame template alone, with no picture under it, as premultiplied BGRA at the clip's frame
    /// size. Composited over the opening of a framed unlock clip.
    /// </summary>
    internal sealed class FrameChromeImage
    {
        public FrameChromeImage(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }

        /// <summary>Premultiplied BGRA, top-down, <c>Width * 4</c> bytes per row.</summary>
        public byte[] Pixels { get; }

        public int Width { get; }

        public int Height { get; }

        public bool IsValid =>
            Pixels != null && Width > 0 && Height > 0 && Pixels.Length >= Width * Height * 4;
    }
}
