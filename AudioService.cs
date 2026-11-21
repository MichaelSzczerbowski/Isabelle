using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace IslandPlayer
{
    /// <summary>
    /// Głośność systemowa i poziom dźwięku (do słupków i pulsowania okładki).
    /// Wszystko używane wyłącznie z wątku interfejsu - obiekty COM nie są przekazywane między wątkami.
    /// </summary>
    public sealed class AudioService : IDisposable
    {
        /// <summary>Głośność 0..1 i wyciszenie - zmienione przez kogokolwiek (klawiatura, mikser, my).</summary>
        public event Action<float, bool> VolumeChanged;

        private readonly Dispatcher _ui;
        private MMDeviceEnumerator _enum;
        private MMDevice _default;
        private string _defaultId;
        private AudioEndpointVolume _vol;
        private readonly List<MMDevice> _meters = new List<MMDevice>();
        private DateTime _lastRefresh = DateTime.MinValue, _lastMeters = DateTime.MinValue;
        private bool _fading;

        public AudioService(Dispatcher ui)
        {
            _ui = ui;
            try
            {
                _enum = new MMDeviceEnumerator();
                Refresh(true);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        /// <summary>Co 3 s sprawdza, czy nie zmieniło się domyślne urządzenie i lista wyjść (np. podłączone słuchawki).</summary>
        public void Refresh(bool force = false)
        {
            if (_enum == null) return;
            if (!force && (DateTime.UtcNow - _lastRefresh).TotalSeconds < 3) return;
            _lastRefresh = DateTime.UtcNow;
            try
            {
                var dev = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (dev.ID != _defaultId)
                {
                    DetachDefault();
                    _default = dev;
                    _defaultId = dev.ID;
                    _vol = dev.AudioEndpointVolume;
                    _vol.OnVolumeNotification += OnVolumeNotification;
                }
                else
                {
                    dev.Dispose();
                }
            }
            catch
            {
                DetachDefault();
            }

            // lista wyjść (słuchawki, głośniki) odświeżana rzadko - to kosztowne wywołania systemowe
            if (!force && (DateTime.UtcNow - _lastMeters).TotalSeconds < 15) return;
            _lastMeters = DateTime.UtcNow;
            try
            {
                foreach (var m in _meters)
                {
                    try { m.Dispose(); } catch { }
                }
                _meters.Clear();
                foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    _meters.Add(d);
            }
            catch { }
        }

        private void DetachDefault()
        {
            try
            {
                if (_vol != null) _vol.OnVolumeNotification -= OnVolumeNotification;
            }
            catch { }
            try { _default?.Dispose(); } catch { }
            _vol = null;
            _default = null;
            _defaultId = null;
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            if (_fading) return;
            float v = data.MasterVolume;
            bool muted = data.Muted;
            _ui.BeginInvoke(new Action(() => VolumeChanged?.Invoke(v, muted)));
        }

        /// <summary>Najgłośniejszy z aktywnych wyjść (głośniki, słuchawki, HDMI...).</summary>
        public float ReadPeak()
        {
            Refresh();
            float best = 0;
            foreach (var m in _meters)
            {
                try
                {
                    float p = m.AudioMeterInformation.MasterPeakValue;
                    if (p > best) best = p;
                }
                catch { }
            }
            return best;
        }

        // ---------- głośność samego odtwarzacza (jak w mikserze głośności Windows) ----------
        /// <summary>Identyfikator aplikacji, która gra (z multimediów Windows), np. "Spotify.exe".</summary>
        public string TargetApp { get; set; }
        private string _sessionsFor;
        private DateTime _sessionsAt = DateTime.MinValue;
        private readonly List<AudioSessionControl> _sessions = new List<AudioSessionControl>();

        private bool UseApp => App.Config.AppVolume && !string.IsNullOrEmpty(TargetApp);

        /// <summary>
        /// Gra nasz własny odtwarzacz (Moja muzyka / radio) - wtedy wyspa ustawia jego głośność bezpośrednio,
        /// bez szukania sesji w mikserze Windows (to zawsze działa i nie rusza głośności całego komputera).
        /// </summary>
        private bool OwnPlayer => LocalPlayer.Active && (TargetApp ?? "").IndexOf("Isabelle", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Możliwe nazwy procesu dla identyfikatora aplikacji (Spotify.exe, SpotifyAB.SpotifyMusic_...!Spotify, Chrome...).</summary>
        private static List<string> Candidates(string appId)
        {
            var list = new List<string>();
            string a = appId ?? "";
            int bang = a.IndexOf('!');
            if (bang >= 0)
            {
                list.Add(a.Substring(bang + 1));
                string pkg = a.Substring(0, bang).Split('_')[0];
                list.Add(pkg.Substring(pkg.LastIndexOf('.') + 1));
            }
            else
            {
                if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a.Substring(0, a.Length - 4);
                a = a.Substring(Math.Max(a.LastIndexOf('\\'), 0)).TrimStart('\\');
                list.Add(a);
            }
            list.RemoveAll(x => x.Length < 4);
            return list;
        }

        private static bool Matches(string process, List<string> candidates)
        {
            foreach (var c in candidates)
            {
                if (process.Equals(c, StringComparison.OrdinalIgnoreCase)) return true;
                if (process.Length >= 4 && c.IndexOf(process, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (process.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>Sesje dźwięku odtwarzacza na domyślnym wyjściu (odświeżane co 2 s).</summary>
        private List<AudioSessionControl> AppSessions()
        {
            if (_default == null) return _sessions;
            if (_sessionsFor == TargetApp + _defaultId && (DateTime.UtcNow - _sessionsAt).TotalSeconds < 2) return _sessions;
            _sessionsFor = TargetApp + _defaultId;
            _sessionsAt = DateTime.UtcNow;
            foreach (var s in _sessions)
            {
                try { s.Dispose(); } catch { }
            }
            _sessions.Clear();
            try
            {
                var cands = Candidates(TargetApp);
                if (cands.Count == 0) return _sessions;
                var mgr = _default.AudioSessionManager;
                mgr.RefreshSessions();
                var all = mgr.Sessions;
                for (int i = 0; i < all.Count; i++)
                {
                    var s = all[i];
                    bool keep = false;
                    try
                    {
                        if (!s.IsSystemSoundsSession)
                        {
                            using (var p = Process.GetProcessById((int)s.GetProcessID))
                                keep = Matches(p.ProcessName, cands);
                        }
                    }
                    catch { }
                    if (keep) _sessions.Add(s);
                    else
                    {
                        try { s.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            return _sessions;
        }

        /// <summary>Głośność: samego odtwarzacza (gdy da się go znaleźć i opcja jest włączona) albo całego komputera.</summary>
        public float Volume
        {
            get
            {
                try
                {
                    if (OwnPlayer) return (float)LocalPlayer.UserVolume;
                    if (UseApp)
                    {
                        var ss = AppSessions();
                        if (ss.Count > 0)
                        {
                            float max = 0;
                            foreach (var s in ss) max = Math.Max(max, s.SimpleAudioVolume.Mute ? 0 : s.SimpleAudioVolume.Volume);
                            return max;
                        }
                    }
                    return _vol?.MasterVolumeLevelScalar ?? 0;
                }
                catch { return 0; }
            }
            set
            {
                try
                {
                    float v = Math.Clamp(value, 0f, 1f);
                    if (OwnPlayer) { LocalPlayer.UserVolume = v; return; }
                    if (UseApp)
                    {
                        var ss = AppSessions();
                        if (ss.Count > 0)
                        {
                            foreach (var s in ss)
                            {
                                s.SimpleAudioVolume.Volume = v;
                                if (v > 0 && s.SimpleAudioVolume.Mute) s.SimpleAudioVolume.Mute = false;
                            }
                            return;
                        }
                    }
                    if (_vol == null) return;
                    _vol.MasterVolumeLevelScalar = v;
                    if (v > 0 && _vol.Mute) _vol.Mute = false;
                }
                catch { }
            }
        }

        /// <summary>Trwa płynna pauza / wznowienie (wtedy nikt inny nie rusza głośności).</summary>
        public bool Fading => _fading;

        /// <summary>Czy zmiana dotyczy samego odtwarzacza (do podpisu w wyspie).</summary>
        public bool IsAppVolume => OwnPlayer || UseApp && AppSessions().Count > 0;

        public bool Muted
        {
            get
            {
                try { return _vol?.Mute ?? false; }
                catch { return false; }
            }
        }

        /// <summary>Kółko myszy: krok 2%.</summary>
        public float Step(int direction)
        {
            float v = (float)(Math.Round(Volume * 50) + direction) / 50f;
            Volume = v;
            return Math.Clamp(v, 0f, 1f);
        }

        /// <summary>Pauza/wznowienie z płynnym ściszeniem i podgłośnieniem.</summary>
        public async Task FadeToggleAsync(bool playingNow, Action toggle)
        {
            float target = Volume;
            if (target <= 0.01f || (!IsAppVolume && (_vol == null || Muted)))
            {
                toggle();
                return;
            }
            _fading = true;
            try
            {
                if (playingNow)
                {
                    for (int i = 1; i <= 12; i++)
                    {
                        Volume = target * (1 - i / 12f);
                        await Task.Delay(25);
                    }
                    toggle();
                    await Task.Delay(350);
                    Volume = target;
                }
                else
                {
                    Volume = 0.001f;
                    toggle();
                    await Task.Delay(150);
                    for (int i = 1; i <= 16; i++)
                    {
                        float k = i / 16f;
                        Volume = target * k * k;
                        await Task.Delay(25);
                    }
                    Volume = target;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                Volume = target;
            }
            finally
            {
                _fading = false;
            }
        }

        public void Dispose()
        {
            foreach (var s in _sessions)
            {
                try { s.Dispose(); } catch { }
            }
            _sessions.Clear();
            DetachDefault();
            foreach (var m in _meters)
            {
                try { m.Dispose(); } catch { }
            }
            _meters.Clear();
            try { _enum?.Dispose(); } catch { }
            _enum = null;
        }
    }
}
