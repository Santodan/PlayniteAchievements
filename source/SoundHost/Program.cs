using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PlayniteAchievements.SoundHost
{
    /// <summary>
    /// Entry point of the unlock-sound host. Reads commands from stdin, one per line, and exits
    /// when stdin closes (the plugin died or disposed it) or on "quit". Transitional stub: the
    /// engine and protocol land in the following commits.
    /// </summary>
    internal static class Program
    {
        [MTAThread]
        private static int Main(string[] args)
        {
            var utf8 = new UTF8Encoding(false);
            using (var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true })
            using (var stdin = new StreamReader(Console.OpenStandardInput(), utf8))
            {
                stdout.WriteLine("ready\t" + Process.GetCurrentProcess().Id);
                string line;
                while ((line = stdin.ReadLine()) != null)
                {
                    if (line == "quit")
                    {
                        break;
                    }
                }
            }

            return 0;
        }
    }
}
