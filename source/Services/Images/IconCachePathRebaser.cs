using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Rewrites stored absolute paths under one icon cache root to the same place under another,
    /// leaving every other value as it is.
    /// </summary>
    internal static class IconCachePathRebaser
    {
        public static string RebasePath(string path, string oldRoot, string newRoot)
        {
            return IconCacheLocation.TryRebase(path, oldRoot, newRoot, out var rebased) ? rebased : path;
        }

        /// <summary>
        /// Rewrites every string value in a JSON document that is a path under
        /// <paramref name="oldRoot"/>. Returns the document unchanged, and false, when none is.
        /// Dates and numbers are read as written, so nothing else in the document changes.
        /// </summary>
        public static bool TryRebaseJson(string json, string oldRoot, string newRoot, out string rebased)
        {
            rebased = json;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            JToken root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json))
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal
                })
                {
                    root = JToken.Load(reader);
                }
            }
            catch (JsonException)
            {
                return false;
            }

            if (!RebaseToken(root, oldRoot, newRoot))
            {
                return false;
            }

            rebased = root.ToString(Formatting.None);
            return true;
        }

        private static bool RebaseToken(JToken token, string oldRoot, string newRoot)
        {
            var changed = false;
            switch (token)
            {
                case JValue value when value.Type == JTokenType.String:
                    var text = (string)value.Value;
                    if (IconCacheLocation.TryRebase(text, oldRoot, newRoot, out var rebased))
                    {
                        value.Value = rebased;
                        changed = true;
                    }

                    break;
                case JContainer container:
                    foreach (var child in container.Children())
                    {
                        changed |= RebaseToken(child, oldRoot, newRoot);
                    }

                    break;
            }

            return changed;
        }
    }
}
