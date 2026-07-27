namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Output height of unlock recordings. Native keeps the captured monitor resolution;
    /// the fixed options downscale (never upscale) via an ffmpeg scale filter.
    /// </summary>
    public enum RecordingResolution
    {
        Native,
        P1080,
        P720
    }

    /// <summary>
    /// H.264 encoder used for the rolling capture. Auto prefers hardware encoders
    /// (NVENC &gt; QSV &gt; AMF) when the probed ffmpeg build supports them, else libx264.
    /// </summary>
    public enum RecordingEncoder
    {
        Auto,
        X264,
        Nvenc,
        Qsv,
        Amf
    }

    /// <summary>
    /// ffmpeg screen-capture input. Gdigrab works on every ffmpeg build but makes the visible
    /// mouse cursor flicker while capturing (GDI BitBlt with CAPTUREBLT); Ddagrab (Desktop
    /// Duplication, ffmpeg 5.0+) records the cursor without the flicker. Auto prefers Ddagrab
    /// when the probed build supports it, falling back to Gdigrab.
    /// </summary>
    public enum RecordingCaptureBackend
    {
        Auto,
        Gdigrab,
        Ddagrab
    }
}
