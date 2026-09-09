using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PlayniteAchievements.Helper
{
    /// <summary>
    /// Entry point of the plugin's out-of-process helper. The first argument names the role the
    /// process plays; everything else is role-specific. Today there is one role:
    /// <list type="bullet">
    /// <item><c>sound</c> — renders unlock sounds (<see cref="SoundRole"/>), so the recorder can
    /// exclude this process from clip captures.</item>
    /// </list>
    /// Every role speaks a line protocol over stdin/stdout and exits when stdin closes (the plugin
    /// died or disposed it) or when the parent process named by <c>--parent &lt;pid&gt;</c> is
    /// gone, so an orphan never outlives the plugin or blocks an extension update from replacing
    /// the exe.
    /// </summary>
    internal static class Program
    {
        private const int UsageExitCode = 2;

        [MTAThread]
        private static int Main(string[] args)
        {
            var role = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
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

            using (var stdin = new StreamReader(Console.OpenStandardInput(), utf8))
            using (WatchParent(ParseParentPid(args)))
            {
                switch (role)
                {
                    case SoundRole.Name:
                        return SoundRole.Run(emit, stdin);
                    default:
                        Console.Error.WriteLine(
                            "usage: PlayniteAchievementsHelper.exe <role> [--parent <pid>]  roles: " + SoundRole.Name);
                        return UsageExitCode;
                }
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
