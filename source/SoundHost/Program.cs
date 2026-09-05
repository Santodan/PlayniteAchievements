using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using NAudio.MediaFoundation;
using PlayniteAchievements.Services.Sound;

namespace PlayniteAchievements.SoundHost
{
    /// <summary>
    /// Entry point of the unlock-sound host. Reads protocol lines from stdin and exits when stdin
    /// closes (the plugin died or disposed it), on "quit", or when the parent process named by
    /// <c>--parent &lt;pid&gt;</c> is gone, so an orphan never outlives the plugin or blocks an
    /// extension update from replacing the exe.
    /// </summary>
    internal static class Program
    {
        [MTAThread]
        private static int Main(string[] args)
        {
            var utf8 = new UTF8Encoding(false);
            var stdoutGate = new object();
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            Action<string> emit = line =>
            {
                lock (stdoutGate)
                {
                    try
                    {
                        stdout.WriteLine(line);
                    }
                    catch (IOException)
                    {
                    }
                }
            };

            MediaFoundationApi.Startup();
            using (var engine = new SoundEngine(emit))
            using (var stdin = new StreamReader(Console.OpenStandardInput(), utf8))
            {
                emit(SoundHostProtocol.EncodeReady(Process.GetCurrentProcess().Id));
                using (WatchParent(ParseParentPid(args)))
                {
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

        private static int? ParseParentPid(string[] args)
        {
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--parent" && int.TryParse(args[i + 1], out var pid))
                {
                    return pid;
                }
            }

            return null;
        }

        /// <summary>Exits the process if the parent disappears; stdin EOF normally gets there first.</summary>
        private static IDisposable WatchParent(int? parentPid)
        {
            if (parentPid == null)
            {
                return new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
            }

            return new Timer(
                _ =>
                {
                    try
                    {
                        using (var parent = Process.GetProcessById(parentPid.Value))
                        {
                            if (!parent.HasExited)
                            {
                                return;
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                    }

                    Environment.Exit(0);
                },
                null,
                5000,
                5000);
        }
    }
}
