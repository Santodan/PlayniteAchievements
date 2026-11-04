using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Playnite.SDK.Models;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalSavesProvider
    {
        private sealed class SantodanFolderResolution
        {
            public bool Resolved;
            public string Folder;
            public IReadOnlyList<string> Candidates;
            public bool Overridden;
            public bool Ambiguous;
        }

        // Minimal shared resolver hook; disabled/manual/upstream calls use the original body.
        private bool TryResolveLocalFolder(Game game, string appId, out string folderPath,
            out IReadOnlyList<string> candidateFolders, out bool isOverridden, out bool isAmbiguous)
        {
            if (!SantodanPollingScope.IsActive || game == null ||
                ProviderRegistry.Settings<LocalSettings>()?.ReuseCustomPollingDiscoveryWithinPoll != true)
                return TryResolveLocalFolderUncached(game, appId, out folderPath, out candidateFolders,
                    out isOverridden, out isAmbiguous);

            var key = JsonConvert.SerializeObject(new { game.Id, appId, Configuration = GetSantodanPollingConfiguration(game) });
            var result = SantodanPollingScope.GetOrDiscover(this, key, () =>
            {
                var value = new SantodanFolderResolution();
                value.Resolved = TryResolveLocalFolderUncached(game, appId, out value.Folder,
                    out value.Candidates, out value.Overridden, out value.Ambiguous);
                // Callers must not mutate the stored candidate list.
                value.Candidates = value.Candidates?.ToArray();
                return value;
            });
            folderPath = result.Folder;
            candidateFolders = result.Candidates?.ToArray();
            isOverridden = result.Overridden;
            isAmbiguous = result.Ambiguous;
            return result.Resolved;
        }
    }
}
