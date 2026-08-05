using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace PlayniteAchievements.Providers.GameJolt
{
    /// <summary>
    /// Encrypted persistence for the GameJolt session cookies harvested during interactive login.
    /// Mirrors the Exophase snapshot store: DPAPI-encrypted JSON keyed to the current Windows SID,
    /// with a decrypted in-memory cache so repeated fetches in one refresh don't re-decrypt the file.
    /// This store is the single reader and writer of the file; Save/Delete invalidate the cache.
    /// </summary>
    internal sealed class GameJoltCookieSnapshotStore
    {
        private const string CookieDomainToken = "gamejolt.com";

        private readonly ILogger _logger;
        private readonly string _snapshotPath;

        private readonly object _cacheLock = new object();
        private List<HttpCookie> _cachedCookies;

        public GameJoltCookieSnapshotStore(string pluginUserDataPath, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                throw new ArgumentException("Plugin user data path is required.", nameof(pluginUserDataPath));
            }

            _logger = logger;
            _snapshotPath = Path.Combine(pluginUserDataPath, "gamejolt", "cookies.json.enc");
        }

        public bool Exists => File.Exists(_snapshotPath);

        public bool Save(IReadOnlyList<HttpCookie> cookies)
        {
            try
            {
                var filteredCookies = FilterGameJoltCookies(cookies);
                if (filteredCookies.Count == 0)
                {
                    return false;
                }

                var directory = Path.GetDirectoryName(_snapshotPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var snapshot = new GameJoltCookieSnapshotFile
                {
                    CreatedUtc = DateTime.UtcNow,
                    Cookies = filteredCookies.Select(ToStoredCookie).ToList()
                };

                var json = JsonConvert.SerializeObject(snapshot);
                Encryption.EncryptToFile(_snapshotPath, json, Encoding.UTF8, GetCurrentUserSid());
                InvalidateCache();
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[GameJoltAuth] Failed to save encrypted GameJolt cookie snapshot.");
                return false;
            }
        }

        public bool TryLoad(out List<HttpCookie> cookies)
        {
            cookies = new List<HttpCookie>();

            lock (_cacheLock)
            {
                if (_cachedCookies != null)
                {
                    cookies = _cachedCookies.Select(CloneCookie).ToList();
                    return cookies.Count > 0;
                }
            }

            try
            {
                if (!File.Exists(_snapshotPath))
                {
                    return false;
                }

                var json = Encryption.DecryptFromFile(_snapshotPath, Encoding.UTF8, GetCurrentUserSid());
                var snapshot = JsonConvert.DeserializeObject<GameJoltCookieSnapshotFile>(json);
                if (snapshot?.Cookies == null || snapshot.Cookies.Count == 0)
                {
                    _logger?.Warn("[GameJoltAuth] Snapshot file exists but contains no cookies - deleting corrupt snapshot");
                    Delete();
                    return false;
                }

                cookies = snapshot.Cookies
                    .Select(ToHttpCookie)
                    .Where(cookie => cookie != null)
                    .ToList();

                if (cookies.Count == 0)
                {
                    _logger?.Warn("[GameJoltAuth] Snapshot had cookies but all failed to convert - deleting corrupt snapshot");
                    Delete();
                    return false;
                }

                lock (_cacheLock)
                {
                    _cachedCookies = cookies.Select(CloneCookie).ToList();
                }

                return cookies.Count > 0;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[GameJoltAuth] Failed to load encrypted GameJolt cookie snapshot - deleting corrupt file");
                Delete();
                cookies = new List<HttpCookie>();
                return false;
            }
        }

        public void Delete()
        {
            InvalidateCache();
            try
            {
                if (File.Exists(_snapshotPath))
                {
                    File.Delete(_snapshotPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[GameJoltAuth] Failed to delete encrypted GameJolt cookie snapshot.");
            }
        }

        private void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedCookies = null;
            }
        }

        public static List<HttpCookie> FilterGameJoltCookies(IEnumerable<HttpCookie> cookies)
        {
            return (cookies ?? Enumerable.Empty<HttpCookie>())
                .Where(cookie =>
                    cookie != null &&
                    !string.IsNullOrWhiteSpace(cookie.Domain) &&
                    cookie.Domain.IndexOf(CookieDomainToken, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(CloneCookie)
                .OrderBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(cookie => cookie.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(cookie => cookie.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static HttpCookie CloneCookie(HttpCookie cookie)
        {
            if (cookie == null)
            {
                return null;
            }

            return new HttpCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                Expires = cookie.Expires,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                SameSite = cookie.SameSite,
                Priority = cookie.Priority
            };
        }

        private static GameJoltStoredCookie ToStoredCookie(HttpCookie cookie)
        {
            return new GameJoltStoredCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                Expires = cookie.Expires,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                SameSite = cookie.SameSite,
                Priority = cookie.Priority
            };
        }

        private static HttpCookie ToHttpCookie(GameJoltStoredCookie cookie)
        {
            if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name))
            {
                return null;
            }

            return new HttpCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                Expires = cookie.Expires,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                SameSite = cookie.SameSite,
                Priority = cookie.Priority
            };
        }

        private static string GetCurrentUserSid()
        {
            var sid = WindowsIdentity.GetCurrent()?.User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
            {
                throw new InvalidOperationException("Unable to resolve current Windows SID for GameJolt cookie encryption.");
            }

            return sid;
        }

        private sealed class GameJoltCookieSnapshotFile
        {
            public DateTime CreatedUtc { get; set; }

            public List<GameJoltStoredCookie> Cookies { get; set; } = new List<GameJoltStoredCookie>();
        }

        private sealed class GameJoltStoredCookie
        {
            public string Name { get; set; }

            public string Value { get; set; }

            public string Domain { get; set; }

            public string Path { get; set; }

            public DateTime? Expires { get; set; }

            public bool Secure { get; set; }

            public bool HttpOnly { get; set; }

            public CookieSameSite SameSite { get; set; }

            public CookiePriority Priority { get; set; }
        }
    }
}
