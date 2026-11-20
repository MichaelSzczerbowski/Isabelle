using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media;
using Windows.Media.Control;

namespace IslandPlayer
{
    /// <summary>Stan odtwarzania w danej chwili (niezmienny obiekt - bezpieczny między wątkami).</summary>
    public sealed class MediaSnapshot
    {
        public string Key = "";
        public string Title = "";
        public string Artist = "";
        public string AppId = "";
        public bool Playing;
        public double Position;      // sekundy w chwili At
        public double Duration;      // sekundy (0 = nieznana)
        public DateTime At;          // UTC
        public int Repeat = -1;      // -1 = odtwarzacz nie obsługuje, 0 brak, 1 utwór, 2 lista
        public List<string> Apps = new List<string>();

        public double PositionNow(DateTime nowUtc)
        {
            double p = Position + (Playing ? (nowUtc - At).TotalSeconds : 0);
            return Duration > 0 ? Math.Clamp(p, 0, Duration) : Math.Max(0, p);
        }
    }

    /// <summary>
    /// Multimedia Windows (to, co pokazuje systemowe okienko głośności): Spotify, YouTube w przeglądarce,
    /// Apple Music itd. Działa w tle; wyniki oddaje przez zdarzenia (wywoływane poza wątkiem interfejsu).
    /// </summary>
    public sealed class MediaService
    {
        public event Action<MediaSnapshot> Updated;                 // null = nic nie gra
        public event Action<string, BitmapSource, Color> CoverReady; // klucz utworu, okładka, kolor akcentu

        public string SelectedApp { get; private set; }

        private GlobalSystemMediaTransportControlsSessionManager _mgr;
        private GlobalSystemMediaTransportControlsSession _current;
        private GlobalSystemMediaTransportControlsSession _subscribed;
        private readonly SemaphoreSlim _poke = new SemaphoreSlim(0);
        private readonly object _tlLock = new object();
        private List<string> _apps = new List<string>();

        // "kotwica" czasu - Spotify aktualizuje pozycję rzadko, więc liczymy od chwili prawdziwej zmiany
        private string _tlKey;
        private double _rep = -999;
        private DateTimeOffset? _lu;
        private double _anchorPos;
        private DateTime _anchorT = DateTime.UtcNow;
        private bool _tlPlaying;

        // okładki: kilka prób, odrzucanie okładki poprzedniego utworu
        private sealed class CoverState { public DateTime Start; public int Tries; public ulong? Crc; public int Same; public bool Got; public bool OnlineTried; public volatile bool OnlineDone; }
        private readonly Dictionary<string, CoverState> _covers = new Dictionary<string, CoverState>();
        private static readonly double[] CoverSchedule = { 0, 0.8, 2, 4, 7, 11, 16 };
        private ulong? _curCrc, _prevCrc;
        private string _curCrcKey, _prevCrcKey;

        public async Task StartAsync()
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.SessionsChanged += (s, a) => Poke();
            _mgr.CurrentSessionChanged += (s, a) => Poke();
            _ = Task.Run(LoopAsync);
        }

        public void Poke()
        {
            if (_poke.CurrentCount == 0) _poke.Release();
        }

        private async Task LoopAsync()
        {
            while (true)
            {
                try { await PollOnceAsync(); }
                catch (Exception ex) { Log.Error(ex); }
                await _poke.WaitAsync(500);
            }
        }

        private GlobalSystemMediaTransportControlsSession ChooseSession()
        {
            var list = new List<string>();
            GlobalSystemMediaTransportControlsSession chosen = null;
            try
            {
                foreach (var s in _mgr.GetSessions())
                {
                    string id = s.SourceAppUserModelId ?? "";
                    list.Add(id);
                    if (SelectedApp != null && id == SelectedApp) chosen = s;
                }
            }
            catch { }
            _apps = list;
            return chosen ?? _mgr.GetCurrentSession();
        }

        private void Subscribe(GlobalSystemMediaTransportControlsSession s)
        {
            if (ReferenceEquals(s, _subscribed)) return;
            try
            {
                if (_subscribed != null)
                {
                    _subscribed.MediaPropertiesChanged -= OnProps;
                    _subscribed.PlaybackInfoChanged -= OnPlayback;
                    _subscribed.TimelinePropertiesChanged -= OnTimeline;
                }
            }
            catch { }
            _subscribed = s;
            try
            {
                s.MediaPropertiesChanged += OnProps;
                s.PlaybackInfoChanged += OnPlayback;
                s.TimelinePropertiesChanged += OnTimeline;
            }
            catch { }
        }

        private void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) => Poke();
        private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) => Poke();
        private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) => Poke();

        private async Task PollOnceAsync()
        {
            if (_mgr == null) return;
            var session = ChooseSession();
            _current = session;
            if (session == null)
            {
                Updated?.Invoke(null);
                return;
            }
            Subscribe(session);

            var props = await session.TryGetMediaPropertiesAsync();
            var pi = session.GetPlaybackInfo();
            bool playing = pi != null && pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            string appId = session.SourceAppUserModelId ?? "";
            var snap = new MediaSnapshot
            {
                Title = title,
                Artist = artist,
                // aplikacja w kluczu: ten sam tytuł w Chrome i w Spotify to dwa różne "utwory" (inne okładki)
                Key = appId + "\u0001" + title + "\u0001" + artist,
                AppId = appId,
                Playing = playing,
                Apps = new List<string>(_apps),
            };
            try
            {
                if (pi != null && pi.Controls != null && pi.Controls.IsRepeatEnabled && pi.AutoRepeatMode.HasValue)
                    snap.Repeat = (int)pi.AutoRepeatMode.Value;
            }
            catch { }

            ReadTimeline(session, snap);
            Updated?.Invoke(snap);

            if (props != null && title.Length > 0)
                await MaybeFetchCoverAsync(props, snap.Key, snap.AppId);
        }

        private void ReadTimeline(GlobalSystemMediaTransportControlsSession session, MediaSnapshot snap)
        {
            var tl = session.GetTimelineProperties();
            double rep = tl.Position.TotalSeconds;
            double dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            DateTimeOffset lu = tl.LastUpdatedTime;
            bool luValid = lu.Year > 2000;
            DateTime now = DateTime.UtcNow;
            bool playing = snap.Playing;
            string key = snap.Key;

            lock (_tlLock)
            {
                bool fresh = key != _tlKey || Math.Abs(rep - _rep) > 0.01 || (luValid && lu != _lu);
                if (fresh)
                {
                    DateTime anchorT = now;
                    if (luValid)
                    {
                        DateTime lut = lu.UtcDateTime;
                        if (lut > now.AddHours(-1) && lut <= now.AddSeconds(0.5)) anchorT = lut;
                    }
                    double est = rep + (playing ? (now - anchorT).TotalSeconds : 0);
                    double prev = _anchorPos + (_tlPlaying ? (now - _anchorT).TotalSeconds : 0);
                    // odtwarzacz czasem podaje starą pozycję - nie cofaj o mały kawałek
                    if (key == _tlKey && playing && _tlPlaying && est - prev > -1.2 && est - prev < 0) est = prev;
                    _tlKey = key;
                    _rep = rep;
                    _lu = luValid ? lu : (DateTimeOffset?)null;
                    _anchorPos = est;
                    _anchorT = now;
                    _tlPlaying = playing;
                }
                else if (playing != _tlPlaying)
                {
                    double cur = _anchorPos + (_tlPlaying ? (now - _anchorT).TotalSeconds : 0);
                    _anchorPos = cur;
                    _anchorT = now;
                    _tlPlaying = playing;
                }
                double pos = _anchorPos + (playing ? (now - _anchorT).TotalSeconds : 0);
                snap.Duration = dur > 1 ? dur : 0;
                snap.Position = snap.Duration > 0 ? Math.Clamp(pos, 0, snap.Duration) : Math.Max(0, pos);
                snap.At = now;
            }
        }

        private async Task MaybeFetchCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props, string key, string appId)
        {
            if (_curCrcKey != null && key != _curCrcKey)
            {
                _prevCrc = _curCrc;
                _prevCrcKey = _curCrcKey;
                _curCrc = null;
                _curCrcKey = null;
            }
            DateTime now = DateTime.UtcNow;
            if (!_covers.TryGetValue(key, out var st) || (!st.Got && (now - st.Start).TotalSeconds > 20 && st.Tries >= CoverSchedule.Length))
            {
                if (_covers.Count > 80) _covers.Clear();
                st = new CoverState { Start = now };
                _covers[key] = st;
            }
            if (st.Same >= 1 || st.Tries >= CoverSchedule.Length) return;

            // Spotify dokleja do okładki pasek z logo - najpierw szukamy czystej okładki w internecie
            // (w tle, żeby nie blokować odświeżania paska postępu; miniaturę od Spotify bierzemy dopiero, gdy się nie uda)
            bool spotify = (appId ?? "").IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0;
            if (spotify && !st.OnlineTried)
            {
                st.OnlineTried = true;
                string t = props.Title ?? "", a = props.Artist ?? "";
                var state = st;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var online = await CoverService.FindAsync(t, a);
                        if (online != null)
                        {
                            state.Got = true;
                            state.Same = 99; // mamy czystą okładkę - miniatura od Spotify nie jest już potrzebna
                            CoverReady?.Invoke(key, online.Value.bmp, online.Value.accent);
                        }
                    }
                    catch (Exception ex) { Log.Error(ex); }
                    finally
                    {
                        state.OnlineDone = true;
                        Poke();
                    }
                });
            }
            if (spotify && !st.OnlineDone) return;
            if (st.Same >= 1) return;

            if ((now - st.Start).TotalSeconds < CoverSchedule[st.Tries]) return;
            st.Tries++;
            if (props.Thumbnail == null) return;

            byte[] data;
            using (var ras = await props.Thumbnail.OpenReadAsync())
            using (var stream = ras.AsStreamForRead())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                data = ms.ToArray();
            }
            if (data.Length == 0) return;

            ulong crc = ImageUtil.Hash(data);
            bool stale = _prevCrc.HasValue && crc == _prevCrc.Value && _prevCrcKey != key;
            if (stale && st.Tries >= 4) stale = false; // ta sama płyta = ta sama okładka
            if (stale) return;
            if (st.Crc.HasValue && st.Crc.Value == crc) st.Same++;
            st.Crc = crc;
            _curCrc = crc;
            _curCrcKey = key;

            var (bmp, accent) = ImageUtil.ProcessCover(data, appId);
            if (st.Same >= 99) return; // w międzyczasie przyszła czysta okładka z internetu
            if (bmp != null)
            {
                st.Got = true;
                CoverReady?.Invoke(key, bmp, accent);
            }
        }

        // ---------- polecenia (wywoływane z interfejsu) ----------
        private void Run(Func<GlobalSystemMediaTransportControlsSession, Task> action)
        {
            var s = _current;
            if (s == null) return;
            Task.Run(async () =>
            {
                try { await action(s); }
                catch (Exception ex) { Log.Error(ex); }
                Poke();
            });
        }

        public void PlayPause() => Run(async s => await s.TryTogglePlayPauseAsync());
        public void Next() => Run(async s => await s.TrySkipNextAsync());
        public void Previous() => Run(async s => await s.TrySkipPreviousAsync());

        public void SetRepeat(int mode) =>
            Run(async s => await s.TryChangeAutoRepeatModeAsync((MediaPlaybackAutoRepeatMode)mode));

        public void Seek(double seconds)
        {
            lock (_tlLock)
            {
                _anchorPos = seconds;     // od razu przestaw "kotwicę" - stara pozycja jej nie cofnie
                _anchorT = DateTime.UtcNow;
            }
            Run(async s => await s.TryChangePlaybackPositionAsync((long)(seconds * 10_000_000)));
        }

        public void CycleApp()
        {
            var apps = _apps;
            if (apps.Count < 2) return;
            string cur = _current?.SourceAppUserModelId;
            int i = apps.IndexOf(cur);
            SelectedApp = apps[(i + 1) % apps.Count];
            Poke();
        }

        public static string PrettyAppName(string appId)
        {
            string a = (appId ?? "").Split('!')[0];
            if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a.Substring(0, a.Length - 4);
            if (a.Contains("_") && a.Contains("."))
            {
                a = a.Split('_')[0];
                a = a.Substring(a.LastIndexOf('.') + 1);
            }
            switch (a.ToLowerInvariant())
            {
                case "spotify": return "Spotify";
                case "msedge": return "Edge";
                case "chrome": return "Chrome";
                case "firefox": return "Firefox";
                case "opera": return "Opera";
                case "brave": return "Brave";
                case "zunemusic": return "Groove";
                case "vlc": return "VLC";
                case "applemusic": return "Apple Music";
            }
            return a.Length > 0 ? char.ToUpper(a[0]) + a.Substring(1) : L.T("Odtwarzacz", "Player");
        }
    }
}
