using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using XamlAnimatedGif;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Owns one XamlAnimatedGif decoder attached to an Image. The decoder renders the GIF at its
    /// native dimensions into one stable WriteableBitmap and reads compressed frame data on demand.
    /// </summary>
    internal sealed class NativeGifAnimation : IDisposable
    {
        private readonly Image _image;
        private readonly NativeGifPayloadCache.Lease _payloadLease;
        private readonly MemoryStream _stream;
        private readonly ImageSource _fallback;
        private readonly bool _applyGray;
        private readonly Action<Exception> _onError;
        private readonly Action _onSourceReady;
        private bool _disposed;

        private NativeGifAnimation(
            Image image,
            string sourceIdentity,
            NativeGifPayloadCache.Lease payloadLease,
            ImageSource fallback,
            bool applyGray,
            Action<Exception> onError,
            Action onSourceReady)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
            SourceIdentity = sourceIdentity;
            _payloadLease = payloadLease ?? throw new ArgumentNullException(nameof(payloadLease));
            _stream = payloadLease.OpenRead();
            _fallback = fallback;
            _applyGray = applyGray;
            _onError = onError;
            _onSourceReady = onSourceReady;
        }

        internal string SourceIdentity { get; }

        internal Image Target => _image;

        internal event EventHandler Failed;

        internal static async Task<NativeGifAnimation> CreateAsync(
            Image image,
            string sourceIdentity,
            string localPath,
            ImageSource fallback,
            bool applyGray,
            CancellationToken cancellationToken,
            Action<Exception> onError = null,
            Action onSourceReady = null)
        {
            var lease = await NativeGifPayloadCache
                .AcquireAsync(localPath, cancellationToken)
                .ConfigureAwait(true);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new NativeGifAnimation(
                    image,
                    sourceIdentity,
                    lease,
                    fallback,
                    applyGray,
                    onError,
                    onSourceReady);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        internal void Start()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeGifAnimation));
            }

            AnimationBehavior.AddLoadedHandler(_image, OnAnimationLoaded);
            AnimationBehavior.AddErrorHandler(_image, OnAnimationError);
            // false is XamlAnimatedGif's default. Setting it redundantly invokes its SourceChanged
            // callback and performs an extra clear/reinitialization before SourceStream is set.
            AnimationBehavior.SetRepeatBehavior(_image, RepeatBehavior.Forever);
            AnimationBehavior.SetAutoStart(_image, true);
            AnimationBehavior.SetSourceStream(_image, _stream);
        }

        private void OnAnimationLoaded(object sender, RoutedEventArgs e)
        {
            if (_disposed || !ReferenceEquals(sender, _image))
            {
                return;
            }

            try
            {
                if (_applyGray && _image.Source is BitmapSource bitmap)
                {
                    _image.Source = CreateGrayscaleView(bitmap);
                }

                _onSourceReady?.Invoke();
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private void OnAnimationError(DependencyObject sender, AnimationErrorEventArgs e)
        {
            if (!_disposed && ReferenceEquals(sender, _image))
            {
                Fail(e?.Exception ?? new InvalidOperationException("GIF animation failed."));
            }
        }

        private void Fail(Exception exception)
        {
            if (_disposed)
            {
                return;
            }

            _onError?.Invoke(exception);
            Dispose();
            _image.Source = _fallback;
            _onSourceReady?.Invoke();
            Failed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Builds a live grayscale presentation over a mutable source. FormatConvertedBitmap
        /// follows subsequent WriteableBitmap invalidations; the opacity mask comes from the
        /// original BGRA source so transparent GIF pixels remain transparent.
        /// </summary>
        internal static ImageSource CreateGrayscaleView(BitmapSource source)
        {
            if (source == null)
            {
                return null;
            }

            var grayscale = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
            var bounds = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            var group = new DrawingGroup
            {
                OpacityMask = new ImageBrush(source)
                {
                    Stretch = Stretch.Fill
                }
            };
            group.Children.Add(new ImageDrawing(grayscale, bounds));
            return new DrawingImage(group);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AnimationBehavior.RemoveLoadedHandler(_image, OnAnimationLoaded);
            AnimationBehavior.RemoveErrorHandler(_image, OnAnimationError);

            try
            {
                // Clearing the behavior disposes its Animator and cancels its render loop.
                AnimationBehavior.SetSourceStream(_image, null);
            }
            catch
            {
            }

            try { _stream.Dispose(); } catch { }
            _payloadLease.Dispose();
        }
    }

    /// <summary>
    /// Shares immutable compressed GIF bytes between active visuals. Each decoder receives its own
    /// seekable MemoryStream over the same array, so decoders never contend for a stream position
    /// and the original managed image file remains replaceable while it is on screen.
    /// </summary>
    internal static class NativeGifPayloadCache
    {
        internal sealed class Entry
        {
            internal string Key;
            internal Task<byte[]> LoadTask;
            internal int LeaseCount;
        }

        internal sealed class Lease : IDisposable
        {
            private Entry _entry;
            private readonly byte[] _bytes;

            internal Lease(Entry entry, byte[] bytes)
            {
                _entry = entry;
                _bytes = bytes;
            }

            internal MemoryStream OpenRead() => new MemoryStream(_bytes, writable: false);

            internal byte[] PayloadReference => _bytes;

            public void Dispose()
            {
                var entry = Interlocked.Exchange(ref _entry, null);
                if (entry != null)
                {
                    Release(entry);
                }
            }
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Entry> Entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        internal static async Task<Lease> AcquireAsync(string localPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new ArgumentException("A local GIF path is required.", nameof(localPath));
            }

            var fullPath = Path.GetFullPath(localPath);
            var key = BuildKey(fullPath);
            Entry entry;
            lock (Sync)
            {
                if (!Entries.TryGetValue(key, out entry))
                {
                    entry = new Entry
                    {
                        Key = key,
                        LoadTask = Task.Run(() => File.ReadAllBytes(fullPath))
                    };
                    Entries[key] = entry;
                }

                entry.LeaseCount++;
            }

            try
            {
                var bytes = await entry.LoadTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes == null || bytes.Length == 0)
                {
                    throw new InvalidDataException("The GIF file was empty.");
                }

                return new Lease(entry, bytes);
            }
            catch
            {
                Release(entry);
                throw;
            }
        }

        private static string BuildKey(string fullPath)
        {
            var info = new FileInfo(fullPath);
            return string.Concat(
                fullPath,
                "\u001f",
                info.Length.ToString(),
                "\u001f",
                info.LastWriteTimeUtc.Ticks.ToString());
        }

        private static void Release(Entry entry)
        {
            lock (Sync)
            {
                entry.LeaseCount--;
                if (entry.LeaseCount <= 0 &&
                    Entries.TryGetValue(entry.Key, out var current) &&
                    ReferenceEquals(current, entry))
                {
                    Entries.Remove(entry.Key);
                }
            }
        }

        internal static int ActiveEntryCount
        {
            get
            {
                lock (Sync)
                {
                    return Entries.Count;
                }
            }
        }
    }
}
