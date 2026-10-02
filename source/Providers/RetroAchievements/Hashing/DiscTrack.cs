using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    /// <summary>
    /// One opened track of a disc image, modeled on rcheevos's rc_hash_cdrom_track_t (cdreader.c).
    /// Sectors are addressed by absolute LBA: the payload of sector <c>lba</c> starts at
    /// <c>(lba - TrackFirstSector) * SectorSize + HeaderSize + FileTrackOffset</c> in the track file,
    /// and each sector contributes <see cref="RawDataSize"/> payload bytes.
    /// </summary>
    internal sealed class DiscTrack : IDisposable
    {
        public const int CookedSectorSize = 2048;

        /// <summary>Raw sectors per read when the payload view has to strip sector headers.</summary>
        private const int ViewBatchSectors = 28;

        /// <summary>Sectors per read for the sector-run hashers.</summary>
        public const int RunSectors = 32;

        private readonly Stream _stream;
        private readonly long _streamLength;
        private byte[] _batch;

        internal DiscTrack(
            Stream stream,
            int trackNumber,
            int sectorSize,
            int headerSize,
            int rawDataSize,
            long fileTrackOffset,
            int trackFirstSector,
            int pregapSectors,
            string filePath = null)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            FilePath = filePath;
            _streamLength = stream.Length;
            TrackNumber = trackNumber;
            SectorSize = sectorSize;
            HeaderSize = headerSize;
            RawDataSize = rawDataSize;
            FileTrackOffset = fileTrackOffset;
            TrackFirstSector = trackFirstSector;
            PregapSectors = pregapSectors;
        }

        /// <summary>The track file, or null for a stream source.</summary>
        public string FilePath { get; }

        public int TrackNumber { get; }

        /// <summary>Bytes per sector in the track file (2048, 2336, or 2352).</summary>
        public int SectorSize { get; }

        /// <summary>Bytes skipped at the start of each sector before the payload.</summary>
        public int HeaderSize { get; }

        /// <summary>Payload bytes per sector: 2048 for data tracks, 2352 for audio tracks.</summary>
        public int RawDataSize { get; }

        public long FileTrackOffset { get; }

        /// <summary>Absolute LBA of the first sector stored for the track (its first INDEX).</summary>
        public int TrackFirstSector { get; }

        public int PregapSectors { get; }

        /// <summary>Absolute LBA of INDEX 01, as rc_cd_first_track_sector (cdreader.c:840-847).</summary>
        public int FirstTrackSector => TrackFirstSector + PregapSectors;

        /// <summary>
        /// Reads <paramref name="count"/> bytes starting at sector <paramref name="lba"/> with the
        /// semantics of cdreader_read_sector (cdreader.c:791-826): whole payloads of consecutive
        /// sectors while more than one payload is requested, then the remainder from the next sector.
        /// Returns fewer bytes at the end of the file, and 0 for sectors before the track.
        /// </summary>
        public int ReadSector(long lba, byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            var fullSectors = (count - 1) / RawDataSize;
            var total = 0;
            if (fullSectors > 0)
            {
                total = ReadSectorRun(lba, fullSectors, RawDataSize, buffer, offset);
                if (total < fullSectors * RawDataSize)
                {
                    return total;
                }
            }

            return total + ReadSectorRun(lba + fullSectors, 1, count - (fullSectors * RawDataSize), buffer, offset + total);
        }

        /// <summary>
        /// Reads the first <paramref name="bytesPerSector"/> payload bytes of each of
        /// <paramref name="sectorCount"/> consecutive sectors into one contiguous buffer, as if
        /// <see cref="ReadSector"/> were called once per sector. Stops after the first sector that
        /// is short at the end of the file.
        /// </summary>
        public int ReadSectorRun(long lba, int sectorCount, int bytesPerSector, byte[] buffer, int offset)
        {
            if (bytesPerSector <= 0 || bytesPerSector > RawDataSize) throw new ArgumentOutOfRangeException(nameof(bytesPerSector));
            if (sectorCount <= 0)
            {
                return 0;
            }

            return ReadPayload(lba, bytesPerSector, 0, buffer, offset, (long)sectorCount * bytesPerSector, RunSectors);
        }

        /// <summary>Reads a sector run on a thread-pool thread.</summary>
        public Task<int> ReadSectorRunAsync(long lba, int sectorCount, int bytesPerSector, byte[] buffer, CancellationToken cancel)
        {
            return Task.Run(() => ReadSectorRun(lba, sectorCount, bytesPerSector, buffer, 0), cancel);
        }

        /// <summary>
        /// Payload bytes readable from sector <paramref name="lba"/> to the end of the track file.
        /// </summary>
        public long PayloadBytesFrom(long lba)
        {
            if (lba < TrackFirstSector)
            {
                return 0;
            }

            var fileBytes = _streamLength - SectorStart(lba);
            if (fileBytes <= 0)
            {
                return 0;
            }

            var full = fileBytes / SectorSize;
            var rest = fileBytes % SectorSize;
            return (full * RawDataSize) + Math.Max(0, Math.Min(RawDataSize, rest - HeaderSize));
        }

        /// <summary>
        /// A 2048-byte-sector view of a data track for filesystem readers. See
        /// <see cref="PayloadStream"/> for how view sectors map onto disc LBAs.
        /// </summary>
        public Stream OpenPayloadStream(bool ownsTrack = false)
        {
            if (RawDataSize != CookedSectorSize)
            {
                throw new InvalidDataException("Only data tracks expose a 2048-byte payload view.");
            }

            return new PayloadStream(this, ownsTrack);
        }

        /// <summary>
        /// Byte offset of sector <paramref name="lba"/> in the payload view, or -1 when the view
        /// does not reach it. Inverse of the mapping described on <see cref="PayloadStream"/>.
        /// </summary>
        public long ToPayloadOffset(long lba)
        {
            long viewSector;
            if (TrackFirstSector <= 0)
            {
                viewSector = lba - FirstTrackSector;
            }
            else if (lba >= TrackFirstSector)
            {
                viewSector = lba;
            }
            else
            {
                viewSector = lba - FirstTrackSector;
                if (viewSector >= TrackFirstSector)
                {
                    return -1;
                }
            }

            return viewSector < 0 ? -1 : viewSector * CookedSectorSize;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        private long SectorStart(long lba)
        {
            return ((lba - TrackFirstSector) * SectorSize) + FileTrackOffset;
        }

        /// <summary>
        /// Copies <paramref name="count"/> bytes of the stream formed by the first
        /// <paramref name="bytesPerSector"/> payload bytes of each sector from <paramref name="lba"/>
        /// on, skipping <paramref name="skip"/> bytes of the first sector.
        /// </summary>
        private int ReadPayload(long lba, int bytesPerSector, int skip, byte[] dest, int destOffset, long count, int batchSectors)
        {
            if (lba < TrackFirstSector || count <= 0)
            {
                return 0;
            }

            // Payload runs contiguously in the file: one read straight into the caller's buffer.
            if (HeaderSize == 0 && bytesPerSector == SectorSize)
            {
                return ReadAt(SectorStart(lba) + skip, dest, destOffset, (int)count);
            }

            var batchBytes = batchSectors * SectorSize;
            if (_batch == null || _batch.Length < batchBytes)
            {
                _batch = new byte[batchBytes];
            }

            var total = 0;
            var within = skip;
            while (count > 0)
            {
                var sectorsNeeded = (within + count + bytesPerSector - 1) / bytesPerSector;
                var sectors = (int)Math.Min(batchSectors, sectorsNeeded);
                var got = ReadAt(SectorStart(lba), _batch, 0, sectors * SectorSize);

                for (var i = 0; i < sectors && count > 0; i++)
                {
                    var available = got - (i * SectorSize) - HeaderSize;
                    var payloadEnd = Math.Min(bytesPerSector, available);
                    if (payloadEnd <= within)
                    {
                        return total;
                    }

                    var take = (int)Math.Min(count, payloadEnd - within);
                    Buffer.BlockCopy(_batch, (i * SectorSize) + HeaderSize + within, dest, destOffset + total, take);
                    total += take;
                    count -= take;

                    if (payloadEnd < bytesPerSector)
                    {
                        return total;
                    }

                    within = 0;
                }

                lba += sectors;
            }

            return total;
        }

        private int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            if (position < 0 || position >= _streamLength || count <= 0)
            {
                return 0;
            }

            if (_stream.Position != position)
            {
                _stream.Seek(position, SeekOrigin.Begin);
            }

            return HashUtils.ReadFull(_stream, buffer, offset, count);
        }

        /// <summary>
        /// 2048-byte logical sectors over a data track. For a track that starts at or before LBA 0,
        /// view sector N is LBA FirstTrackSector + N, so the view begins at INDEX 01. For a track
        /// that starts later on the disc, view sector N is absolute LBA N, and view sectors below the
        /// track start alias to the start of the track: rc_cd_find_file_sector (hash_disc.c:116)
        /// reads the volume descriptors at first_track_sector + 16 and then follows absolute
        /// directory LBAs, and this layout lets an ISO9660 reader do the same.
        /// </summary>
        private sealed class PayloadStream : Stream
        {
            private readonly DiscTrack _track;
            private readonly bool _ownsTrack;
            private readonly bool _absolute;
            private readonly long _length;
            private long _position;

            public PayloadStream(DiscTrack track, bool ownsTrack)
            {
                _track = track;
                _ownsTrack = ownsTrack;
                _absolute = track.TrackFirstSector > 0;
                _length = _absolute
                    ? ((long)track.TrackFirstSector * CookedSectorSize) + track.PayloadBytesFrom(track.TrackFirstSector)
                    : track.PayloadBytesFrom(track.FirstTrackSector);
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _position;
                set
                {
                    if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                    _position = value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
                if (count == 0 || _position >= _length) return 0;

                var remaining = (int)Math.Min(count, _length - _position);
                var total = 0;
                while (remaining > 0)
                {
                    var viewSector = _position / CookedSectorSize;
                    var within = (int)(_position % CookedSectorSize);

                    long lba;
                    long runBytes;
                    if (!_absolute)
                    {
                        lba = _track.FirstTrackSector + viewSector;
                        runBytes = remaining;
                    }
                    else if (viewSector < _track.TrackFirstSector)
                    {
                        lba = _track.FirstTrackSector + viewSector;
                        runBytes = ((_track.TrackFirstSector - viewSector) * CookedSectorSize) - within;
                    }
                    else
                    {
                        lba = viewSector;
                        runBytes = remaining;
                    }

                    var toRead = (int)Math.Min(remaining, runBytes);
                    var read = _track.ReadPayload(lba, CookedSectorSize, within, buffer, offset + total, toRead, ViewBatchSectors);
                    total += read;
                    _position += read;
                    remaining -= read;
                    if (read < toRead)
                    {
                        break;
                    }
                }

                return total;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                switch (origin)
                {
                    case SeekOrigin.Begin: Position = offset; break;
                    case SeekOrigin.Current: Position = _position + offset; break;
                    case SeekOrigin.End: Position = _length + offset; break;
                    default: throw new ArgumentOutOfRangeException(nameof(origin));
                }

                return _position;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && _ownsTrack)
                {
                    _track.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
