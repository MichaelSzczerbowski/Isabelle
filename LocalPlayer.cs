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

        public static void PlayTracks(IReadOnlyList<LibraryTrack> tracks, int start, bool shuffle)
        {
            if (tracks == null || tracks.Count == 0) return;
            HookSession();     // zdarzenia pauza/gra - dla przycisków w oknie
            SaveGain();
            StopLeveler();
            StopLive();
            _radioToken++;
            _icy.Stop();
            CurrentStation = null;
            StreamTitle = null;
            _radioItem = null;
            var list = new MediaPlaybackList { AutoRepeatEnabled = false, ShuffleEnabled = shuffle, MaxPlayedItemsToKeepOpen = 2 };
            _tracks.Clear();
            MediaPlaybackItem startItem = null;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                // plik otwierany dopiero, gdy jest potrzebny (szybki start nawet przy tysiącach utworów)
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
                var file = await StorageFile.GetFileFromPathAsync(sender.Token);
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

        public static void PlayStation(RadioStation st)
        {
            if (st == null || string.IsNullOrWhiteSpace(st.Url)) return;
            _clickedStation = st;
            _triedUrls.Clear();
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
            CurrentStation = st;
            CurrentTrack = null;
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
            if (_list == null) return false;
            try { _list.MoveNext(); Changed?.Invoke(); return true; } catch { return false; }
        }

        /// <summary>Jak w każdym odtwarzaczu: po 3 s utworu „wstecz” wraca na początek, wcześniej - do poprzedniego.</summary>
        public static bool SkipPrevious()
        {
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
            Player.Volume = _gain;
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
                _player.Volume = 1;
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
            if (Math.Abs(_player.Volume - _gain) > 0.003) _player.Volume = _gain;
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
            try { _player?.Dispose(); } catch { }
            _player = null;
        }
    }
}
