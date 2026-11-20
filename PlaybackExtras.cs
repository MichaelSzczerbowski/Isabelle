using System;
using System.Windows.Threading;

namespace IslandPlayer
{
    /// <summary>
    /// Opcje odtwarzania działające w tle:
    ///  • wyrównywanie głośności - cichsze i głośniejsze utwory grają podobnie,
    ///  • płynne przejścia - wyciszenie końca utworu i rozjaśnienie początku następnego,
    ///  • wygaszacz z muzyką - tryb pełnoekranowy, gdy komputer leży bezczynnie, a coś gra,
    ///  • auto-pauza - gdy zacznie grać inna aplikacja, muzyka czeka, a potem sama wraca.
    /// Głośność zmieniamy tylko samemu odtwarzaczowi (sesja w mikserze), nigdy całemu komputerowi.
    /// </summary>
    public sealed class PlaybackExtras
    {
        private readonly IslandWindow _island;
        private readonly AudioService _audio;
        private readonly MediaService _media;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private DateTime _last = DateTime.UtcNow;

        // głośność, którą wybrał użytkownik, i to, co faktycznie ustawiliśmy
        private float? _userVol;
        private float _applied;
        private double _norm = 1, _fade = 1;

        // pomiar głośności utworu (szczyt podzielony przez ustawioną głośność = „prawdziwa” głośność nagrania)
        private string _key;
        private DateTime _keyAt = DateTime.UtcNow;
        private double _trackLevel, _refLevel, _measured;

        public PlaybackExtras(IslandWindow island, AudioService audio, MediaService media)
        {
            _island = island;
            _audio = audio;
            _media = media;
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            Restore();
        }

        private void Tick()
        {
            var now = DateTime.UtcNow;
            double dt = Math.Min(0.6, (now - _last).TotalSeconds);
            _last = now;
            // 20 razy na sekundę tylko wtedy, gdy coś płynnie zmienia głośność; inaczej 2 razy na sekundę wystarczy
            var c = App.Config;
            var want = TimeSpan.FromMilliseconds((c.NormalizeVolume || c.SmoothTransitions) && _island.Playing ? 50 : 500);
            if (_timer.Interval != want) _timer.Interval = want;
            try
            {
                Screensaver();
                Shape(now, dt);
                if ((now - _lastAutoPause).TotalMilliseconds >= 450) { _lastAutoPause = now; AutoPause(now); }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        // ================= auto-pauza =================
        private DateTime _lastAutoPause = DateTime.MinValue;
        private readonly System.Collections.Generic.Dictionary<string, DateTime> _playingSince = new System.Collections.Generic.Dictionary<string, DateTime>();
        private string _pausedApp;            // aplikacja, którą zatrzymaliśmy
        private DateTime _othersQuietSince = DateTime.MinValue;

        private void AutoPause(DateTime now)
        {
            if (!App.Config.AutoPause)
            {
                _pausedApp = null;
                _playingSince.Clear();
                return;
            }
            var states = _media.SessionStates();
            var playing = new System.Collections.Generic.HashSet<string>();
            foreach (var (app, isPlaying) in states)
                if (isPlaying && app.Length > 0) playing.Add(app);
            foreach (var app in playing)
                if (!_playingSince.ContainsKey(app)) _playingSince[app] = now;
            foreach (var app in new System.Collections.Generic.List<string>(_playingSince.Keys))
                if (!playing.Contains(app)) _playingSince.Remove(app);

            if (_pausedApp == null)
            {
                // gra więcej niż jedna aplikacja: zatrzymujemy tę, która grała wcześniej (nowa wygrywa)
                if (playing.Count < 2) return;
                string oldest = null, newest = null;
                foreach (var kv in _playingSince)
                {
                    if (oldest == null || kv.Value < _playingSince[oldest]) oldest = kv.Key;
                    if (newest == null || kv.Value > _playingSince[newest]) newest = kv.Key;
                }
                if (oldest == null || newest == null || oldest == newest) return;
                if ((now - _playingSince[newest]).TotalSeconds < 1) return;     // krótkie dźwięki nie przerywają muzyki
                _media.PauseApp(oldest);
                _pausedApp = oldest;
                _othersQuietSince = DateTime.MinValue;
                return;
            }

            bool stillThere = false;
            foreach (var (app, _) in states) if (app == _pausedApp) stillThere = true;
            if (!stillThere || playing.Contains(_pausedApp))
            {
                // odtwarzacz zamknięty albo ktoś sam go wznowił - już nie pilnujemy
                _pausedApp = null;
                return;
            }
            if (playing.Count > 0) { _othersQuietSince = DateTime.MinValue; return; }
            if (_othersQuietSince == DateTime.MinValue) _othersQuietSince = now;
            if ((now - _othersQuietSince).TotalSeconds >= 2.5)
            {
                _media.PlayApp(_pausedApp);
                _pausedApp = null;
            }
        }

        // ================= wygaszacz z muzyką =================
        private void Screensaver()
        {
            var c = App.Config;
            if (!c.MusicScreensaver || FullscreenPlayer.IsOpen || !_island.Playing || _island.Snapshot == null) return;
            if (_island.ForegroundFullscreen) return;           // gra albo film na pełnym ekranie - nie przeszkadzamy
            if (Native.IdleSeconds() >= Math.Max(1, c.ScreensaverMinutes) * 60)
                FullscreenPlayer.Open(screensaver: true);
        }

        // ================= głośność =================
        private void Shape(DateTime now, double dt)
        {
            var c = App.Config;
            var snap = _island.Snapshot;
            bool on = (c.NormalizeVolume || c.SmoothTransitions) && snap != null && _audio.IsAppVolume;
            if (!on)
            {
                Restore();
                return;
            }
            // pauza z wyciszeniem albo ściszenie przy rozmowie - wtedy nie ruszamy głośności
            if (_audio.Fading || _island.MicBusy) return;

            float cur = _audio.Volume;
            if (_userVol == null)
            {
                _userVol = cur;
                _applied = cur;
            }
            else if (Math.Abs(cur - _applied) > 0.015f)
            {
                // ktoś zmienił głośność (kółko, mikser, Spotify) - to nowa głośność użytkownika
                _userVol = (float)Math.Clamp(cur / Math.Max(0.05, _norm * _fade), 0, 1);
                _applied = cur;
            }

            if (snap.Key != _key)
            {
                _key = snap.Key;
                _keyAt = now;
                _trackLevel = 0;
                _measured = 0;
            }
            double sinceStart = (now - _keyAt).TotalSeconds;
            bool playing = _island.Playing;

            // ---- wyrównywanie ----
            double normTarget = 1;
            if (c.NormalizeVolume)
            {
                float peak = _audio.ReadPeak();
                if (playing && _applied > 0.04f && peak > 0.003f && sinceStart > 0.8)
                {
                    double level = Math.Min(1.5, peak / _applied);
                    // szybko w górę (głośny fragment), wolno w dół (cichy fragment nie podkręca od razu)
                    double tau = level > _trackLevel ? 2.5 : 12;
                    _trackLevel = _trackLevel <= 0 ? level : _trackLevel + (level - _trackLevel) * Math.Min(1, dt / tau);
                    _measured += dt;
                }
                if (_measured > 3 && _trackLevel > 0.01)
                {
                    // poziom odniesienia: średnia z ostatnich kilkunastu minut słuchania
                    _refLevel = _refLevel <= 0 ? _trackLevel : _refLevel + (_trackLevel - _refLevel) * Math.Min(1, dt / 600);
                    normTarget = Math.Clamp(_refLevel / _trackLevel, 0.55, 1.5);
                }
                else normTarget = _norm;         // jeszcze mierzymy - trzymamy poprzednie ustawienie
            }
            // zmiana powolna (w skali logarytmicznej), żeby nie było „pompowania”
            double lnNow = Math.Log(_norm), lnTarget = Math.Log(normTarget);
            double step = 0.35 * dt;
            _norm = Math.Exp(lnNow + Math.Clamp(lnTarget - lnNow, -step, step));

            // ---- płynne przejścia ----
            double fadeTarget = 1;
            if (c.SmoothTransitions && playing)
            {
                const double outSec = 3.0, inSec = 1.6, floor = 0.15;
                double pos = _island.Position, dur = snap.Duration;
                if (sinceStart < inSec && pos < inSec + 1) fadeTarget = floor + (1 - floor) * (sinceStart / inSec);
                if (dur > 20 && dur - pos < outSec) fadeTarget = Math.Min(fadeTarget, floor + (1 - floor) * Math.Max(0, dur - pos) / outSec);
            }
            _fade += (fadeTarget - _fade) * Math.Min(1, dt * 12);

            float applied = (float)Math.Clamp(_userVol.Value * _norm * _fade, 0, 1);
            if (Math.Abs(applied - _applied) > 0.002f)
            {
                _audio.Volume = applied;
                _applied = applied;
            }
        }

        /// <summary>Oddaje głośność wybraną przez użytkownika (po wyłączeniu opcji).</summary>
        private void Restore()
        {
            if (_userVol != null && (Math.Abs(_norm - 1) > 0.01 || Math.Abs(_fade - 1) > 0.01))
            {
                try
                {
                    if (_audio.IsAppVolume && !_audio.Fading) _audio.Volume = _userVol.Value;
                }
                catch { }
            }
            _userVol = null;
            _norm = 1;
            _fade = 1;
        }
    }
}
