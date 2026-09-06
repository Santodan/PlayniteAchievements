using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Build
{
    /// <summary>
    /// Guards the build wiring facts that would otherwise fail silently until Playnite loads the
    /// plugin: the helper's sources must not compile into the plugin, its exe must be copied into
    /// the plugin's output so Toolbox packs it, and the plugin must launch it under the right role.
    /// </summary>
    [TestClass]
    public class HelperPackagingTests
    {
        [TestMethod]
        public void PluginProject_ExcludesHelperSourcesFromItsCompileGlob()
        {
            var csproj = File.ReadAllText(FindRepoFile("source", "PlayniteAchievements.csproj"));
            var compile = csproj.IndexOf("<Compile Include=\"**\\*.cs\"", StringComparison.Ordinal);
            Assert.IsTrue(compile >= 0, "The plugin compiles its sources through a glob.");

            var lineEnd = csproj.IndexOf("/>", compile, StringComparison.Ordinal);
            var glob = csproj.Substring(compile, lineEnd - compile);
            StringAssert.Contains(glob, "Helper\\**");
        }

        [TestMethod]
        public void PluginProject_CopiesTheHelperExeWithoutReferencingIt()
        {
            var csproj = File.ReadAllText(FindRepoFile("source", "PlayniteAchievements.csproj"));
            var reference = csproj.IndexOf(
                "<ProjectReference Include=\"Helper\\PlayniteAchievements.Helper.csproj\">",
                StringComparison.Ordinal);
            Assert.IsTrue(reference >= 0, "The plugin must build the helper through a ProjectReference.");

            var end = csproj.IndexOf("</ProjectReference>", reference, StringComparison.Ordinal);
            var block = csproj.Substring(reference, end - reference);
            StringAssert.Contains(block, "<ReferenceOutputAssembly>false</ReferenceOutputAssembly>");

            var content = csproj.IndexOf(
                "<Content Include=\"Helper\\bin\\$(Configuration)\\PlayniteAchievementsHelper.exe\">",
                StringComparison.Ordinal);
            Assert.IsTrue(content >= 0, "The helper exe ships as loose content beside the plugin dll.");
            var contentEnd = csproj.IndexOf("</Content>", content, StringComparison.Ordinal);
            var contentBlock = csproj.Substring(content, contentEnd - content);
            StringAssert.Contains(contentBlock, "<Link>PlayniteAchievementsHelper.exe</Link>");
            StringAssert.Contains(contentBlock, "<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
        }

        [TestMethod]
        public void HelperProject_IsAStandaloneExeThatDependsOnlyOnNAudio()
        {
            var csproj = File.ReadAllText(FindRepoFile("source", "Helper", "PlayniteAchievements.Helper.csproj"));
            StringAssert.Contains(csproj, "<OutputType>WinExe</OutputType>");
            StringAssert.Contains(csproj, "<AssemblyName>PlayniteAchievementsHelper</AssemblyName>");
            StringAssert.Contains(csproj, "<TargetFrameworkVersion>v4.6.2</TargetFrameworkVersion>");
            StringAssert.Contains(csproj, "NAudio.1.10.0");
            // Toolbox strips Playnite-provided assemblies from the .pext; the exe cannot resolve them.
            Assert.IsFalse(csproj.Contains("<Reference Include=\"Newtonsoft"), "The helper must not depend on Newtonsoft.Json.");
            Assert.IsFalse(csproj.Contains("<Reference Include=\"System.ValueTuple"), "The helper must not depend on System.ValueTuple.");
            Assert.IsFalse(csproj.Contains("<PackageReference"), "The helper takes no NuGet packages of its own.");
        }

        [TestMethod]
        public void Plugin_LaunchesTheHelperUnderTheSoundRole()
        {
            // The exe is general; the first argument selects the role, and the plugin's sound host
            // must ask for the sound one by the shared constant rather than a literal.
            var protocol = File.ReadAllText(FindRepoFile("source", "Services", "Sound", "SoundHostProtocol.cs"));
            StringAssert.Contains(protocol, "public const string ExecutableName = \"PlayniteAchievementsHelper.exe\";");
            StringAssert.Contains(protocol, "public const string RoleArgument = \"sound\";");

            var host = File.ReadAllText(FindRepoFile("source", "Services", "Sound", "UnlockSoundHost.cs"));
            StringAssert.Contains(host, "SoundHostProtocol.RoleArgument + \" --parent \"");

            var program = File.ReadAllText(FindRepoFile("source", "Helper", "Program.cs"));
            StringAssert.Contains(program, "case SoundRole.Name:");
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
