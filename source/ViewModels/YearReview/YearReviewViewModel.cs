using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.YearReview;
using PlayniteAchievements.Services.YearReview;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.YearReview
{
    /// <summary>One segment of the progress strip over the slides.</summary>
    public sealed class YearReviewProgressSegment : ObservableObject
    {
        private bool _isReached;

        /// <summary>True for the current slide and every slide before it.</summary>
        public bool IsReached
        {
            get => _isReached;
            set => SetValue(ref _isReached, value);
        }
    }

    /// <summary>
    /// The Year in Review window: the year picker, the slides of the selected year and the
    /// position in them. Reads run off the UI thread; slides are built back on it.
    /// </summary>
    internal sealed class YearReviewViewModel : ObservableObject, IDisposable
    {
        private readonly YearReviewReader _reader;
        private readonly ILogger _logger;
        private CancellationTokenSource _load;
        private YearReviewLibrary _library;
        private IReadOnlyList<int> _years = Array.Empty<int>();
        private int _selectedYear;
        private IReadOnlyList<YearReviewSlide> _slides = Array.Empty<YearReviewSlide>();
        private IReadOnlyList<YearReviewProgressSegment> _segments = Array.Empty<YearReviewProgressSegment>();
        private int _index;
        private bool _isLoading = true;

        public YearReviewViewModel(YearReviewReader reader, ILogger logger)
        {
            _reader = reader;
            _logger = logger;
        }

        public IReadOnlyList<int> Years
        {
            get => _years;
            private set => SetValue(ref _years, value);
        }

        public int SelectedYear
        {
            get => _selectedYear;
            set
            {
                if (_selectedYear == value)
                {
                    return;
                }

                SetValue(ref _selectedYear, value);
                _ = LoadYearAsync(value);
            }
        }

        public IReadOnlyList<YearReviewSlide> Slides
        {
            get => _slides;
            private set => SetValue(ref _slides, value);
        }

        public IReadOnlyList<YearReviewProgressSegment> Segments
        {
            get => _segments;
            private set => SetValue(ref _segments, value);
        }

        public YearReviewSlide CurrentSlide => _index >= 0 && _index < _slides.Count ? _slides[_index] : null;

        public string Counter => _slides.Count == 0
            ? string.Empty
            : (_index + 1).ToString(FormattingCulture.Current) + " / " + _slides.Count.ToString(FormattingCulture.Current);

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetValue(ref _isLoading, value);
        }

        public async Task LoadAsync()
        {
            var cancel = Restart();
            try
            {
                _library = await _reader.LoadLibraryAsync(cancel.Token).ConfigureAwait(true);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                var years = _library.Years.Count > 0 ? _library.Years : new[] { DateTime.Today.Year };
                Years = years;
                _selectedYear = years[0];
                OnPropertyChanged(nameof(SelectedYear));
                await LoadYearAsync(_selectedYear).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Year in Review: the library could not be read.");
                IsLoading = false;
            }
        }

        public void Go(int index)
        {
            if (index < 0 || index >= _slides.Count || index == _index)
            {
                return;
            }

            _index = index;
            for (var i = 0; i < _segments.Count; i++)
            {
                _segments[i].IsReached = i <= _index;
            }

            OnPropertyChanged(nameof(CurrentSlide));
            OnPropertyChanged(nameof(Counter));
        }

        private async Task LoadYearAsync(int year)
        {
            if (_library == null)
            {
                return;
            }

            var cancel = Restart();
            IsLoading = true;
            try
            {
                var library = _library;
                var reader = _reader;
                var (data, art) = await Task.Run(() =>
                {
                    var built = YearReviewBuilder.Build(year, reader.ReadYear(library, year, DateTime.Today));
                    var covers = library.Games
                        .GroupBy(game => game.GameId)
                        .ToDictionary(group => group.Key, group => group.First().CoverPath);
                    var backdrops = new Dictionary<Guid, string>();
                    foreach (var kind in built.Slides)
                    {
                        if (YearReviewSlideFactory.BackdropGame(built, kind) is Guid id && !backdrops.ContainsKey(id))
                        {
                            var path = reader.BackgroundPath(id);
                            if (string.IsNullOrWhiteSpace(path))
                            {
                                covers.TryGetValue(id, out path);
                            }

                            backdrops[id] = path;
                        }
                    }

                    return (built, new YearReviewArt
                    {
                        Backdrops = backdrops,
                        TopFriendAvatarPath = built.Friends.TopFriendKey != null ? reader.FriendAvatar(built.Friends.TopFriendKey) : null
                    });
                }, cancel.Token).ConfigureAwait(true);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                var slides = YearReviewSlideFactory.Create(data, art);
                _index = -1;
                Segments = slides.Select(_ => new YearReviewProgressSegment()).ToList();
                Slides = slides;
                IsLoading = false;
                Go(0);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Year in Review: {year} could not be built.");
                IsLoading = false;
            }
        }

        private CancellationTokenSource Restart()
        {
            _load?.Cancel();
            _load?.Dispose();
            _load = new CancellationTokenSource();
            return _load;
        }

        public void Dispose()
        {
            _load?.Cancel();
            _load?.Dispose();
            _load = null;
            _library = null;
        }
    }
}
