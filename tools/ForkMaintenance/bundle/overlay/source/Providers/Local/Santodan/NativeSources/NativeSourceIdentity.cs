using System.Security.Cryptography;
using System.Text;

namespace PlayniteAchievements.Providers.Local
{
    internal static class NativeSourceIdentity
    {
        internal static string Key(string source, string save, string schema, string collection)
        {
            using (var hash = SHA256.Create())
            {
                var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes((source + "\n" + save + "\n" + schema + "\n" + collection).ToLowerInvariant()));
                return "native:" + source + ":" + System.BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }
        // Cached-unlock protection applies within one native save selection. A successful switch
        // of profile, source or trophy set must not carry another selection's unlocks forward.
        internal static bool IsNewSelection(string incomingProvider, string incomingKey, string previousKey) =>
            incomingProvider == "Local" && incomingKey?.StartsWith("native:", System.StringComparison.Ordinal) == true && incomingKey != previousKey;
    }
}
