using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace IslandPlayer
{
    /// <summary>
    /// własne odtwarzanie (pliki z dysku i radio internetowe).
    /// Używa odtwarzacza Windows z wbudowanym „okienkiem multimediów” (SMTC) - dzięki temu wyspa, klawisze
    /// multimedialne, tekst piosenki i Discord działają z nim tak samo jak ze Spotify, bez osobnego kodu.
    /// </summary>
    public static class LocalPlayer
    {
        private static MediaPlayer _player;
        private static MediaPlaybackList _list;
        private static readonly Dictionary<MediaPlaybackItem, LibraryTrack> _tracks = new Dictionary<MediaPlaybackItem, LibraryTrack>();

        /// <summary>Utwór z biblioteki, który teraz gra (null = radio albo nic).</summary>
        public static LibraryTrack CurrentTrack { get; private set; }

        /// <summary>Co teraz gra z naszego odtwarzacza (null = nic).</summary>
        public static string NowPlaying { get; private set; }
        public static event Action Changed;

        private static MediaPlayer Player
        {
            get
            {
                if (_player != null) return _player;
                _player = new MediaPlayer
                {
                    AudioCategory = MediaPlayerAudioCategory.Media,
                    AutoPlay = true,
                };
                _player.CommandManager.IsEnabled = true;     // pauza / następny / przewijanie z wyspy i klawiszy
                _player.Volume = UserVolume;
                _player.MediaEnded += (s, e) =>
                {
                    _listEnded = true;      // koniec całej listy (tryb imprezy wraca do poprzedniego odtwarzacza)
                    // autoplay: po końcu playlisty z Mojej muzyki grają podobne piosenki
                    var last = _lastListTracks;
                    if (last != null && CurrentEpisode == null && CurrentStation == null && !PlayingPreview)
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = AutoplayService.OnListEndedAsync(last)));
                };
                _player.MediaOpened += (s, e) =>
                {
                    // po przeładowaniu (włączenie korektora) wracamy w to samo miejsce utworu
                    double seek = _pendingSeek;
                    _pendingSeek = 0;
                    if (seek > 0) { try { s.PlaybackSession.Position = TimeSpan.FromSeconds(seek); } catch { } }
                };
                _player.MediaFailed += (s, e) =>
                {
                    Log.Info("player: " + e.Error + " " + e.ErrorMessage);
                    var st = CurrentStation;
                    if (st == null) return;
                    if (_live != null)
                    {
                        // odtwarzacz nie radzi sobie z tym strumieniem przez nasz bufor - ta stacja gra bez niego
                        _noBuffer.Add(st.Url);
                        Reconnect(force: true);
                    }
                    else TryAlternate();      // adres nie działa - szukamy zapasowego
                };
                return _player;
            }
        }

        // ================= pliki z dysku =================

        /// <summary>Pozycja listy dla utworu - plik otwierany dopiero, gdy jest potrzebny (szybki start nawet przy tysiącach utworów).</summary>
        private static MediaPlaybackItem MakeItem(LibraryTrack t)
        {
            var binder = new MediaBinder { Token = t.Path };
            binder.Binding += OnBinding;
            var item = new MediaPlaybackItem(MediaSource.CreateFromMediaBinder(binder));
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = t.Title;
            props.MusicProperties.Artist = t.Artist;
            props.MusicProperties.AlbumTitle = t.Album ?? "";
            item.ApplyDisplayProperties(props);
            _tracks[item] = t;
            return item;
        }

        // kolejka „zagraj następne” (tryb imprezy): kolejne dodane utwory ustawiają się za sobą
        private static MediaPlaybackItem _queueAnchor;
        private static int _queuedAhead;

        /// <summary>Dodaje utwór jako następny (po wcześniej dodanych). Gdy nasz odtwarzacz nie gra listy - włącza go od razu.</summary>
        public static void Enqueue(LibraryTrack t)
        {
            if (t == null) return;
            var list = _list;
            if (list == null || CurrentStation != null || PlayingPreview || CurrentTrack == null || list.CurrentItem == null)
            {
                PlayTracks(new[] { t }, 0, false);
                return;
            }
            var cur = list.CurrentItem;
            if (!ReferenceEquals(_queueAnchor, cur)) { _queueAnchor = cur; _queuedAhead = 0; }
            var item = MakeItem(t);
            int ci = list.Items.IndexOf(cur);
            list.Items.Insert(Math.Min(list.Items.Count, ci + 1 + _queuedAhead), item);
            if (list.ShuffleEnabled)
            {
                // przy losowaniu też ma zagrać jako następny
                var order = new List<MediaPlaybackItem>(list.ShuffledItems);
                order.Remove(item);
                int si = order.IndexOf(cur);
                order.Insert(Math.Min(order.Count, si + 1 + _queuedAhead), item);
                list.SetShuffledItems(order);
            }
            _queuedAhead++;
        }

        private static volatile bool _listEnded;
        private static List<LibraryTrack> _lastListTracks;

        /// <summary>Czy lista skończyła się sama od ostatniego sprawdzenia (i kasuje znacznik).</summary>
        public static bool ConsumeListEnded()
        {
            bool e = _listEnded;
            _listEnded = false;
            return e;
        }

        private static double _pendingSeek;
        private static int _playCountToken;

        public static void PlayTracks(IReadOnlyList<LibraryTrack> tracks, int start, bool shuffle)
        {
            if (tracks == null || tracks.Count == 0) return;
            lock (_wrapped) _wrapped.Clear();
            HookSession();     // zdarzenia pauza/gra - dla przycisków w oknie
            SaveGain();
            StopLeveler();
            StopLive();
            _radioToken++;
            _icy.Stop();
            CurrentStation = null;
            StreamTitle = null;
            _radioItem = null;
            PlayingPreview = false;
            SavePodcastPosition();
            CurrentEpisode = null;
            StopCrossfade();
            EnsureCrossfadeWatch();
            var list = new MediaPlaybackList { AutoRepeatEnabled = false, ShuffleEnabled = shuffle, MaxPlayedItemsToKeepOpen = 2 };
            _tracks.Clear();
            MediaPlaybackItem startItem = null;
            _queueAnchor = null;
            _queuedAhead = 0;
            _lastListTracks = new List<LibraryTrack>(tracks);
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var item = MakeItem(t);
                list.Items.Add(item);
                if (i == start) startItem = item;
            }
            if (startItem != null) list.StartingItem = startItem;
            list.CurrentItemChanged += (s, e) =>
            {
                if (e.NewItem != null && _tracks.TryGetValue(e.NewItem, out var t))
                {
                    CurrentTrack = t;
                    NowPlaying = t.Artist + " – " + t.Title;
                    _ = LoadCover(e.NewItem, t);
                    _ = CountPlay(t, ++_playCountToken);
                }
                Changed?.Invoke();
            };
            _list = list;
            Player.Source = list;
            Player.Play();
        }

        private static async void OnBinding(MediaBinder sender, MediaBindingEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                string path = sender.Token;
                if (Equalizer.Enabled)
                {
                    // z korektorem: plik dekodujemy sami i podajemy jako WAV przepuszczony przez korektor
                    var eq = await Task.Run(() => EqStream.TryOpen(path));
                    if (eq != null)
                    {
                        lock (_wrapped) _wrapped.Add(path);
                        args.SetStream(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(eq), "audio/wav");
                        return;
                    }
                }
                lock (_wrapped) _wrapped.Remove(path);
                var file = await StorageFile.GetFileFromPathAsync(path);
                args.SetStorageFile(file);
            }
            catch (Exception ex)
            {
                Log.Info("player: nie można otworzyć " + sender.Token + ": " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }

        // pliki, które grają przez korektor (reszta - bezpośrednio z dysku)
        private static readonly HashSet<string> _wrapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Korektor włączony/wyłączony. Wyłączenie działa od razu (strumień przestaje filtrować). Włączenie przy utworze
        /// granym prosto z pliku - przeładowujemy listę i wracamy w to samo miejsce (chwila ciszy).
        /// </summary>
        public static void RefreshEqualizer()
        {
            var t = CurrentTrack;
            var list = _list;
            if (t == null || list == null || _player == null || !Equalizer.Enabled) return;
            bool wrapped;
            lock (_wrapped) wrapped = _wrapped.Contains(t.Path);
            if (wrapped) return;
            try
            {
                var order = new List<LibraryTrack>();
                int start = 0;
                foreach (var item in list.Items)
                {
                    if (!_tracks.TryGetValue(item, out var x)) continue;
                    if (ReferenceEquals(item, list.CurrentItem)) start = order.Count;
                    order.Add(x);
                }
                if (order.Count == 0) return;
                double pos = _player.PlaybackSession.Position.TotalSeconds;
                bool playing = IsPlaying;
                PlayTracks(order, start, list.ShuffleEnabled);
                _pendingSeek = pos;
                if (!playing) _player.Pause();
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        /// <summary>Utwór liczy się jako odsłuchany po 30 s grania (albo połowie, gdy jest krótszy) - do inteligentnych playlist.</summary>
        private static async Task CountPlay(LibraryTrack t, int token)
        {
            try
            {
                double need = t.Duration > 0 ? Math.Min(30, t.Duration * 0.5) : 30, played = 0;
                while (played < need)
                {
                    await Task.Delay(2000);
                    if (token != _playCountToken || !ReferenceEquals(CurrentTrack, t)) return;
                    if (IsPlaying) played += 2;
                }
                PlayStats.Count(t.Path);
            }
            catch (Exception ex) { Log.Info("statystyki: " + ex.Message); }
        }

        /// <summary>Okładka z pliku (wbudowana w MP3/FLAC albo folder.jpg) - dla wyspy i Discorda.</summary>
        private static async Task LoadCover(MediaPlaybackItem item, LibraryTrack t)
        {
            try
            {
                // okładka z biblioteki (wbudowana albo znaleziona w internecie) - z pliku, bo wyspa czyta ją z innego procesu
                if (t.CoverPath == null) await CoverFinder.Find(t);
                if (string.IsNullOrEmpty(t.CoverPath) || !System.IO.File.Exists(t.CoverPath)) return;
                var cover = await StorageFile.GetFileFromPathAsync(t.CoverPath);
                var props = item.GetDisplayProperties();
                props.Thumbnail = RandomAccessStreamReference.CreateFromFile(cover);
                // poprawiony tytuł / wykonawca (gdy plik nie miał tagów)
                props.MusicProperties.Title = t.Title;
                props.MusicProperties.Artist = t.Artist;
                item.ApplyDisplayProperties(props);
            }
            catch (Exception ex)
            {
                Log.Info("player: okładka " + ex.Message);
            }
        }

        // ================= radio =================
        public static RadioStation CurrentStation { get; private set; }
        /// <summary>„Wykonawca - Tytuł” z radia (null = stacja nie podaje).</summary>
        public static string StreamTitle { get; private set; }
        /// <summary>Czas od kliknięcia do pierwszego dźwięku (s), liczba przerw (buforowanie) w trakcie słuchania.</summary>
        public static double ConnectSeconds { get; private set; }
        /// <summary>Przerwy: zacięcia odtwarzacza + zacięcia naszego bufora (gdy skończył się zapas).</summary>
        public static int Dropouts
        {
            get => _dropouts + (_live?.Underruns ?? 0);
            private set => _dropouts = value - (_live?.Underruns ?? 0);
        }
        private static int _dropouts;
        public static bool Buffering { get; private set; }

        private static readonly IcyReader _icy = new IcyReader();
        private static MediaPlaybackItem _radioItem;
        private static System.Diagnostics.Stopwatch _connectWatch;
        private static bool _sessionHooked;

        public static string LiveSignal =>
            CurrentStation == null || ConnectSeconds <= 0 ? L.T("łączenie…", "connecting…")
            : Dropouts == 0 && ConnectSeconds < 16 ? L.T("doskonały", "excellent")
            : Dropouts <= 1 ? L.T("dobry", "good")
            : Dropouts <= 4 ? L.T("przerywa", "dropping out")
            : L.T("słaby", "poor");

        private static LiveRadioStream _live;
        private static readonly HashSet<string> _triedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _noBuffer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static RadioStation _clickedStation;

        /// <summary>Zapas dźwięku w sekundach (własny bufor) i jego docelowa wielkość; 0 = bufor odtwarzacza Windows.</summary>
        public static double BufferSeconds => (_live ?? _pendingLive)?.BufferedSeconds ?? 0;
        public static double BufferTarget => (_live ?? _pendingLive)?.TargetSeconds ?? 0;
        private static LiveRadioStream _pendingLive;     // jeszcze zbiera zapas przed startem
        public static int StreamReconnects => _live?.Reconnects ?? 0;

        // ================= przenikanie utworów (crossfade) =================
        // Przygotowanie: ~3 s wcześniej drugi, niewidoczny odtwarzacz otwiera obecną piosenkę i czeka (bez opóźnienia na starcie).
        // Przejęcie: przez 0,25 s oba grają TĘ SAMĄ końcówkę (płynna zamiana, bez przeskoku), potem główny odtwarzacz
        // (ten w wyspie) przechodzi do następnej piosenki i ją podgłaśnia, a drugi wycisza końcówkę poprzedniej.
        // Głośność zmienia się co ~10 ms w tle - bez „schodków” słyszalnych przy wolniejszym odświeżaniu.
        private static System.Windows.Threading.DispatcherTimer _xfTimer;
        private static MediaPlayer _tail;
        private static LibraryTrack _tailTrack;
        private static bool _tailReady, _fading;
        private static System.Threading.CancellationTokenSource _xfCts;

        private static void EnsureCrossfadeWatch()
        {
            if (_xfTimer != null) return;
            _xfTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _xfTimer.Tick += (s, e) => CrossfadeTick();
            _xfTimer.Start();
        }

        private static void CrossfadeTick()
        {
            try
            {
                if (_fading) return;
                int sec = App.Config.Crossfade;
                var t = CurrentTrack;
                if (sec <= 0 || _player == null || _list == null || CurrentStation != null || PlayingPreview || t == null)
                {
                    if (_tail != null) StopCrossfade();
                    return;
                }
                var ses = _player.PlaybackSession;
                if (ses.PlaybackState != MediaPlaybackState.Playing) return;
                double dur = ses.NaturalDuration.TotalSeconds, pos = ses.Position.TotalSeconds;
                if (dur < sec * 2 + 5) return;                    // krótkie utwory bez przenikania
                double remain = dur - pos;
                if (_tail != null && !ReferenceEquals(_tailTrack, t)) StopCrossfade();     // zmienił się utwór (np. przewinięcie)
                if (remain > sec + 3 || remain < 0.8) return;
                if (!HasNextItem()) return;

                if (_tail == null) { PrepareTail(t); return; }   // ~3 s przed - otwieramy w tle
                if (remain <= sec && _tailReady) _ = RunCrossfade(t, Math.Min(sec, remain - 0.3));
            }
            catch (Exception ex) { Log.Info("przenikanie: " + ex.Message); StopCrossfade(); }
        }

        private static bool HasNextItem()
        {
            var cur = _list?.CurrentItem;
            if (cur == null) return false;
            if (_list.AutoRepeatEnabled) return true;
            var order = _list.ShuffleEnabled ? new List<MediaPlaybackItem>(_list.ShuffledItems) : new List<MediaPlaybackItem>(_list.Items);
            int idx = order.IndexOf(cur);
            return idx >= 0 && idx < order.Count - 1;
        }

        private static void PrepareTail(LibraryTrack t)
        {
            _tailReady = false;
            _tailTrack = t;
            var tail = new MediaPlayer { AutoPlay = false, Volume = 0 };
            tail.CommandManager.IsEnabled = false;            // niewidoczny dla wyspy i klawiszy multimedialnych
            _tail = tail;
            MediaSource src = null;
            if (Equalizer.Enabled)
            {
                var eq = EqStream.TryOpen(t.Path);           // ten sam dźwięk co z korektorem
                if (eq != null) src = MediaSource.CreateFromStream(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(eq), "audio/wav");
            }
            src ??= MediaSource.CreateFromUri(new Uri(t.Path));
            tail.MediaOpened += (s, e) =>
            {
                // od razu ustawiamy się blisko miejsca przejęcia - później skok jest krótki i szybki
                try { s.PlaybackSession.Position = _player.PlaybackSession.Position + TimeSpan.FromSeconds(2); } catch { }
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(_tail, s)) _tailReady = true; }));
            };
            tail.MediaFailed += (s, e) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(_tail, s)) StopCrossfade(); }));
            tail.Source = src;
        }

        private static async Task RunCrossfade(LibraryTrack t, double len)
        {
            var main = _player;
            var tail = _tail;
            var list = _list;
            if (main == null || tail == null || list == null) return;
            _fading = true;
            _xfCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _xfCts = cts;
            var tok = cts.Token;
            try
            {
                await Task.Run(async () =>
                {
                    // 1) końcówka startuje w miejscu, w którym gra główny odtwarzacz
                    tail.Volume = 0;
                    tail.PlaybackSession.Position = main.PlaybackSession.Position + TimeSpan.FromMilliseconds(30);
                    tail.Play();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (tail.PlaybackSession.PlaybackState != MediaPlaybackState.Playing && sw.ElapsedMilliseconds < 600)
                    {
                        tok.ThrowIfCancellationRequested();
                        await Task.Delay(5);
                    }
                    // 2) przez 0,25 s oba grają to samo - zamiana bez przeskoku
                    const double blend = 0.25;
                    sw.Restart();
                    while (true)
                    {
                        tok.ThrowIfCancellationRequested();
                        double p = Math.Min(1, sw.Elapsed.TotalSeconds / blend), v = UserVolume;
                        tail.Volume = v * Math.Sin(p * Math.PI / 2);
                        main.Volume = v * Math.Cos(p * Math.PI / 2);
                        if (p >= 1) break;
                        await Task.Delay(10);
                    }
                    // 3) główny odtwarzacz przechodzi do następnej piosenki (po cichu) i ją podgłaśnia
                    main.Volume = 0;
                    list.MoveNext();
                    double fade = Math.Max(1, len - blend);
                    sw.Restart();
                    while (true)
                    {
                        tok.ThrowIfCancellationRequested();
                        if (main.PlaybackSession.PlaybackState == MediaPlaybackState.Paused) break;     // pauza w trakcie
                        double p = Math.Min(1, sw.Elapsed.TotalSeconds / fade), v = UserVolume;
                        // krzywa równej mocy, łagodnie wygładzona na początku i końcu
                        double sp = p * p * (3 - 2 * p);
                        main.Volume = v * Math.Sin(sp * Math.PI / 2);
                        tail.Volume = v * Math.Cos(sp * Math.PI / 2);
                        if (p >= 1) break;
                        await Task.Delay(10);
                    }
                }, tok);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Info("przenikanie: " + ex.Message); }
            finally
            {
                if (ReferenceEquals(_xfCts, cts))
                {
                    _xfCts = null;
                    _fading = false;
                    StopTail();
                    try { if (_player != null) _player.Volume = UserVolume; } catch { }
                }
                cts.Dispose();
            }
        }

        private static void StopTail()
        {
            var t = _tail;
            _tail = null;
            _tailTrack = null;
            _tailReady = false;
            if (t == null) return;
            try { t.Pause(); t.Dispose(); } catch { }
        }

        private static void StopCrossfade()
        {
            bool was = _fading;
            try { _xfCts?.Cancel(); } catch { }
            _xfCts = null;
            _fading = false;
            StopTail();
            if (was && _player != null) { try { _player.Volume = UserVolume; } catch { } }
        }

        // ================= podcasty =================
        /// <summary>Grany odcinek podcastu (null = nie podcast).</summary>
        public static PodcastEpisode CurrentEpisode { get; private set; }
        private static System.Windows.Threading.DispatcherTimer _podTimer;

        public static async void PlayPodcast(PodcastEpisode ep)
        {
            if (ep == null || string.IsNullOrWhiteSpace(ep.Url)) return;
            var ui = System.Windows.Application.Current?.Dispatcher;
            if (ui != null && !ui.CheckAccess()) { ui.BeginInvoke(new Action(() => PlayPodcast(ep))); return; }
            try
            {
                HookSession();
                StopCrossfade();
                SaveGain();
                StopLeveler();
                StopLive();
                SavePodcastPosition();
                _radioToken++;
                int token = _radioToken;
                _icy.Stop();
                lock (_wrapped) _wrapped.Clear();
                CurrentStation = null;
                StreamTitle = null;
                _radioItem = null;
                _list = null;
                _tracks.Clear();
                CurrentTrack = null;
                PlayingPreview = false;
                CurrentEpisode = ep;
                NowPlaying = ep.Show + " – " + ep.Title;
                Changed?.Invoke();

                var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(ep.Url)));
                var props = item.GetDisplayProperties();
                props.Type = MediaPlaybackType.Music;
                props.MusicProperties.Title = ep.Title;
                props.MusicProperties.Artist = ep.Show;
                item.ApplyDisplayProperties(props);
                // wracamy tam, gdzie ostatnio skończyłeś
                double pos = PodcastService.IsDone(ep.Key) ? 0 : PodcastService.Position(ep.Key);
                _pendingSeek = pos > 5 ? pos : 0;
                // gramy od razu - okładka dochodzi chwilę później (wcześniej start czekał na jej pobranie)
                Player.Source = item;
                Player.Volume = UserVolume;
                Player.Play();
                Changed?.Invoke();
                string cover = await DownloadPreviewCover(ep.Art);
                if (token == _radioToken && cover != null)
                {
                    try
                    {
                        props.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(cover));
                        item.ApplyDisplayProperties(props);
                    }
                    catch { }
                }
                if (_podTimer == null)
                {
                    _podTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    _podTimer.Tick += (s, e) => SavePodcastPosition();
                    _podTimer.Start();
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        private static void SavePodcastPosition()
        {
            var ep = CurrentEpisode;
            if (ep == null || _player == null) return;
            try
            {
                var ses = _player.PlaybackSession;
                PodcastService.SetPosition(ep.Key, ses.Position.TotalSeconds, ses.NaturalDuration.TotalSeconds);
            }
            catch { }
        }

        // ================= 30-sekundowe fragmenty (szybkie wyszukiwanie bez Spotify Premium) =================
        private static readonly System.Net.Http.HttpClient PreviewHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        /// <summary>Gra fragment piosenki z internetu (nie z biblioteki i nie radio).</summary>
        public static bool PlayingPreview { get; private set; }

        public static async void PlayPreview(string title, string artist, string previewUrl, string artUrl)
        {
            if (string.IsNullOrWhiteSpace(previewUrl)) return;
            var ui = System.Windows.Application.Current?.Dispatcher;
            if (ui != null && !ui.CheckAccess()) { ui.BeginInvoke(new Action(() => PlayPreview(title, artist, previewUrl, artUrl))); return; }
            try
            {
                HookSession();
                StopCrossfade();
                SaveGain();
                StopLeveler();
                StopLive();
                _radioToken++;
                int token = _radioToken;
                _icy.Stop();
                lock (_wrapped) _wrapped.Clear();
                CurrentStation = null;
                StreamTitle = null;
                _radioItem = null;
                _list = null;
                _tracks.Clear();
                CurrentTrack = null;
                SavePodcastPosition();
                CurrentEpisode = null;
                PlayingPreview = true;
                NowPlaying = artist + " – " + title;
                Changed?.Invoke();

                var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(previewUrl)));
                var props = item.GetDisplayProperties();
                props.Type = MediaPlaybackType.Music;
                props.MusicProperties.Title = title;
                props.MusicProperties.Artist = artist + " · " + L.T("fragment 30 s", "30 s preview");
                item.ApplyDisplayProperties(props);
                // gramy od razu, okładka dochodzi zaraz potem
                Player.Source = item;
                Player.Volume = UserVolume;
                Player.Play();
                string cover = await DownloadPreviewCover(artUrl);
                if (token == _radioToken && cover != null)
                {
                    try
                    {
                        props.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(cover));
                        item.ApplyDisplayProperties(props);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        private static async Task<string> DownloadPreviewCover(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                // większa okładka z katalogu (100x100 → 600x600)
                url = url.Replace("100x100bb", "600x600bb");
                string dir = System.IO.Path.Combine(Settings.Folder, "covers");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "preview-" + ((uint)url.GetHashCode()).ToString("x8") + ".jpg");
                if (!System.IO.File.Exists(file))
                {
                    byte[] data = await PreviewHttp.GetByteArrayAsync(url);
                    await System.IO.File.WriteAllBytesAsync(file, data);
                }
                return file;
            }
            catch { return null; }
        }

        public static void PlayStation(RadioStation st)
        {
            if (st == null || string.IsNullOrWhiteSpace(st.Url)) return;
            _clickedStation = st;
            _triedUrls.Clear();
            StopCrossfade();
            Start(st);
        }

        /// <summary>Start stacji: najpierw własny bufor (MP3/AAC), a gdy się nie da - zwykłe odtwarzanie Windows.</summary>
        private static async void Start(RadioStation st)
        {
            // zawsze w wątku interfejsu (wołane też ze zdarzeń odtwarzacza z innych wątków)
            var ui = System.Windows.Application.Current?.Dispatcher;
            if (ui != null && !ui.CheckAccess()) { ui.BeginInvoke(new Action(() => Start(st))); return; }
            SaveGain();
            _list = null;
            _tracks.Clear();
            HookSession();
            StopLive();
            _icy.Stop();
            _triedUrls.Add(st.Url);
            SavePodcastPosition();
            CurrentEpisode = null;
            CurrentStation = st;
            CurrentTrack = null;
            PlayingPreview = false;
            StreamTitle = null;
            ConnectSeconds = 0;
            Dropouts = 0;
            Buffering = true;
            _radioToken++;
            int token = _radioToken;
            _logoFile = null;
            NowPlaying = st.Name;
            _connectWatch = System.Diagnostics.Stopwatch.StartNew();
            _lastReconnect = DateTime.MinValue;
            _bufferingSince = null;
            _ = LoadLogo(st);
            RememberRecent(st);
            Changed?.Invoke();

            MediaSource source = null;
            if (CanBuffer(st))
            {
                var live = new LiveRadioStream(st.Url, st.Bitrate);
                live.TitleChanged += t => { if (token == _radioToken) { StreamTitle = t; ApplyRadioDisplay(); Changed?.Invoke(); } };
                live.Failed += () => { if (token == _radioToken) TryAlternate(); };
                _pendingLive = live;
                bool ok = await live.StartAsync();
                if (ReferenceEquals(_pendingLive, live)) _pendingLive = null;
                if (token != _radioToken) { live.Stop(); return; }     // w międzyczasie wybrano inną stację
                if (ok)
                {
                    _live = live;
                    source = MediaSource.CreateFromStream(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(live), live.ContentType);
                }
                else
                {
                    live.Stop();
                    Log.Info("radio: własny bufor nie ruszył, zwykłe odtwarzanie - " + st.Name);
                }
            }
            if (source == null)
            {
                source = MediaSource.CreateFromUri(new Uri(st.Url));
                _icy.Start(st.Url);
            }

            var item = new MediaPlaybackItem(source);
            _radioItem = item;
            ApplyRadioDisplay();
            Player.Source = item;
            Player.Play();
            StartLeveler(st);
            RadioService.RegisterClick(st.Uuid);
        }

        /// <summary>Własny bufor obsługuje zwykłe strumienie MP3/AAC (większość stacji). HLS i playlisty - odtwarzacz Windows.</summary>
        private static bool CanBuffer(RadioStation st)
        {
            if (st.Hls || _noBuffer.Contains(st.Url)) return false;
            string u = st.Url.ToLowerInvariant().Split('?')[0];
            if (u.EndsWith(".m3u8") || u.EndsWith(".m3u") || u.EndsWith(".pls") || u.EndsWith(".asx") || u.EndsWith(".xspf")) return false;
            string c = st.Codec.ToUpperInvariant();
            return c.Length == 0 || c.Contains("MP3") || c.Contains("AAC");
        }

        private static void StopLive()
        {
            try { _pendingLive?.Stop(); } catch { }
            _pendingLive = null;
            try { _live?.Stop(); } catch { }
            _live = null;
        }

        /// <summary>
        /// Stacja nie działa pod tym adresem - szukamy innego adresu tej samej stacji w bazie
        /// (wiele stacji ma kilka strumieni / serwerów zapasowych) i przełączamy się bez klikania.
        /// </summary>
        private static async void TryAlternate()
        {
            var cur = CurrentStation;
            var clicked = _clickedStation;
            if (cur == null || clicked == null) return;
            int token = _radioToken;
            try
            {
                var found = await RadioService.Search(clicked.Name);
                if (token != _radioToken) return;
                RadioStation best = null;
                foreach (var v in found)
                {
                    if (!string.Equals(v.Name, clicked.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (clicked.CountryCode.Length > 0 && v.CountryCode != clicked.CountryCode) continue;
                    if (_triedUrls.Contains(v.Url) || !v.LastCheckOk) continue;
                    // najpierw niezawodność, potem jakość
                    if (best == null || v.Signal > best.Signal || (v.Signal == best.Signal && v.SoundQuality > best.SoundQuality)) best = v;
                }
                if (best == null) { Log.Info("radio: brak zapasowego adresu dla " + clicked.Name); return; }
                Log.Info("radio: przełączam na zapasowy adres " + best.Url);
                best.Favicon = string.IsNullOrEmpty(best.Favicon) ? clicked.Favicon : best.Favicon;
                Start(best);
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        /// <summary>Tytuł w wyspie: aktualna piosenka (gdy stacja ją podaje) albo nazwa stacji.</summary>
        private static void ApplyRadioDisplay()
        {
            var st = CurrentStation;
            var item = _radioItem;
            if (st == null || item == null) return;
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            string artist = null, title = null;
            if (!string.IsNullOrWhiteSpace(StreamTitle))
            {
                int dash = StreamTitle.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0) { artist = StreamTitle.Substring(0, dash).Trim(); title = StreamTitle.Substring(dash + 3).Trim(); }
                else title = StreamTitle;
            }
            props.MusicProperties.Title = title ?? st.Name;
            props.MusicProperties.Artist = artist ?? (title != null ? st.Name : L.T("Radio", "Radio") + (string.IsNullOrWhiteSpace(st.Country) ? "" : " · " + st.Country));
            props.MusicProperties.AlbumTitle = st.Name;
            // grafika: kafelek z logo stacji. Z PLIKU - wyspa czyta okładkę z innego procesu (Windows),
            // a strumień z pamięci programu albo link do internetu nie zawsze da się tam otworzyć.
            if (_logoFile != null) props.Thumbnail = RandomAccessStreamReference.CreateFromFile(_logoFile);
            item.ApplyDisplayProperties(props);
        }

        private static StorageFile _logoFile;
        /// <summary>Plik z kafelkiem logo grającej stacji (do kolorów tła okna).</summary>
        public static string LogoPath => CurrentStation != null ? _logoFile?.Path : null;
        private static int _radioToken;

        static LocalPlayer()
        {
            _icy.TitleChanged += t =>
            {
                StreamTitle = t;
                ApplyRadioDisplay();
                Changed?.Invoke();
            };
        }

        private static async Task LoadLogo(RadioStation st)
        {
            int token = _radioToken;
            try
            {
                string path = await RadioArt.LogoFile(st);
                if (token != _radioToken || path == null) return;
                var file = await StorageFile.GetFileFromPathAsync(path);
                if (token != _radioToken) return;
                _logoFile = file;
                ApplyRadioDisplay();
                Changed?.Invoke();
            }
            catch (Exception ex) { Log.Info("radio logo: " + ex.Message); }
        }

        private static void HookSession()
        {
            if (_sessionHooked) return;
            _sessionHooked = true;
            var session = Player.PlaybackSession;
            session.PlaybackStateChanged += (s, e) =>
            {
                if (s.PlaybackState == MediaPlaybackState.Playing && CurrentStation != null && ConnectSeconds <= 0 && _connectWatch != null)
                {
                    ConnectSeconds = _connectWatch.Elapsed.TotalSeconds;
                    Buffering = false;
                }
                Changed?.Invoke();      // ikonka pauzy w oknie radia
            };
            // przerwa liczy się dopiero, gdy buforowanie trwa ponad 0,8 s (krótkie mignięcia są niesłyszalne)
            session.BufferingStarted += (s, e) =>
            {
                if (CurrentStation == null || ConnectSeconds <= 0) return;
                _bufferingSince = DateTime.UtcNow;
                _countedThisStall = false;
                Buffering = true;
                _ = WatchStall(++_stallId);
            };
            session.BufferingEnded += (s, e) =>
            {
                _bufferingSince = null;
                if (Buffering) { Buffering = false; Changed?.Invoke(); }
            };
        }

        private static DateTime? _bufferingSince;
        private static bool _countedThisStall;
        private static int _stallId;
        private static DateTime _lastReconnect = DateTime.MinValue;

        /// <summary>Pilnuje zawieszenia: po 0,8 s liczy przerwę, po 7 s łączy od nowa (stacja się „zacięła”).</summary>
        private static async Task WatchStall(int id)
        {
            await Task.Delay(800);
            if (id != _stallId || _bufferingSince == null) return;
            if (!_countedThisStall) { _countedThisStall = true; Dropouts++; Changed?.Invoke(); }
            await Task.Delay(6200);
            if (id != _stallId || _bufferingSince == null) return;
            Reconnect();
        }

        /// <summary>Ponowne połączenie z tą samą stacją (nie częściej niż co 10 s).</summary>
        private static void Reconnect(bool force = false)
        {
            var st = CurrentStation;
            if (st == null) return;
            // własny bufor sam odnawia połączenie w tle - restart całości tylko na wyraźne żądanie
            if (_live != null && !force) return;
            if (!force && (DateTime.UtcNow - _lastReconnect).TotalSeconds < 10) return;
            _lastReconnect = DateTime.UtcNow;
            Log.Info("radio: ponowne łączenie " + st.Name);
            int dropouts = Dropouts;
            _triedUrls.Remove(st.Url);
            Start(st);
            Dropouts = dropouts;
        }

        /// <summary>Pauza / wznowienie (radio po pauzie łączy się od nowa, żeby grać „na żywo”, a nie z opóźnieniem).</summary>
        public static void TogglePause()
        {
            if (_player == null) return;
            if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
            else if (CurrentStation != null) Reconnect(force: true);     // radio po pauzie: znowu na żywo
            else _player.Play();
            Changed?.Invoke();
        }

        public static bool IsPlaying => _player != null && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        /// <summary>Nasz odtwarzacz ma coś załadowanego (plik albo radio).</summary>
        public static bool Active => _player != null && _player.Source != null;

        public static bool SkipNext()
        {
            if (CurrentEpisode != null && _player != null) { _player.PlaybackSession.Position += TimeSpan.FromSeconds(30); return true; }   // podcast: +30 s
            if (_list == null) return false;
            try { _list.MoveNext(); Changed?.Invoke(); return true; } catch { return false; }
        }

        /// <summary>Jak w każdym odtwarzaczu: po 3 s utworu „wstecz” wraca na początek, wcześniej - do poprzedniego.</summary>
        public static bool SkipPrevious()
        {
            if (CurrentEpisode != null && _player != null)       // podcast: -15 s
            {
                var p = _player.PlaybackSession.Position - TimeSpan.FromSeconds(15);
                _player.PlaybackSession.Position = p < TimeSpan.Zero ? TimeSpan.Zero : p;
                return true;
            }
            if (_list == null) return false;
            try
            {
                if (_player.PlaybackSession.Position.TotalSeconds > 3) _player.PlaybackSession.Position = TimeSpan.Zero;
                else _list.MovePrevious();
                Changed?.Invoke();
                return true;
            }
            catch { return false; }
        }

        public static bool SeekTo(double seconds)
        {
            if (_list == null || _player == null) return false;     // radia nie przewijamy
            try { _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds)); return true; }
            catch { return false; }
        }

        // ================= głośność ustawiana z wyspy =================
        private static double _userVolume = -1;
        private static System.Windows.Threading.DispatcherTimer _volSave;

        /// <summary>
        /// Głośność naszego odtwarzacza (0-1) - zmienia ją kółko / suwak w wyspie, tak jak głośność Spotify.
        /// Przy radiu mnoży się z wyrównaniem głośności stacji. Zapamiętywana między uruchomieniami.
        /// </summary>
        public static double UserVolume
        {
            get
            {
                if (_userVolume < 0) _userVolume = Math.Clamp(App.Config?.LocalVolume ?? 1, 0, 1);
                return _userVolume;
            }
            set
            {
                _userVolume = Math.Clamp(value, 0, 1);
                if (_player != null)
                {
                    double g = CurrentStation != null ? _gain : 1;
                    try { _player.Volume = Math.Clamp(_userVolume * g, 0, 1); } catch { }
                }
                // zapis do ustawień chwilę po ostatniej zmianie (nie przy każdym ruchu kółka)
                var ui = System.Windows.Application.Current?.Dispatcher;
                if (ui == null) return;
                ui.BeginInvoke(new Action(() =>
                {
                    if (_volSave == null)
                    {
                        _volSave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                        _volSave.Tick += (s, e) =>
                        {
                            _volSave.Stop();
                            App.Config.LocalVolume = Math.Round(UserVolume, 3);
                            App.Config.Save();
                        };
                    }
                    _volSave.Stop();
                    _volSave.Start();
                }));
            }
        }

        // ================= wyrównywanie głośności stacji =================
        // Każda stacja nadaje inaczej głośno. Mierzymy, jak głośno gra (szczyt na wyjściu podzielony przez
        // głośność naszego odtwarzacza) i ściszamy głośne stacje do wspólnego poziomu. Wynik pamiętamy
        // dla każdej stacji, więc przy następnym włączeniu od razu gra równo.
        private const double TargetLevel = 0.42;
        private static System.Windows.Threading.DispatcherTimer _leveler;
        private static double _stationLevel, _measured, _gain = 1;
        private static DateTime _levelStart;

        private static void StartLeveler(RadioStation st)
        {
            _stationLevel = 0;
            _measured = 0;
            _levelStart = DateTime.UtcNow;
            var gains = App.Config.RadioGains ??= new Dictionary<string, double>();
            _gain = gains.TryGetValue(StationKey(st), out double g) ? g : 0.75;
            Player.Volume = Math.Clamp(UserVolume * _gain, 0, 1);
            if (_leveler == null)
            {
                _leveler = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, System.Windows.Application.Current.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(100),
                };
                _leveler.Tick += (s, e) => LevelTick();
            }
            _leveler.Start();
        }

        private static void StopLeveler()
        {
            _leveler?.Stop();
            if (_player != null)
            {
                _player.Volume = UserVolume;
                try { _player.PlaybackSession.PlaybackRate = 1; } catch { }
            }
        }

        private static void LevelTick()
        {
            var st = CurrentStation;
            if (st == null || _player == null) { _leveler?.Stop(); return; }
            if (!IsPlaying || Buffering || ConnectSeconds <= 0) return;
            BuildCushion();
            const double dt = 0.1;
            float peak = App.Island?.ReadPeak() ?? 0;
            double vol = Math.Max(0.05, _player.Volume);
            if (peak > 0.003)
            {
                double level = Math.Min(2, peak / vol);
                // głośne fragmenty łapiemy szybko, ciche wolno (cicha zapowiedź nie podgłaśnia od razu)
                double tau = level > _stationLevel ? 1.5 : 8;
                _stationLevel = _stationLevel <= 0 ? level : _stationLevel + (level - _stationLevel) * Math.Min(1, dt / tau);
                _measured += dt;
            }
            if (_measured < 2) return;
            double target = Math.Clamp(TargetLevel / _stationLevel, 0.2, 1.0);
            // na początku szybciej, potem bardzo łagodnie (bez „pompowania”)
            double rate = (DateTime.UtcNow - _levelStart).TotalSeconds < 8 ? 1.2 : 0.25;
            double ln = Math.Log(_gain), lt = Math.Log(target), step = rate * dt;
            _gain = Math.Exp(ln + Math.Clamp(lt - ln, -step, step));
            double want = Math.Clamp(UserVolume * _gain, 0, 1);
            if (Math.Abs(_player.Volume - want) > 0.003) _player.Volume = want;
            if (_measured > 6 && ((int)(_measured * 10)) % 50 == 0)
            {
                (App.Config.RadioGains ??= new Dictionary<string, double>())[StationKey(st)] = Math.Round(_gain, 3);
            }
        }

        /// <summary>Zapamiętuje poziom poprzedniej stacji (przy zmianie stacji / zatrzymaniu).</summary>
        private static void SaveGain()
        {
            var st = CurrentStation;
            if (st == null || _measured < 4) return;
            (App.Config.RadioGains ??= new Dictionary<string, double>())[StationKey(st)] = Math.Round(_gain, 3);
            App.Config.Save();
        }

        private static string StationKey(RadioStation st) => st.Uuid.Length > 0 ? st.Uuid : st.Url;

        /// <summary>
        /// Szybki start ma mały zapas - dobudowujemy go do 10 s, grając minimalnie wolniej (3%, niesłyszalne),
        /// bo radio na żywo nie da się „przewinąć do przodu”, a zatrzymanie dźwięku byłoby słychać.
        /// </summary>
        private static void BuildCushion()
        {
            var live = _live;
            var session = _player?.PlaybackSession;
            if (session == null) return;
            double want = 1.0;
            if (live != null)
            {
                double lead = live.BufferedSeconds;
                if (lead < LiveRadioStream.GoalSeconds - 0.5) want = 0.97;
                else if (lead < LiveRadioStream.GoalSeconds && Math.Abs(session.PlaybackRate - 0.97) < 0.001) want = 0.97;
            }
            if (Math.Abs(session.PlaybackRate - want) > 0.001)
            {
                try { session.PlaybackRate = want; } catch { }
            }
        }

        // ---- ulubione i ostatnie (w ustawieniach) ----
        public static bool IsFavorite(RadioStation st) =>
            st != null && (App.Config.RadioFavorites ?? new List<RadioStation>()).Exists(x => Same(x, st));

        public static void ToggleFavorite(RadioStation st)
        {
            if (st == null) return;
            var fav = App.Config.RadioFavorites ??= new List<RadioStation>();
            int i = fav.FindIndex(x => Same(x, st));
            if (i >= 0) fav.RemoveAt(i); else fav.Insert(0, st);
            App.Config.Save();
            Changed?.Invoke();
        }

        private static void RememberRecent(RadioStation st)
        {
            var rec = App.Config.RadioRecent ??= new List<RadioStation>();
            rec.RemoveAll(x => Same(x, st));
            rec.Insert(0, st);
            if (rec.Count > 25) rec.RemoveRange(25, rec.Count - 25);
            App.Config.Save();
        }

        private static bool Same(RadioStation a, RadioStation b) =>
            (a.Uuid.Length > 0 && a.Uuid == b.Uuid) || string.Equals(a.Url, b.Url, StringComparison.OrdinalIgnoreCase);

        public static void Stop()
        {
            CurrentTrack = null;
            SaveGain();
            StopLeveler();
            StopLive();
            _radioToken++;
            _icy.Stop();
            CurrentStation = null;
            StreamTitle = null;
            _radioItem = null;
            if (_player == null) { Changed?.Invoke(); return; }
            _player.Pause();
            _player.Source = null;
            _list = null;
            _tracks.Clear();
            NowPlaying = null;
            Changed?.Invoke();
        }

        public static void Dispose()
        {
            SavePodcastPosition();
            try { PodcastService.Flush(); } catch { }
            try { _player?.Dispose(); } catch { }
            _player = null;
        }
    }
}
