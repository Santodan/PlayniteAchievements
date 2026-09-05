using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Build
{
    /// <summary>
    /// Guards the two build wiring facts that would otherwise fail silently until Playnite loads
    /// the plugin: the sound host's sources must not compile into the plugin, and its exe must be
    /// copied into the plugin's output so Toolbox packs it.
    /// </summary>
    [TestClass]
    public class SoundHostPackagingTests
    {
        [TestMethod]
        public void PluginProject_ExcludesSoundHostSourcesFromItsCompileGlob()
        {
            var csproj = File.ReadAllText(FindRepoFile("source", "PlayniteAchievements.csproj"));
            var compile = csproj.IndexOf("<Compile Include=\"**\\*.cs\"", StringComparison.Ordinal);
            Assert.IsTrue(compile >= 0, "The plugin compiles its sources through a glob.");

            var lineEnd = csproj.IndexOf("/>", compile, StringComparison.Ordinal);
            var glob = csproj.Substring(compile, lineEnd - compile);
            StringAssert.Contains(glob, "SoundHost\\**");
        }

        [TestMethod]
        public void PluginProject_CopiesTheSoundHostExeWithoutReferencingIt()
        {
            var csproj = File.ReadAllText(FindRepoFile("source", "PlayniteAchievements.csproj"));
            var reference = csproj.IndexOf(
                "<ProjectReference Include=\"SoundHost\\PlayniteAchievements.SoundHost.csproj\">",
                StringComparison.Ordinal);
            Assert.IsTrue(reference >= 0, "The plugin must build the sound host through a ProjectReference.");

            var end = csproj.IndexOf("</ProjectReference>", reference, StringComparison.Ordinal);
            var block = csproj.Substring(reference, end - reference);
            StringAssert.Contains(block, "<ReferenceOutputAssembly>false</ReferenceOutputAssembly>");

            var content = csproj.IndexOf(
                "<Content Include=\"SoundHost\\bin\\$(Configuration)\\PlayniteAchievements.SoundHost.exe\">",
                StringComparison.Ordinal);
            Assert.IsTrue(content >= 0, "The helper exe ships as loose content beside the plugin dll.");
            var contentEnd = csproj.IndexOf("</Content>", content, StringComparison.Ordinal);
            var contentBlock = csproj.Substring(content, contentEnd - content);
            StringAssert.Contains(contentBlock, "<Link>PlayniteAchievements.SoundHost.exe</Link>");
            StringAssert.Contains(contentBlock, "<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
        }

        [TestMethod]
        public void SoundHostProject_IsAStandaloneExeThatDependsOnlyOnNAudio()
        {
            var csproj = File.ReadAllText(FindRepoFile("source", "SoundHost", "PlayniteAchievements.SoundHost.csproj"));
            StringAssert.Contains(csproj, "<OutputType>WinExe</OutputType>");
            StringAssert.Contains(csproj, "<TargetFrameworkVersion>v4.6.2</TargetFrameworkVersion>");
            StringAssert.Contains(csproj, "NAudio.1.10.0");
            // Toolbox strips Playnite-provided assemblies from the .pext; the exe cannot resolve them.
            Assert.IsFalse(csproj.Contains("<Reference Include=\"Newtonsoft"), "The sound host must not depend on Newtonsoft.Json.");
            Assert.IsFalse(csproj.Contains("<Reference Include=\"System.ValueTuple"), "The sound host must not depend on System.ValueTuple.");
            Assert.IsFalse(csproj.Contains("<PackageReference"), "The sound host takes no NuGet packages of its own.");
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = directory.FullName;
                foreach (var part in parts)
                {
                    path = Path.Combine(path, part);
                }

                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Repository file not found: " + Path.Combine(parts));
            return null;
        }
    }
}
