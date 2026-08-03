namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Output height of unlock recordings. Native keeps the captured game-window resolution;
    /// the fixed options downscale (never upscale) via a GPU scale pass at capture time.
    /// </summary>
    public enum RecordingResolution
    {
        Native,
        P1080,
        P720
    }

    /// <summary>
    /// Which audio is captured into unlock clips. FullSystem records the default render endpoint
    /// (everything you hear); GameOnly records just the game process's audio via per-process
    /// loopback (Windows 10 build 19041+, else it degrades to FullSystem). The microphone is a
    /// separate opt-in mixed on top of either (see RecordingIncludeMicrophone).
    /// </summary>
    public enum RecordingAudioSource
    {
        FullSystem,
        GameOnly
    }
}
