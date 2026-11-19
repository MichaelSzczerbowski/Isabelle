using System;
using System.Collections.Generic;
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
        private DateTime _lastRefresh = DateTime.MinValue;
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

        public float Volume
        {
            get
            {
                try { return _vol?.MasterVolumeLevelScalar ?? 0; }
                catch { return 0; }
            }
            set
            {
                try
                {
                    if (_vol == null) return;
                    float v = Math.Clamp(value, 0f, 1f);
                    _vol.MasterVolumeLevelScalar = v;
                    if (v > 0 && _vol.Mute) _vol.Mute = false;
                }
                catch { }
            }
        }

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
            if (_vol == null || target <= 0.01f || Muted)
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
