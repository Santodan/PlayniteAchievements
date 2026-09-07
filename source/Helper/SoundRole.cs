using System;
using System.Diagnostics;
using System.IO;
using NAudio.MediaFoundation;
using PlayniteAchievements.Services.Sound;

namespace PlayniteAchievements.Helper
{
    /// <summary>
    /// The <c>sound</c> role: renders unlock sounds through <see cref="SoundEngine"/>, driven by
    /// <see cref="SoundHostProtocol"/> lines on stdin, until stdin closes or "quit" arrives.
    /// </summary>
    internal static class SoundRole
    {
        public const string Name = SoundHostProtocol.RoleArgument;

        public static int Run(Action<string> emit, TextReader stdin)
        {
            MediaFoundationApi.Startup();
            using (var engine = new SoundEngine(emit))
            {
                emit(SoundHostProtocol.EncodeReady(Process.GetCurrentProcess().Id));
                string line;
                while ((line = stdin.ReadLine()) != null)
                {
                    if (!SoundHostProtocol.TryParse(line, out var message))
                    {
                        continue;
                    }

                    if (message.Verb == SoundHostProtocol.QuitVerb)
                    {
                        break;
                    }

                    Dispatch(engine, message);
                }
            }

            MediaFoundationApi.Shutdown();
            return 0;
        }

        private static void Dispatch(SoundEngine engine, SoundHostMessage message)
        {
            switch (message.Verb)
            {
                case SoundHostProtocol.PreloadVerb:
                    engine.Preload(message.Paths);
                    break;
                case SoundHostProtocol.PlayVerb:
                    engine.Play(message.Id, message.Path, message.Gain);
                    break;
                case SoundHostProtocol.StopVerb:
                    engine.Stop();
                    break;
            }
        }
    }
}
