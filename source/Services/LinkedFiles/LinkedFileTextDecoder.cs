using System.Text;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// Text from a linked file's bytes: a byte order mark decides the encoding when present,
    /// otherwise strict UTF-8 with a Windows-1252 fallback for files written by older tools.
    /// </summary>
    internal static class LinkedFileTextDecoder
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
        }
    }
}
