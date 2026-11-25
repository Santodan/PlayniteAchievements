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
        private bool _isCurrent;

        public YearReviewProgressSegment(int index, string title)
        {
            Index = index;
            Title = title;
        }

        /// <summary>The slide's position, for jumping to it.</summary>
        public int Index { get; }

        /// <summary>The slide's name, shown on hover.</summary>
        public string Title { get; }

        /// <summary>True for the current slide and every slide before it.</summary>
        public bool IsReached
        {
            get => _isReached;
            set => SetValue(ref _isReached, value);
        }

        /// <summary>True for the slide on screen.</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set => SetValue(ref _isCurrent, value);
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
        private readonly Func<(string Name, string AvatarPath)> _savedProfile;
        private readonly Action<string, string> _saveProfile;
        private CancellationTokenSource _load;
        private YearReviewLibrary _library;
        private IReadOnlyList<int> _years = Array.Empty<int>();
        private int _selectedYear;
        private IReadOnlyList<YearReviewSlide> _slides = Array.Empty<YearReviewSlide>();
        private IReadOnlyList<YearReviewProgressSegment> _segments = Array.Empty<YearReviewProgressSegment>();
        private int _index;
        private bool _isLoading = true;

        public YearReviewViewModel(
            YearReviewReader reader,
            ILogger logger,
            Func<(string Name, string AvatarPath)> savedProfile = null,
            Action<string, string> saveProfile = null,
            Func<bool> showInOverview = null,
            Action<bool> saveShowInOverview = null)
        {
            _reader = reader;
            _logger = logger;
            _savedProfile = savedProfile;
            _saveProfile = saveProfile;
            _showInOverview = showInOverview;
            _saveShowInOverview = saveShowInOverview;
        }

        private readonly Func<bool> _showInOverview;
        private readonly Action<bool> _saveShowInOverview;

        /// <summary>Whether the Overview offers this window around the turn of the year; saved on change.</summary>
        public bool ShowInOverview
        {
            get => _showInOverview?.Invoke() ?? true;
            set
            {
                if (value == ShowInOverview)
                {
                    return;
                }

                _saveShowInOverview?.Invoke(value);
                OnPropertyChanged();
            }
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

        /// <summary>The current slide's number, two digits wide.</summary>
        public string Position => _slides.Count == 0 ? string.Empty : (_index + 1).ToString("00", FormattingCulture.Current);

        /// <summary>How many slides the year has.</summary>
        public string Total => _slides.Count == 0 ? string.Empty : _slides.Count.ToString(FormattingCulture.Current);

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
                _segments[i].IsCurrent = i == _index;
            }

            OnPropertyChanged(nameof(CurrentSlide));
            OnPropertyChanged(nameof(Position));
            OnPropertyChanged(nameof(Total));
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
                    return (built, new YearReviewArt
                    {
                        TopFriendAvatarPath = built.Friends.TopFriendKey != null ? reader.FriendAvatar(built.Friends.TopFriendKey) : null
                    });
                }, cancel.Token).ConfigureAwait(true);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                var covers = library.Games
                    .GroupBy(game => game.GameId)
                    .ToDictionary(group => group.Key, group => group.First().CoverPath);
                var backdrops = new Dictionary<Guid, string>();
                art.BackdropOf = id =>
                {
                    if (!backdrops.TryGetValue(id, out var path))
                    {
                        path = reader.BackgroundPath(id);
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            covers.TryGetValue(id, out path);
                        }

                        backdrops[id] = path;
                    }

                    return path;
                };

                var slides = YearReviewSlideFactory.Create(data, art);
                foreach (var friends in slides.OfType<YearReviewFriendsSlide>())
                {
                    friends.PropertyChanged += OnFriendsViewChanged;
                }

                var saved = _savedProfile?.Invoke() ?? (null, null);
                foreach (var summary in slides.OfType<YearReviewSummarySlide>())
                {
                    summary.CardName = string.IsNullOrWhiteSpace(saved.Name) ? library.ProfileName : saved.Name;
                    summary.CardAvatarPath = string.IsNullOrWhiteSpace(saved.AvatarPath) ? library.ProfileAvatarPath : saved.AvatarPath;
                    summary.PropertyChanged += OnCardEdited;
                }

                _index = -1;
                Segments = slides.Select((slide, index) => new YearReviewProgressSegment(index, slide.Title ?? ResourceProvider.GetString("LOCPlayAch_YearReview_Title"))).ToList();
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

        /// <summary>Keeps the card's name and picture for next time; a cleared name goes back to the profile's.</summary>
        private void OnCardEdited(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!(sender is YearReviewSummarySlide summary)
                || (e.PropertyName != nameof(YearReviewSummarySlide.CardName) && e.PropertyName != nameof(YearReviewSummarySlide.CardAvatarPath)))
            {
                return;
            }

            var name = string.IsNullOrWhiteSpace(summary.CardName) ? null : summary.CardName.Trim();
            var avatar = string.IsNullOrWhiteSpace(summary.CardAvatarPath) ? null : summary.CardAvatarPath;
            try
            {
                _saveProfile?.Invoke(name, avatar);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Year in Review: the card profile could not be saved.");
            }
        }

        /// <summary>Reads a friend's avatar the first time their view is picked.</summary>
        private async void OnFriendsViewChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(YearReviewSlide.CurrentView)
                || !(sender is YearReviewSlide slide)
                || !(slide.CurrentView is YearReviewFriendsView view)
                || view.AvatarRequested
                || string.IsNullOrEmpty(view.FriendKey))
            {
                return;
            }

            view.AvatarRequested = true;
            try
            {
                var key = view.FriendKey;
                var reader = _reader;
                view.AvatarPath = await Task.Run(() => reader.FriendAvatar(key)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Year in Review: friend avatar could not be read.");
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
