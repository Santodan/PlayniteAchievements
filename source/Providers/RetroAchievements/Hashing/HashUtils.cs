using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal static class HashUtils
    {
        public const int MaxHashBytes = 64 * 1024 * 1024;

        // FileStream's own buffer; reads of ReadChunkSize or more bypass it.
        public const int FileBufferSize = 64 * 1024;
        private const int ReadChunkSize = 256 * 1024;

        private static readonly byte[] NewlineByte = { (byte)'\n' };

        /// <summary>
        /// True for failures caused by the file being unavailable right now (locked by another
        /// process, access denied, network share dropped) rather than by its contents. Content
        /// failures are deterministic for unchanged bytes and can be recorded as a miss; these
        /// cannot. A plain IOException is not transient: DiscUtils raises one for images that
        /// are not ISO-9660.
        /// </summary>
        public static bool IsTransientReadFailure(Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return true;
            }

            if (ex is IOException)
            {
                switch (ex.HResult & 0xFFFF)
                {
                    case 32:   // ERROR_SHARING_VIOLATION
                    case 33:   // ERROR_LOCK_VIOLATION
                    case 53:   // ERROR_BAD_NETPATH
                    case 55:   // ERROR_DEV_NOT_EXIST
                    case 64:   // ERROR_NETNAME_DELETED
                    case 67:   // ERROR_BAD_NET_NAME
                    case 121:  // ERROR_SEM_TIMEOUT
                    case 1231: // ERROR_NETWORK_UNREACHABLE
                        return true;
                }
            }

            return ex?.InnerException != null && IsTransientReadFailure(ex.InnerException);
        }

        public static FileStream OpenSequential(string filePath)
        {
            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileBufferSize, FileOptions.SequentialScan);
        }

        /// <summary>Advances past <paramref name="count"/> bytes, reading them when the stream cannot seek.</summary>
        public static void Skip(Stream stream, long count)
        {
            if (count <= 0)
            {
                return;
            }

            if (stream.CanSeek)
            {
                stream.Seek(count, SeekOrigin.Current);
                return;
            }

            var buffer = new byte[(int)Math.Min(count, ReadChunkSize)];
            while (count > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0)
                {
                    return;
                }

                count -= read;
            }
        }

        /// <summary>Reads until <paramref name="count"/> bytes arrive or the stream ends.</summary>
        public static int ReadFull(Stream stream, byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        public static string ToHexLower(byte[] hashBytes)
        {
            if (hashBytes == null || hashBytes.Length == 0)
            {
                return string.Empty;
            }

            var chars = new char[hashBytes.Length * 2];
            var idx = 0;
            for (var i = 0; i < hashBytes.Length; i++)
            {
                var b = hashBytes[i];
                chars[idx++] = GetHexValue(b / 16);
                chars[idx++] = GetHexValue(b % 16);
            }
            return new string(chars);
        }

        private static char GetHexValue(int i) => (char)(i < 10 ? i + '0' : i - 10 + 'a');

        public static string ComputeMd5Hex(byte[] buffer, int offset, int count)
        {
            using (var md5 = MD5.Create())
            {
                md5.TransformFinalBlock(buffer, offset, count);
                return ToHexLower(md5.Hash);
            }
        }

        public static string ComputeMd5Hex(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            return ComputeMd5Hex(buffer, 0, buffer.Length);
        }

        public static string ComputeMd5HexFromString(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var bytes = Encoding.UTF8.GetBytes(text);
            return ComputeMd5Hex(bytes);
        }

        public static async Task<string> ComputeMd5HexFromFileAsync(
            string filePath,
            long startOffset,
            long maxBytes,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path is required.", nameof(filePath));
            }

            using (var stream = OpenSequential(filePath))
            {
                if (startOffset > 0)
                {
                    stream.Seek(startOffset, SeekOrigin.Begin);
                }

                return await ComputeMd5HexFromStreamAsync(stream, maxBytes, cancel).ConfigureAwait(false);
            }
        }

        public static async Task<string> ComputeMd5HexFromStreamAsync(
            Stream stream,
            long maxBytes,
            CancellationToken cancel)
        {
            return await ComputeMd5HexFromStreamAsync(stream, maxBytes, cancel, transform: null).ConfigureAwait(false);
        }

        /// <summary>
        /// MD5 of up to <paramref name="maxBytes"/> (capped at <see cref="MaxHashBytes"/>) from the
        /// stream's current position. Reads run synchronously in whole chunks on one thread-pool
        /// hop, so <paramref name="transform"/> always sees full chunks except at the end.
        /// </summary>
        public static Task<string> ComputeMd5HexFromStreamAsync(
            Stream stream,
            long maxBytes,
            CancellationToken cancel,
            Action<byte[], int> transform)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            return Task.Run(() =>
            {
                using (var md5 = MD5.Create())
                {
                    AppendStream(md5, stream, maxBytes, cancel, transform);
                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return ToHexLower(md5.Hash);
                }
            }, cancel);
        }

        private static void AppendStream(HashAlgorithm hash, Stream stream, long maxBytes, CancellationToken cancel, Action<byte[], int> transform)
        {
            var remaining = Math.Min(maxBytes, MaxHashBytes);
            var buffer = new byte[(int)Math.Max(1, Math.Min(ReadChunkSize, remaining))];

            while (remaining > 0)
            {
                cancel.ThrowIfCancellationRequested();

                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = ReadFull(stream, buffer, 0, toRead);
                if (read <= 0)
                {
                    break;
                }

                transform?.Invoke(buffer, read);

                hash.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;

                if (read < toRead)
                {
                    break;
                }
            }
        }

        public static async Task<byte[]> ReadFilePrefixAsync(string filePath, int maxBytes, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path is required.", nameof(filePath));
            }

            maxBytes = Math.Min(maxBytes, MaxHashBytes);
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var length = (int)Math.Min(stream.Length, maxBytes);
                var buffer = new byte[length];
                var read = 0;
                while (read < length)
                {
                    cancel.ThrowIfCancellationRequested();
                    var n = await stream.ReadAsync(buffer, read, length - read, cancel).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }
                    read += n;
                }

                if (read == buffer.Length)
                {
                    return buffer;
                }

                var trimmed = new byte[read];
                Buffer.BlockCopy(buffer, 0, trimmed, 0, read);
                return trimmed;
            }
        }

        public static void ByteSwap16(byte[] buffer, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            for (var i = 0; i + 1 < count; i += 2)
            {
                var tmp = buffer[i];
                buffer[i] = buffer[i + 1];
                buffer[i + 1] = tmp;
            }
        }

        public static void ByteSwap32(byte[] buffer, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            for (var i = 0; i + 3 < count; i += 4)
            {
                var a = buffer[i];
                var b = buffer[i + 1];
                buffer[i] = buffer[i + 3];
                buffer[i + 1] = buffer[i + 2];
                buffer[i + 2] = b;
                buffer[i + 3] = a;
            }
        }

        public static async Task<string> ComputeMd5HexForNormalizedTextAsync(string filePath, CancellationToken cancel)
        {
            var data = await ReadFilePrefixAsync(filePath, MaxHashBytes, cancel).ConfigureAwait(false);
            return ComputeMd5HexForNormalizedText(data);
        }

        public static Task<string> ComputeMd5HexForNormalizedTextAsync(Stream stream, CancellationToken cancel)
        {
            return Task.Run(() =>
            {
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[ReadChunkSize];
                    int read;
                    while (buffer.Length < MaxHashBytes &&
                           (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxHashBytes - buffer.Length))) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        buffer.Write(chunk, 0, read);
                    }

                    return ComputeMd5HexForNormalizedText(buffer.ToArray());
                }
            }, cancel);
        }

        private static string ComputeMd5HexForNormalizedText(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                var idx = 0;
                do
                {
                    var lineStart = idx;
                    while (idx < data.Length && data[idx] != '\r' && data[idx] != '\n')
                    {
                        idx++;
                    }

                    var lineLen = idx - lineStart;
                    if (lineLen > 0)
                    {
                        md5.TransformBlock(data, lineStart, lineLen, null, 0);
                    }

                    md5.TransformBlock(NewlineByte, 0, 1, null, 0);

                    if (idx < data.Length && data[idx] == '\r') idx++;
                    if (idx < data.Length && data[idx] == '\n') idx++;

                } while (idx < data.Length);

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHexLower(md5.Hash);
            }
        }

        public static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancel)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (count == 0) return 0;

            var totalRead = 0;
            while (totalRead < count)
            {
                cancel.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancel).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }

            return totalRead;
        }

        public static uint ReadUInt32LE(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                          | (buffer[offset + 1] << 8)
                          | (buffer[offset + 2] << 16)
                          | (buffer[offset + 3] << 24));
        }

        public static uint ReadUInt32BE(byte[] buffer, int offset)
        {
            return (uint)((buffer[offset] << 24)
                          | (buffer[offset + 1] << 16)
                          | (buffer[offset + 2] << 8)
                          | buffer[offset + 3]);
        }

        public static bool StartsWith(byte[] buffer, byte[] prefix)
        {
            if (buffer == null || prefix == null) return false;
            if (buffer.Length < prefix.Length) return false;
            for (var i = 0; i < prefix.Length; i++)
            {
                if (buffer[i] != prefix[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Checks if the byte array matches the expected byte sequence starting at the specified offset.
        /// </summary>
        /// <param name="buffer">The buffer to check.</param>
        /// <param name="offset">The starting offset in the buffer.</param>
        /// <param name="expected">The expected byte sequence to match.</param>
        /// <returns>True if the buffer contains the expected bytes at the specified offset; otherwise, false.</returns>
        public static bool MatchesAt(byte[] buffer, int offset, byte[] expected)
        {
            if (buffer == null || expected == null) return false;
            if (offset < 0 || offset + expected.Length > buffer.Length) return false;
            for (var i = 0; i < expected.Length; i++)
            {
                if (buffer[offset + i] != expected[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Appends stream data to a hash algorithm, limiting the number of bytes processed.
        /// </summary>
        /// <param name="hash">The hash algorithm to append data to.</param>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="maxBytes">Maximum number of bytes to process (capped at MaxHashBytes).</param>
        /// <param name="cancel">Cancellation token for the operation.</param>
        public static Task AppendStreamAsync(HashAlgorithm hash, Stream stream, long maxBytes, CancellationToken cancel)
        {
            if (hash == null) throw new ArgumentNullException(nameof(hash));
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            return Task.Run(() => AppendStream(hash, stream, maxBytes, cancel, null), cancel);
        }

        /// <summary>
        /// Attempts to read a specific sector from a file stream.
        /// </summary>
        /// <param name="stream">The file stream to read from.</param>
        /// <param name="sectorIndex">The zero-based sector index.</param>
        /// <param name="sectorSize">The size of each sector in bytes.</param>
        /// <param name="buffer">The buffer to read data into.</param>
        /// <param name="count">The number of bytes to read.</param>
        /// <param name="cancel">Cancellation token for the operation.</param>
        /// <returns>True if the sector was successfully read; otherwise, false.</returns>
        public static Task<bool> TryReadSectorAsync(FileStream stream, int sectorIndex, int sectorSize, byte[] buffer, int count, CancellationToken cancel)
        {
            return TryReadSectorAsync((Stream)stream, sectorIndex, sectorSize, buffer, count, cancel);
        }

        public static async Task<bool> TryReadSectorAsync(Stream stream, int sectorIndex, int sectorSize, byte[] buffer, int count, CancellationToken cancel)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

            var offset = (long)sectorIndex * sectorSize;
            if (offset < 0 || offset + count > stream.Length) return false;

            stream.Seek(offset, SeekOrigin.Begin);
            var read = await ReadExactlyAsync(stream, buffer, 0, count, cancel).ConfigureAwait(false);
            return read == count;
        }

        /// <summary>
        /// Attempts to read a sector from a file stream, assuming the buffer length equals the sector size.
        /// </summary>
        /// <param name="stream">The file stream to read from.</param>
        /// <param name="sectorSize">The size of each sector in bytes.</param>
        /// <param name="sectorIndex">The zero-based sector index.</param>
        /// <param name="buffer">The buffer to read data into (must be sized to sectorSize).</param>
        /// <param name="cancel">Cancellation token for the operation.</param>
        /// <returns>True if the sector was successfully read; otherwise, false.</returns>
        public static Task<bool> TryReadSectorAsync(FileStream stream, int sectorSize, int sectorIndex, byte[] buffer, CancellationToken cancel)
        {
            return TryReadSectorAsync((Stream)stream, sectorSize, sectorIndex, buffer, cancel);
        }

        public static async Task<bool> TryReadSectorAsync(Stream stream, int sectorSize, int sectorIndex, byte[] buffer, CancellationToken cancel)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

            var offset = (long)sectorIndex * sectorSize;
            if (offset < 0 || offset + sectorSize > stream.Length) return false;

            stream.Seek(offset, SeekOrigin.Begin);
            var read = await ReadExactlyAsync(stream, buffer, 0, sectorSize, cancel).ConfigureAwait(false);
            return read == sectorSize;
        }
    }
}
