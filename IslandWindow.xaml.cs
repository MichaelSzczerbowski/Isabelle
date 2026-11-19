using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace IslandPlayer
{
    public partial class IslandWindow : Window
    {
        // sprężyna (jak w iOS) - wartości dobrane w wersji Python
        private const double Stiff = 0.11, Damp = 0.66;

        private readonly MediaService _media = new MediaService();
        private AudioService _audio;
        private IntPtr _hwnd;

        private MediaSnapshot _snap;
        private BitmapSource _cover;
        private string _coverKey;
        // okładki już pobrane - po powrocie do Spotify/Chrome od razu właściwa okładka i kolor
        private readonly Dictionary<string, (BitmapSource bmp, Color accent)> _coverCache = new Dictionary<string, (BitmapSource, Color)>();
        private Color _accent = Color.FromRgb(30, 215, 96);
        private SolidColorBrush _accentBrush = new SolidColorBrush(Color.FromRgb(30, 215, 96));

        // rozmiar wyspy
        private double _w = 44, _h = 30, _vw, _vh;
        private readonly RectangleGeometry _clip = new RectangleGeometry();

        // ruchome tło z okładki
        private readonly ScaleTransform _bgScale1 = new ScaleTransform(), _bgScale2 = new ScaleTransform();
        private readonly RotateTransform _bgRot1 = new RotateTransform(), _bgRot2 = new RotateTransform();
        private readonly TranslateTransform _bgMove1 = new TranslateTransform(), _bgMove2 = new TranslateTransform();
        private double _bgPhase;

        // stan
        private bool _hover, _expanded, _fsHidden;
        private readonly DispatcherTimer _expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private readonly DispatcherTimer _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly DispatcherTimer _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private int _slowTicks;
        private string _toastKind;            // "track" / "vol"
        private DateTime _toastUntil;
        private bool? _playingOverride;        // natychmiastowa zmiana ikony po kliknięciu
        private DateTime _playingOverrideUntil;

        // dźwięk
        private double _peakS, _peakMax;

        // tekst piosenki
        private List<LyricLine> _lyrics;
        private string _lyricsKey;
        private int _lyricIdx = -2;

        // przewijanie
        private bool _dragging;
        private double _dragPos;
        private DateTime _lastSeekSent;
        private double? _holdTarget;
        private DateTime _holdAt, _holdUntil;
        private bool _holdPlaying;

        // pętla animacji
        private bool _loopOn;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastFrame;
        private string _posTextCache, _durTextCache;

        /// <summary>Akcje, których skrótu nie udało się zarejestrować (zajęte przez inny program).</summary>
        public HashSet<string> HotkeyFailures { get; } = new HashSet<string>();

        public IslandWindow()
        {
            InitializeComponent();

            // okno przycinane do samej wyspy, gdy nic się nie dzieje (mniej pracy dla procesora)
            var root = Root;
            Content = null;
            _host.Children.Add(root);
            Content = _host;

            _expandTimer.Tick += (s, e) => { _expandTimer.Stop(); _expanded = true; EnsureLoop(); };
            _collapseTimer.Tick += (s, e) => { _collapseTimer.Stop(); if (!_hover && !_dragging) { _expanded = false; EnsureLoop(); } };
            _slowTimer.Tick += (s, e) => SlowTick();

            Pill.MouseEnter += (s, e) => { _hover = true; _collapseTimer.Stop(); if (!_expanded && !_moving && !App.Config.DotMode) _expandTimer.Start(); };
            Pill.MouseLeave += (s, e) =>
            {
                _hover = false;
                _expandTimer.Stop();
                if (!_dragging) _collapseTimer.Start();
            };
            Pill.MouseLeftButtonDown += PillDown;
            Pill.MouseMove += PillMove;
            Pill.MouseLeftButtonUp += PillUp;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => Dispatcher.BeginInvoke(new Action(PlaceWindow));
            Pill.MouseRightButtonUp += (s, e) => { e.Handled = true; App.ShowSettings(); };
            Pill.MouseWheel += OnWheel;

            PlayBtn.Click += (s, e) => PlayPause();
            NextBtn.Click += (s, e) => _media.Next();
            PrevBtn.Click += (s, e) => _media.Previous();
            AppBtn.Click += (s, e) => _media.CycleApp();
            LyricsBtn.Click += (s, e) => ToggleLyrics();
            OpenSpotifyBtn.Click += (s, e) => OpenSpotify();

            SeekArea.MouseLeftButtonDown += SeekDown;
            SeekArea.MouseMove += SeekMove;
            SeekArea.MouseLeftButtonUp += SeekUp;
            SeekArea.MouseEnter += (s, e) => Thumb.Opacity = 1;
            SeekArea.MouseLeave += (s, e) => { if (!_dragging) Thumb.Opacity = 0; };

            PillInner.Clip = _clip;
            BgCover.RenderTransformOrigin = BgCover2.RenderTransformOrigin = new Point(0.5, 0.5);
            var g1 = new TransformGroup();
            g1.Children.Add(_bgScale1);
            g1.Children.Add(_bgRot1);
            g1.Children.Add(_bgMove1);
            BgCover.RenderTransform = g1;
            var g2 = new TransformGroup();
            g2.Children.Add(_bgScale2);
            g2.Children.Add(_bgRot2);
            g2.Children.Add(_bgMove2);
            BgCover2.RenderTransform = g2;
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
        }

        // ================= start =================
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            RegisterHotkeys();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplySettings();
            _audio = new AudioService(Dispatcher);
            _audio.VolumeChanged += (v, m) => ShowVolume(v, m);

            _media.Updated += snap => Dispatcher.BeginInvoke(new Action(() => OnSnapshot(snap)));
            _media.CoverReady += (key, bmp, color) => Dispatcher.BeginInvoke(new Action(() => OnCover(key, bmp, color)));
            _slowTimer.Start();
            EnsureLoop();
            try
            {
                await _media.StartAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        public void ApplySettings()
        {
            var c = App.Config;
            double sc = Math.Clamp(c.Scale, 0.7, 1.6);
            RootScale.ScaleX = RootScale.ScaleY = sc;
            PlaceWindow();

            RegisterHotkeys();
            if (!c.VolumeOsd) Native.SetVolumeOsdVisible(true);
            if (c.Lyrics && _snap != null && _lyricsKey != _snap.Key) LoadLyrics();
            if (c.CoverAnimation == "off") ArtScale.ScaleX = ArtScale.ScaleY = 1;
            UpdateLyricsButton();
            ApplyTexts();
            // tytuł w małej wyspie: szersza pigułka, napis na środku
            CompactView.Width = c.CompactTitle ? 280 : 180;
            CompactTitleText.Visibility = c.CompactTitle ? Visibility.Visible : Visibility.Collapsed;
            EnsureLoop();
        }

        private void ApplyTexts()
        {
            IdleTitle.Text = L.T("Nic nie gra", "Nothing playing");
            IdleSub.Text = L.T("Włącz muzykę w dowolnej aplikacji", "Play music in any app");
            ToastLabel.Text = ToastVLabel.Text = L.T("TERAZ GRA", "NOW PLAYING");
            LyricsBtn.ToolTip = L.T("Tekst piosenki", "Lyrics");
            AppBtn.ToolTip = L.T("Przełącz odtwarzacz", "Switch player");
            if (_snap != null) AppText.Text = MediaService.PrettyAppName(_snap.AppId);
        }

        private void RegisterHotkeys()
        {
            if (_hwnd == IntPtr.Zero) return;
            foreach (var a in Hotkey.Actions) Native.UnregisterHotKey(_hwnd, a.id);
            HotkeyFailures.Clear();
            if (!App.Config.Hotkeys) return;
            foreach (var a in Hotkey.Actions)
            {
                if (!Hotkey.Parse(App.Config.Shortcut(a.key), out uint mods, out uint vk)) continue;
                if (!Native.RegisterHotKey(_hwnd, a.id, mods | Native.MOD_NOREPEAT, vk)) HotkeyFailures.Add(a.key);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                switch (wParam.ToInt32())
                {
                    case 1: PlayPause(); break;
                    case 2: _media.Next(); break;
                    case 3: _media.Previous(); break;
                    case 4:
                        _expanded = !_expanded;
                        if (_expanded) _collapseTimer.Stop();
                        EnsureLoop();
                        break;
                    case 5:
                    case 6:
                        if (_audio != null)
                        {
                            float v = _audio.Step(wParam.ToInt32() == 5 ? 2 : -2);
                            ShowVolume(v, v <= 0);
                        }
                        break;
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Cleanup()
        {
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    foreach (var a in Hotkey.Actions) Native.UnregisterHotKey(_hwnd, a.id);
                }
                Native.SetVolumeOsdVisible(true);
                _audio?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        // ================= dane z odtwarzacza =================
        private void OnSnapshot(MediaSnapshot snap)
        {
            if (snap != null && string.IsNullOrWhiteSpace(snap.Title)) snap = null;
            string oldKey = _snap?.Key;
            string oldApp = _snap?.AppId;
            bool wasPlaying = _snap?.Playing ?? false;
            _snap = snap;

            if (snap == null)
            {
                EnsureLoop();
                return;
            }

            if (snap.Key != oldKey)
            {
                _holdTarget = null;
                // okładka i kolor zawsze należą do bieżącego utworu - nigdy nie zostają po poprzednim
                if (_coverCache.TryGetValue(snap.Key, out var cached)) ApplyCover(snap.Key, cached.bmp, cached.accent);
                else ClearCover();
                _lyrics = null;
                _lyricsKey = null;
                LyricsStack.Children.Clear();
                if (App.Config.Lyrics) LoadLyrics();
                if (oldKey != null && oldApp == snap.AppId && App.Config.TrackToast && !_expanded)
                {
                    _toastKind = "track";
                    _toastUntil = DateTime.UtcNow.AddSeconds(4);
                }
            }

            // po przewinięciu ufamy wybranej pozycji, aż odtwarzacz ją potwierdzi
            if (_holdTarget.HasValue)
            {
                DateTime now = DateTime.UtcNow;
                double holdNow = _holdTarget.Value + (_holdPlaying ? (now - _holdAt).TotalSeconds : 0);
                if (now > _holdUntil || Math.Abs(snap.PositionNow(now) - holdNow) < 1.2) _holdTarget = null;
            }
            if (_playingOverride.HasValue && (snap.Playing == _playingOverride.Value || DateTime.UtcNow > _playingOverrideUntil))
                _playingOverride = null;

            TitleText.Text = snap.Title;
            ArtistText.Text = snap.Artist;
            ToastTitle.Text = snap.Title;
            CompactTitleText.Text = snap.Title;
            ToastArtist.Text = snap.Artist;
            ToastVTitle.Text = snap.Title;
            ToastVArtist.Text = snap.Artist;
            AppText.Text = MediaService.PrettyAppName(snap.AppId);
            if (_audio != null) _audio.TargetApp = snap.AppId;
            AppBtn.Visibility = snap.Apps.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePlayIcon();
            UpdateLyricsButton();
            if (wasPlaying != snap.Playing) EnsureLoop();
            EnsureLoop();
        }

        // dla okna ustawień (wygląda jak odtwarzacz)
        public Color AccentColor => _accent;
        public BitmapSource CoverImage => _cover;
        public string NowTitle => _snap?.Title;
        public string NowArtist => _snap?.Artist;

        private bool IsPlaying => _playingOverride ?? (_snap?.Playing ?? false);

        private void OnCover(string key, BitmapSource bmp, Color accent)
        {
            if (bmp == null) return;
            if (_coverCache.Count > 60) _coverCache.Clear();
            _coverCache[key] = (bmp, accent);
            if (_snap == null || key != _snap.Key) return;
            ApplyCover(key, bmp, accent);
        }

        private void ApplyCover(string key, BitmapSource bmp, Color accent)
        {
            _cover = bmp;
            _coverKey = key;
            ArtBrush.ImageSource = bmp;
            CompactArtBrush.ImageSource = bmp;
            ToastArtBrush.ImageSource = bmp;
            CompactVArtBrush.ImageSource = bmp;
            ToastVArtBrush.ImageSource = bmp;
            BgCover.Source = bmp;
            BgCover2.Source = bmp;
            SetAccent(accent);
        }

        private void SetAccent(Color c)
        {
            _accent = c;
            _accentBrush = new SolidColorBrush(c);
            _accentBrush.Freeze();
            Glow.Background = _accentBrush;
            ToastLabel.Foreground = _accentBrush;
            ToastVLabel.Foreground = _accentBrush;
            CompactVBars.Fill = _accentBrush;
            CompactBars.Fill = _accentBrush;
            ToastBars.Fill = _accentBrush;
            ExpBars.Fill = _accentBrush;
        }

        private void ClearCover()
        {
            _cover = null;
            _coverKey = null;
            ArtBrush.ImageSource = null;
            CompactArtBrush.ImageSource = null;
            ToastArtBrush.ImageSource = null;
            CompactVArtBrush.ImageSource = null;
            ToastVArtBrush.ImageSource = null;
            BgCover.Source = null;
            BgCover2.Source = null;
            SetAccent(Color.FromRgb(30, 215, 96));
        }

        private void UpdatePlayIcon()
        {
            bool p = IsPlaying;
            PlayIcon.Visibility = p ? Visibility.Collapsed : Visibility.Visible;
            PauseIcon.Visibility = p ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateLyricsButton()
        {
            bool on = App.Config.Lyrics;
            LyricsBtn.Foreground = on ? Brushes.Black : new SolidColorBrush(Color.FromArgb(0xD9, 255, 255, 255));
            LyricsBtn.Background = on ? new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
        }

        // ================= polecenia =================
        private void PlayPause()
        {
            if (_snap == null) return;
            bool playing = IsPlaying;
            _playingOverride = !playing;
            _playingOverrideUntil = DateTime.UtcNow.AddSeconds(2);
            if (_holdTarget == null && _snap != null)
            {
                // zamroź pozycję w chwili pauzy, żeby pasek nie skakał
                _holdTarget = DisplayPosition();
                _holdAt = DateTime.UtcNow;
                _holdPlaying = !playing;
                _holdUntil = DateTime.UtcNow.AddSeconds(1.5);
            }
            UpdatePlayIcon();
            if (App.Config.FadeOnPause && _audio != null)
                _ = _audio.FadeToggleAsync(playing, () => _media.PlayPause());
            else
                _media.PlayPause();
            EnsureLoop();
        }

        private void ToggleLyrics()
        {
            App.Config.Lyrics = !App.Config.Lyrics;
            App.Config.Save();
            if (App.Config.Lyrics) LoadLyrics();
            UpdateLyricsButton();
            EnsureLoop();
        }

        private static void OpenSpotify()
        {
            try
            {
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo("https://open.spotify.com") { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error(ex); }
            }
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (!App.Config.WheelVolume || _audio == null) return;
            e.Handled = true;
            float v = _audio.Step(e.Delta > 0 ? 1 : -1);
            ShowVolume(v, v <= 0);
        }

        private void ShowVolume(float v, bool muted)
        {
            if (!App.Config.VolumeOsd) return;
            int pct = (int)Math.Round(v * 100);
            VolText.Text = pct.ToString();
            double barW = (VolFill.Parent as FrameworkElement)?.ActualWidth ?? 0;
            if (barW <= 0) barW = 158;
            VolFill.Width = Math.Max(0, barW * (muted ? 0 : v));
            VolIcon.Data = Geometry.Parse(muted || pct == 0
                ? "M0,5 H4 L9,0 V16 L4,11 H0 Z M11,5 L16,11 M16,5 L11,11"
                : pct < 50 ? "M0,5 H4 L9,0 V16 L4,11 H0 Z M11.5,5 Q13.5,8 11.5,11"
                           : "M0,5 H4 L9,0 V16 L4,11 H0 Z M11.5,5 Q13.5,8 11.5,11 M13.5,2.5 Q17.5,8 13.5,13.5");
            VolIcon.Stroke = Brushes.White;
            VolIcon.StrokeThickness = 1.4;
            VolVText.Text = VolText.Text;
            VolVFill.Height = Math.Max(0, 140 * (muted ? 0 : v));
            VolVIcon.Data = VolIcon.Data;
            VolVIcon.Stroke = Brushes.White;
            VolVIcon.StrokeThickness = 1.4;
            _toastKind = "vol";
            _toastUntil = DateTime.UtcNow.AddSeconds(1.6);
            EnsureLoop();
        }

        // ================= przewijanie =================
        private double SeekFraction(MouseEventArgs e)
        {
            double x = e.GetPosition(SeekArea).X;
            return Math.Clamp(x / Math.Max(1, SeekArea.ActualWidth), 0, 1);
        }

        private void SeekDown(object sender, MouseButtonEventArgs e)
        {
            if (_snap == null || _snap.Duration <= 0) return;
            e.Handled = true;
            _dragging = true;
            SeekArea.CaptureMouse();
            Thumb.Opacity = 1;
            _dragPos = SeekFraction(e) * _snap.Duration;
            SendSeek(false);
        }

        private void SeekMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _snap == null) return;
            _dragPos = SeekFraction(e) * _snap.Duration;
            SendSeek(false);
        }

        private void SeekUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            e.Handled = true;
            if (_snap != null) _dragPos = SeekFraction(e) * _snap.Duration;
            SendSeek(true);
            _dragging = false;
            SeekArea.ReleaseMouseCapture();
            if (!SeekArea.IsMouseOver) Thumb.Opacity = 0;
            if (!_hover) _collapseTimer.Start();
        }

        private void SendSeek(bool final)
        {
            DateTime now = DateTime.UtcNow;
            if (!final && (now - _lastSeekSent).TotalMilliseconds < 180) return;
            _lastSeekSent = now;
            _media.Seek(_dragPos);
            _holdTarget = _dragPos;
            _holdAt = now;
            _holdPlaying = IsPlaying;
            _holdUntil = now.AddSeconds(4);
            _lyricIdx = -2;
            EnsureLoop();
        }

        private double DisplayPosition()
        {
            if (_snap == null) return 0;
            if (_dragging) return _dragPos;
            DateTime now = DateTime.UtcNow;
            if (_holdTarget.HasValue && now < _holdUntil)
            {
                double p = _holdTarget.Value + (_holdPlaying && IsPlaying ? (now - _holdAt).TotalSeconds : 0);
                return _snap.Duration > 0 ? Math.Clamp(p, 0, _snap.Duration) : p;
            }
            return _snap.PositionNow(now);
        }

        private static string Fmt(double s)
        {
            if (s < 0 || double.IsNaN(s)) s = 0;
            int t = (int)s;
            return t >= 3600 ? $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}" : $"{t / 60}:{t % 60:00}";
        }

        // ================= tekst piosenki =================
        private async void LoadLyrics()
        {
            var snap = _snap;
            if (snap == null) return;
            string key = snap.Key;
            _lyricsKey = key;
            List<LyricLine> lines;
            try
            {
                lines = await LyricsService.GetAsync(key, snap.Title, snap.Artist, snap.Duration);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return;
            }
            if (_snap == null || _snap.Key != key) return;
            _lyrics = lines;
            BuildLyrics();
            EnsureLoop();
        }

        private void BuildLyrics()
        {
            LyricsStack.Children.Clear();
            _lyricIdx = -2;
            LyricsShift.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsShift.Y = 30;
            if (_lyrics == null) return;
            foreach (var line in _lyrics)
            {
                FrameworkElement el;
                if (line.Instrumental)
                {
                    el = new BarsControl { Width = 24, Height = 14, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12) };
                }
                else
                {
                    el = new TextBlock
                    {
                        Text = line.Text,
                        FontSize = 15.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                }
                el.Opacity = 0.28;
                LyricsStack.Children.Add(el);
            }
        }

        private bool LyricsVisible => App.Config.Lyrics && _lyrics != null && _lyrics.Count > 0;

        private void UpdateLyrics(double pos, int steps)
        {
            if (!LyricsVisible || LyricsStack.Children.Count != _lyrics.Count) return;
            int idx = 0;
            for (int i = 0; i < _lyrics.Count; i++)
            {
                if (_lyrics[i].Time <= pos + 0.25) idx = i;
                else break;
            }

            if (idx != _lyricIdx)
            {
                LyricsStack.UpdateLayout();
                // panel jeszcze nie ma rozmiaru (np. tuż po zmianie utworu) - spróbuj w następnej klatce
                if (LyricsStack.ActualWidth <= 0 || ((FrameworkElement)LyricsStack.Children[idx]).ActualHeight <= 0) return;

                bool jump = _lyricIdx < 0 || Math.Abs(idx - _lyricIdx) > 1;
                _lyricIdx = idx;
                double top = 0, h = 0;
                for (int i = 0; i <= idx; i++)
                {
                    var el = (FrameworkElement)LyricsStack.Children[i];
                    if (i == idx) { h = el.ActualHeight; top += el.Margin.Top; }
                    else top += el.ActualHeight + el.Margin.Top + el.Margin.Bottom;
                }
                double target = LyricsPanel.Height / 2 - (top + h / 2);
                var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(jump ? 250 : 520))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                LyricsShift.BeginAnimation(TranslateTransform.YProperty, anim);
            }

            // jasność linijek liczona w każdej klatce (bez nakładających się animacji):
            // bieżąca zawsze w pełni biała, sąsiednie przygaszone
            double k = Math.Min(1, 0.2 * steps);
            for (int i = 0; i < LyricsStack.Children.Count; i++)
            {
                var el = (UIElement)LyricsStack.Children[i];
                int d = _lyricIdx < 0 ? 2 : Math.Abs(i - _lyricIdx);
                double t = d == 0 ? 1 : d == 1 ? 0.42 : 0.26;
                double op = el.Opacity + (t - el.Opacity) * k;
                if (Math.Abs(op - t) < 0.01) op = t;
                el.Opacity = op;
            }
        }

        // ================= pętla animacji =================
        private void EnsureLoop()
        {
            if (_loopOn) return;
            _loopOn = true;
            _lastFrame = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopLoop()
        {
            if (!_loopOn) return;
            _loopOn = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private bool ToastActive(string kind) => _toastKind == kind && DateTime.UtcNow < _toastUntil;

        private (double w, double h) Target()
        {
            bool has = _snap != null;
            if (App.Config.DotMode && !_expanded && !ToastActive("vol")) return (30, 30);
            if (_vertical)
            {
                if (ToastActive("vol")) return (36, 220);
                if (_expanded) return has ? (260, LyricsVisible ? 504 : 408) : (220, 130);
                if (has && ToastActive("track")) return (150, 160);
                if (has) return (32, 150);
                switch (App.Config.IdleMode)
                {
                    case "hide": return (0, 0);
                    case "clock": return (30, 64);
                    default: return (30, 44);
                }
            }
            if (ToastActive("vol")) return (240, 36);
            if (_expanded) return has ? (370, LyricsVisible ? 272 : 176) : (300, 96);
            if (has && ToastActive("track")) return (330, 76);
            if (has) return (App.Config.CompactTitle ? 280 : 180, 32);
            switch (App.Config.IdleMode)
            {
                case "hide": return (0, 0);
                case "clock": return (120, 30);
                default: return (44, 30);
            }
        }

        private static double Fade(UIElement el, double target, bool canShow, int steps)
        {
            double t = canShow ? target : 0;
            double op = el.Opacity;
            double k = t > op ? 0.22 : 0.35;
            for (int i = 0; i < steps; i++) op += (t - op) * k;
            if (Math.Abs(op - t) < 0.01) op = t;
            el.Opacity = op;
            el.IsHitTestVisible = op > 0.6;
            el.Visibility = op <= 0 ? Visibility.Hidden : Visibility.Visible;
            return op;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            try { Frame(); }
            catch (Exception ex) { Log.Error(ex); }
        }

        private void Frame()
        {
            double t = _clock.Elapsed.TotalSeconds;
            // gdy wyspa stoi w miejscu (grają tylko słupki) wystarczy 30 klatek na sekundę
            if (_lowFps && t - _lastFrame < 1.0 / 30) return;
            double dt = t - _lastFrame;
            _lastFrame = t;
            int steps = (int)Math.Round(dt * 60);
            steps = Math.Clamp(steps, 1, 4);

            // ---- rozmiar (sprężyna) ----
            var (tw, th) = Target();
            for (int i = 0; i < steps; i++)
            {
                _vw = (_vw + (tw - _w) * Stiff) * Damp;
                _vh = (_vh + (th - _h) * Stiff) * Damp;
                _w += _vw;
                _h += _vh;
            }
            bool settled = Math.Abs(tw - _w) < 0.3 && Math.Abs(th - _h) < 0.3 && Math.Abs(_vw) < 0.05 && Math.Abs(_vh) < 0.05;
            if (settled) { _w = tw; _h = th; _vw = _vh = 0; }
            // coś się zmienia (najechanie, powiadomienie...) -> najpierw przywróć pełne okno
            if (_crop != null && (tw != _cropTw || th != _cropTh || _expanded || _moving)) ApplyCrop(null, 0, 0);
            double w = Math.Max(0, _w), h = Math.Max(0, _h);
            double r = Math.Min(Math.Min(w, h) / 2, 38);

            bool visible = w > 1 && h > 1 && !_fsHidden;
            Pill.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            Pill.Width = w;
            Pill.Height = h;
            Pill.CornerRadius = new CornerRadius(r);
            _clip.Rect = new Rect(0, 0, w, h);
            _clip.RadiusX = _clip.RadiusY = r;
            Shadow.Width = Glow.Width = w;
            Shadow.Height = Glow.Height = h;
            Shadow.CornerRadius = Glow.CornerRadius = new CornerRadius(r);

            // ---- widoki ----
            bool has = _snap != null;
            bool vol = ToastActive("vol");
            bool dot = App.Config.DotMode;
            bool track = !vol && !_expanded && has && !dot && ToastActive("track");
            bool near = Math.Abs(tw - w) < 40 && Math.Abs(th - h) < 30;
            double expOp = Fade(ExpandedView, !vol && _expanded && has ? 1 : 0, near, steps);
            Fade(IdleExpandedView, !vol && _expanded && !has ? 1 : 0, near, steps);
            bool hz = !_vertical;
            bool compact = !vol && !_expanded && !track && has && !dot;
            bool idle = !vol && !_expanded && !has && !dot;
            Fade(DotView, dot && !vol && !_expanded ? 1 : 0, near, steps);
            Fade(ToastView, hz && track ? 1 : 0, near, steps);
            Fade(VolView, hz && vol ? 1 : 0, near, steps);
            Fade(CompactView, hz && compact ? 1 : 0, near, steps);
            Fade(IdleView, hz && idle ? 1 : 0, near, steps);
            Fade(ToastVView, !hz && track ? 1 : 0, near, steps);
            Fade(VolVView, !hz && vol ? 1 : 0, near, steps);
            Fade(CompactVView, !hz && compact ? 1 : 0, near, steps);
            Fade(IdleVView, !hz && idle ? 1 : 0, near, steps);
            bool clock = App.Config.IdleMode == "clock";
            IdleClock.Visibility = IdleVClock.Visibility = clock ? Visibility.Visible : Visibility.Collapsed;
            IdleDot.Visibility = IdleVDot.Visibility = clock ? Visibility.Collapsed : Visibility.Visible;

            bool bg = App.Config.AlbumBackground && _cover != null;
            BgCover.Opacity = bg ? expOp * 0.95 : 0;
            AnimateBackground(bg && expOp > 0, expOp, dt, IsPlaying);
            BgDim.Opacity = bg ? expOp : 0;

            LyricsPanel.Visibility = LyricsVisible ? Visibility.Visible : Visibility.Collapsed;

            // ---- poziom dźwięku ----
            bool playing = IsPlaying;
            if (playing && _audio != null)
            {
                double pk = _audio.ReadPeak();
                _peakS = Math.Max(pk, _peakS * Math.Pow(0.86, steps));
                _peakMax = Math.Max(pk, _peakMax * Math.Pow(0.997, steps));
            }
            else
            {
                _peakS *= Math.Pow(0.86, steps);
            }
            double level = _peakMax > 0.02 ? Math.Min(1, _peakS / Math.Max(0.05, _peakMax * 0.8)) : 0.5;

            if (CompactView.Opacity > 0) CompactBars.Update(t, playing, _peakS, _peakMax);
            if (CompactVView.Opacity > 0) CompactVBars.Update(t, playing, _peakS, _peakMax);
            if (DotView.Opacity > 0)
            {
                // kropka świeci kolorem okładki i pulsuje z muzyką; szara, gdy nic nie gra
                Brush dotFill = has && playing ? _accentBrush : DotIdle;
                if (!ReferenceEquals(DotLight.Fill, dotFill)) DotLight.Fill = dotFill;
                double ds = playing ? 1 + 0.38 * level + 0.06 * Math.Sin(t * 2.4) : 1;
                DotScale.ScaleX += (ds - DotScale.ScaleX) * 0.45;
                DotScale.ScaleY = DotScale.ScaleX;
            }
            if (ToastView.Opacity > 0) ToastBars.Update(t, playing, _peakS, _peakMax);
            if (expOp > 0) ExpBars.Update(t, playing, _peakS, _peakMax);

            // ---- poświata i cień ----
            // stała jasność poświaty: zmiana w każdej klatce zmuszała kartę graficzną do ponownego rozmywania
            double glow = App.Config.Glow && has && playing ? (0.2 + 0.3 * expOp) : 0;
            double go = Glow.Opacity + (glow - Glow.Opacity) * Math.Min(1, 0.15 * steps);
            if (Math.Abs(go - glow) < 0.01) go = glow;
            if (go != Glow.Opacity) Glow.Opacity = go;
            Shadow.Opacity = visible ? Math.Min(0.55, 0.2 + h / 500) : 0;

            // ---- pulsowanie okładki ----
            if (expOp > 0)
            {
                double amt = 0, pulse = 0;
                switch (App.Config.CoverAnimation)
                {
                    case "breathe": amt = 0.025; pulse = playing ? (Math.Sin(t * 2.2) + 1) / 2 : 0; break;
                    case "beat": amt = 0.07; pulse = playing ? level : 0; break;
                    case "strong": amt = 0.12; pulse = playing ? level : 0; break;
                }
                double s = 1 + amt * pulse;
                ArtScale.ScaleX += (s - ArtScale.ScaleX) * 0.5;
                ArtScale.ScaleY = ArtScale.ScaleX;
            }

            // ---- postęp i tekst ----
            if (has && expOp > 0)
            {
                double pos = DisplayPosition();
                double dur = _snap.Duration;
                double frac = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
                double tw2 = SeekArea.ActualWidth;
                TrackFill.Width = tw2 * frac;
                Thumb.Margin = new Thickness(Math.Max(0, tw2 * frac - 5.5), 0, 0, 0);
                string ps = Fmt(pos), ds = dur > 0 ? Fmt(dur) : "--:--";
                if (ps != _posTextCache) { PosText.Text = ps; _posTextCache = ps; }
                if (ds != _durTextCache) { DurText.Text = ds; _durTextCache = ds; }

                if (LyricsVisible)
                {
                    UpdateLyrics(pos, steps);
                    if (_lyricIdx >= 0 && _lyricIdx < LyricsStack.Children.Count && LyricsStack.Children[_lyricIdx] is BarsControl lb)
                        lb.Update(t, playing, _peakS, _peakMax);
                }
            }

            // ---- czy pętla jest jeszcze potrzebna ----
            bool fading = Math.Abs(Glow.Opacity - glow) > 0.01;
            bool barsVisible = has && playing && visible;
            bool needed = !settled || fading || barsVisible || _dragging || (has && expOp > 0) ||
                          ExpandedView.Opacity is > 0 and < 1 || CompactView.Opacity is > 0 and < 1 ||
                          ToastView.Opacity is > 0 and < 1 || VolView.Opacity is > 0 and < 1 ||
                          IdleView.Opacity is > 0 and < 1 || IdleExpandedView.Opacity is > 0 and < 1 ||
                          CompactVView.Opacity is > 0 and < 1 || ToastVView.Opacity is > 0 and < 1 ||
                          VolVView.Opacity is > 0 and < 1 || IdleVView.Opacity is > 0 and < 1 ||
                          DotView.Opacity is > 0 and < 1;
            // ---- przycinanie okna do wyspy, gdy wszystko stoi ----
            bool midFade = ExpandedView.Opacity is > 0 and < 1 || CompactView.Opacity is > 0 and < 1 ||
                           ToastView.Opacity is > 0 and < 1 || VolView.Opacity is > 0 and < 1 ||
                           IdleView.Opacity is > 0 and < 1 || IdleExpandedView.Opacity is > 0 and < 1 ||
                           CompactVView.Opacity is > 0 and < 1 || ToastVView.Opacity is > 0 and < 1 ||
                           VolVView.Opacity is > 0 and < 1 || IdleVView.Opacity is > 0 and < 1 ||
                           DotView.Opacity is > 0 and < 1;
            bool croppable = settled && !_expanded && !_dragging && !_moving && _toastKind == null && !midFade && !fading;
            if (croppable)
            {
                if (_settledSince <= 0) _settledSince = t;
                if (_crop == null && t - _settledSince > 0.35) ApplyCrop(PillRect(tw, th), tw, th);
            }
            else
            {
                _settledSince = 0;
            }
            _lowFps = croppable;
            needed = needed || (croppable && _crop == null);

            if (!needed) StopLoop();
        }

        /// <summary>Apple Music: dwie rozmyte kopie okładki powoli płyną i obracają się, lekko "oddychając" z muzyką.</summary>
        private void AnimateBackground(bool on, double expOp, double dt, bool playing)
        {
            if (!on || !App.Config.AnimatedBackground)
            {
                BgCover2.Opacity = 0;
                _bgScale1.ScaleX = _bgScale1.ScaleY = 1;
                _bgRot1.Angle = 0;
                _bgMove1.X = _bgMove1.Y = 0;
                return;
            }
            _bgPhase += Math.Min(dt, 0.1) * (playing ? 1.0 : 0.3);
            double a = _bgPhase;
            double level = _peakMax > 0.02 ? Math.Min(1, _peakS / Math.Max(0.05, _peakMax * 0.8)) : 0;
            double beat = playing ? level * 0.05 : 0;

            double s1 = 1.3 + 0.07 * Math.Sin(a * 0.45) + beat;
            _bgScale1.ScaleX = _bgScale1.ScaleY = s1;
            _bgRot1.Angle = (a * 6) % 360;
            _bgMove1.X = 45 * Math.Sin(a * 0.31);
            _bgMove1.Y = 30 * Math.Cos(a * 0.27);

            double s2 = 1.55 + 0.08 * Math.Cos(a * 0.38) + beat;
            _bgScale2.ScaleX = -s2;   // lustrzane odbicie - inne plamy koloru
            _bgScale2.ScaleY = s2;
            _bgRot2.Angle = (-a * 4.5) % 360;
            _bgMove2.X = 55 * Math.Cos(a * 0.23);
            _bgMove2.Y = 38 * Math.Sin(a * 0.35);
            BgCover2.Opacity = expOp * 0.6;
        }

        // ================= przenoszenie wyspy =================
        private bool _mouseDown, _moving;
        private Point _downPx;

        private Point CursorPx()
        {
            Native.GetCursorPos(out var p);
            return new Point(p.X, p.Y);
        }

        private double Dpi => Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);
        private double Sc => Math.Clamp(App.Config.Scale, 0.7, 1.6);

        private void PillDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDown = true;
            _downPx = CursorPx();
        }

        private void PillMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _mouseDown = false; return; }
            Point p = CursorPx();
            if (!_moving)
            {
                if (Math.Abs(p.X - _downPx.X) < 6 && Math.Abs(p.Y - _downPx.Y) < 6) return;
                _moving = true;
                _expandTimer.Stop();
                _expanded = false;
                Pill.CaptureMouse();
                SetAlign("top");
                ApplyOrientation(false);
                _crop = null;
                System.Windows.Controls.Canvas.SetLeft(Root, 0);
                System.Windows.Controls.Canvas.SetTop(Root, 0);
                Width = 520 * Sc;
                Height = 380 * Sc;
                EnsureLoop();
            }
            // wyspa jedzie za kursorem
            double d = Dpi, sc = Sc;
            Left = p.X / d - 260 * sc;
            Top = p.Y / d - (8 + Math.Max(16, _h / 2)) * sc;
        }

        private void PillUp(object sender, MouseButtonEventArgs e)
        {
            bool wasDown = _mouseDown;
            _mouseDown = false;
            if (_moving)
            {
                _moving = false;
                Pill.ReleaseMouseCapture();
                e.Handled = true;
                SnapToEdge(CursorPx());
                return;
            }
            if (wasDown && !_expanded) { _expanded = true; EnsureLoop(); }
        }

        /// <summary>Przykleja wyspę do najbliższej krawędzi monitora, nad którym ją puszczono.</summary>
        private void SnapToEdge(Point px)
        {
            var scr = WinForms.Screen.FromPoint(new System.Drawing.Point((int)px.X, (int)px.Y));
            var wa = scr.WorkingArea;
            double dTop = px.Y - wa.Top, dBottom = wa.Bottom - px.Y, dLeft = px.X - wa.Left, dRight = wa.Right - px.X;
            double min = Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight));
            string edge = min == dTop ? "top" : min == dBottom ? "bottom" : min == dLeft ? "left" : "right";
            // blisko rogu -> przyklej w rogu
            bool nearX = Math.Min(dLeft, dRight) < wa.Width * 0.2, nearY = Math.Min(dTop, dBottom) < wa.Height * 0.25;
            if (nearX && nearY) edge = (dTop <= dBottom ? "top" : "bottom") + (dLeft <= dRight ? "left" : "right");
            var c = App.Config;
            c.Edge = edge;
            c.CustomPosition = true;
            c.AnchorX = px.X / Dpi;
            c.AnchorY = px.Y / Dpi;
            c.Save();
            PlaceWindow();
            if (_hover) _collapseTimer.Stop();
        }

        /// <summary>Ustawia okno przy wybranej krawędzi; wyspa rośnie w stronę środka ekranu.</summary>
        public void PlaceWindow()
        {
            try
            {
                var c = App.Config;
                double d = Dpi, sc = Sc;
                string edge0 = c.Edge ?? "top";
                bool vertical = edge0 == "left" || edge0 == "right";
                ApplyOrientation(vertical);
                double W = (vertical ? 300 : 520) * sc, H = (vertical ? 600 : 380) * sc;
                Width = W;
                Height = H;
                WinForms.Screen scr = c.CustomPosition
                    ? WinForms.Screen.FromPoint(new System.Drawing.Point((int)(c.AnchorX * d), (int)(c.AnchorY * d)))
                    : WinForms.Screen.PrimaryScreen;
                var w = scr.WorkingArea;
                Rect wa = new Rect(w.X / d, w.Y / d, w.Width / d, w.Height / d);
                double ax = c.CustomPosition ? c.AnchorX : wa.Left + wa.Width / 2;
                double ay = c.CustomPosition ? c.AnchorY : wa.Top + wa.Height / 2;
                double Clamp(double v, double lo, double hi) => hi < lo ? lo : Math.Max(lo, Math.Min(hi, v));

                string edge = c.Edge ?? "top";
                SetAlign(edge);
                switch (edge)
                {
                    case "bottom":
                        Left = Clamp(ax - W / 2, wa.Left, wa.Right - W);
                        Top = wa.Bottom - H;
                        break;
                    case "left":
                        Left = wa.Left;
                        Top = Clamp(ay - H / 2, wa.Top, wa.Bottom - H);
                        break;
                    case "topleft": Left = wa.Left; Top = wa.Top; break;
                    case "topright": Left = wa.Right - W; Top = wa.Top; break;
                    case "bottomleft": Left = wa.Left; Top = wa.Bottom - H; break;
                    case "bottomright": Left = wa.Right - W; Top = wa.Bottom - H; break;
                    case "right":
                        Left = wa.Right - W;
                        Top = Clamp(ay - H / 2, wa.Top, wa.Bottom - H);
                        break;
                    default:
                        Left = Clamp(ax - W / 2, wa.Left, wa.Right - W);
                        Top = wa.Top;
                        break;
                }
                _baseLeft = Left;
                _baseTop = Top;
                ApplyCrop(null, 0, 0);
                _settledSince = 0;
                EnsureLoop();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private bool _vertical, _orientationSet;

        // ---- przycinanie okna ----
        private readonly System.Windows.Controls.Canvas _host = new System.Windows.Controls.Canvas();
        private static readonly Brush DotIdle = MakeFrozen(Color.FromRgb(0x5A, 0x5A, 0x64));
        private double _baseLeft, _baseTop, _cropTw, _cropTh, _settledSince;
        private Rect? _crop;
        private bool _lowFps;

        private static Brush MakeFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>Prostokąt wyspy (z cieniem i poświatą) we współrzędnych pełnego okna.</summary>
        private Rect PillRect(double w, double h)
        {
            double rw = Root.Width, rh = Root.Height;
            var m = Pill.Margin;
            double x = Pill.HorizontalAlignment == HorizontalAlignment.Left ? m.Left
                     : Pill.HorizontalAlignment == HorizontalAlignment.Right ? rw - m.Right - w
                     : (rw - w) / 2;
            double y = Pill.VerticalAlignment == VerticalAlignment.Top ? m.Top
                     : Pill.VerticalAlignment == VerticalAlignment.Bottom ? rh - m.Bottom - h
                     : (rh - h) / 2;
            const double pad = 46; // rozmycie cienia i poświaty
            double x0 = Math.Max(0, x - pad), y0 = Math.Max(0, y - pad);
            double x1 = Math.Min(rw, x + w + pad), y1 = Math.Min(rh, y + h + pad);
            return new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
        }

        /// <summary>
        /// Przezroczyste okno WPF jest kopiowane na ekran w całości w każdej klatce - im mniejsze, tym mniej pracy.
        /// null = pełne okno (animacje), prostokąt = tylko wyspa (spoczynek).
        /// </summary>
        private void ApplyCrop(Rect? crop, double tw, double th)
        {
            _crop = crop;
            _cropTw = tw;
            _cropTh = th;
            double sc = Sc;
            Rect r = crop ?? new Rect(0, 0, Root.Width, Root.Height);
            System.Windows.Controls.Canvas.SetLeft(Root, -r.X * sc);
            System.Windows.Controls.Canvas.SetTop(Root, -r.Y * sc);
            Left = _baseLeft + r.X * sc;
            Top = _baseTop + r.Y * sc;
            Width = r.Width * sc;
            Height = r.Height * sc;
        }

        /// <summary>Poziomy (góra/dół) albo pionowy (boki ekranu) układ odtwarzacza.</summary>
        private void ApplyOrientation(bool v)
        {
            if (_orientationSet && v == _vertical) return;
            _orientationSet = true;
            _vertical = v;

            Root.Width = v ? 300 : 520;
            Root.Height = v ? 600 : 380;
            ExpandedView.Width = v ? 260 : 370;
            var cols = Header.ColumnDefinitions;
            if (v)
            {
                // duża okładka na górze, pod nią tytuł i przyciski - jak widżet
                Header.Height = double.NaN;
                cols[0].Width = new GridLength(1, GridUnitType.Star);
                cols[1].Width = new GridLength(0);
                cols[2].Width = GridLength.Auto;
                ArtHost.Width = ArtHost.Height = 224;
                Grid.SetRow(ArtHost, 0);
                Grid.SetColumn(ArtHost, 0);
                Grid.SetColumnSpan(ArtHost, 3);
                Grid.SetRow(InfoPanel, 1);
                Grid.SetColumn(InfoPanel, 0);
                InfoPanel.Margin = new Thickness(0, 14, 8, 0);
                InfoPanel.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(SidePanel, 1);
                Grid.SetColumn(SidePanel, 2);
                SidePanel.Margin = new Thickness(0, 16, 0, 0);
                SidePanel.VerticalAlignment = VerticalAlignment.Top;
                TitleText.FontSize = 16;
                ArtistText.FontSize = 13;
                ArtBg.CornerRadius = ArtImg.CornerRadius = new CornerRadius(16);
                LyricsStack.Width = 224;

                IdleExpandedView.Width = 220;
                IdleExpandedView.Height = 130;
                IdleTexts.HorizontalAlignment = HorizontalAlignment.Center;
                IdleTexts.VerticalAlignment = VerticalAlignment.Top;
                IdleTexts.Margin = new Thickness(0, 26, 0, 0);
                IdleTitle.HorizontalAlignment = IdleSub.HorizontalAlignment = HorizontalAlignment.Center;
                OpenSpotifyBtn.HorizontalAlignment = HorizontalAlignment.Center;
                OpenSpotifyBtn.VerticalAlignment = VerticalAlignment.Bottom;
                OpenSpotifyBtn.Margin = new Thickness(0, 0, 0, 22);
            }
            else
            {
                Header.Height = 52;
                cols[0].Width = new GridLength(52);
                cols[1].Width = new GridLength(1, GridUnitType.Star);
                cols[2].Width = GridLength.Auto;
                ArtHost.Width = ArtHost.Height = 52;
                Grid.SetRow(ArtHost, 0);
                Grid.SetColumn(ArtHost, 0);
                Grid.SetColumnSpan(ArtHost, 1);
                Grid.SetRow(InfoPanel, 0);
                Grid.SetColumn(InfoPanel, 1);
                InfoPanel.Margin = new Thickness(12, 0, 8, 0);
                InfoPanel.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(SidePanel, 0);
                Grid.SetColumn(SidePanel, 2);
                SidePanel.Margin = new Thickness(0);
                SidePanel.VerticalAlignment = VerticalAlignment.Center;
                TitleText.FontSize = 15;
                ArtistText.FontSize = 12.5;
                ArtBg.CornerRadius = ArtImg.CornerRadius = new CornerRadius(12);
                LyricsStack.Width = 334;

                IdleExpandedView.Width = 300;
                IdleExpandedView.Height = 96;
                IdleTexts.HorizontalAlignment = HorizontalAlignment.Left;
                IdleTexts.VerticalAlignment = VerticalAlignment.Center;
                IdleTexts.Margin = new Thickness(22, 0, 0, 0);
                IdleTitle.HorizontalAlignment = IdleSub.HorizontalAlignment = HorizontalAlignment.Left;
                OpenSpotifyBtn.HorizontalAlignment = HorizontalAlignment.Right;
                OpenSpotifyBtn.VerticalAlignment = VerticalAlignment.Center;
                OpenSpotifyBtn.Margin = new Thickness(0, 0, 20, 0);
            }
            // tekst piosenki zawija się inaczej - ułóż go od nowa
            if (_lyrics != null) BuildLyrics();
            EnsureLoop();
        }

        private void SetAlign(string edge)
        {
            HorizontalAlignment h = HorizontalAlignment.Center;
            VerticalAlignment v = VerticalAlignment.Top;
            Thickness m = new Thickness(0, 8, 0, 0), sm = new Thickness(0, 14, 0, 0);
            switch (edge)
            {
                case "bottom": v = VerticalAlignment.Bottom; m = new Thickness(0, 0, 0, 8); sm = new Thickness(0, 0, 0, 2); break;
                case "left": h = HorizontalAlignment.Left; v = VerticalAlignment.Center; m = new Thickness(8, 0, 0, 0); sm = new Thickness(8, 12, 0, 0); break;
                case "right": h = HorizontalAlignment.Right; v = VerticalAlignment.Center; m = new Thickness(0, 0, 8, 0); sm = new Thickness(0, 12, 8, 0); break;
                case "topleft": h = HorizontalAlignment.Left; m = new Thickness(8, 8, 0, 0); sm = new Thickness(8, 14, 0, 0); break;
                case "topright": h = HorizontalAlignment.Right; m = new Thickness(0, 8, 8, 0); sm = new Thickness(0, 14, 8, 0); break;
                case "bottomleft": h = HorizontalAlignment.Left; v = VerticalAlignment.Bottom; m = new Thickness(8, 0, 0, 8); sm = new Thickness(8, 0, 0, 2); break;
                case "bottomright": h = HorizontalAlignment.Right; v = VerticalAlignment.Bottom; m = new Thickness(0, 0, 8, 8); sm = new Thickness(0, 0, 8, 2); break;
            }
            foreach (var b in new FrameworkElement[] { Pill, Glow, Shadow })
            {
                b.HorizontalAlignment = h;
                b.VerticalAlignment = v;
            }
            Pill.Margin = Glow.Margin = m;
            Shadow.Margin = sm;
        }

        // ================= co 250 ms =================
        private void SlowTick()
        {
            _slowTicks++;
            try
            {
                var now = DateTime.Now;
                IdleClock.Text = now.ToString("HH:mm");
                IdleVClock.Text = now.ToString("HH") + "\n" + now.ToString("mm");

                if (_snap != null && _coverKey != null && _coverKey != _snap.Key) ClearCover();
                if (_snap == null && _cover != null) ClearCover();

                if (_toastKind != null && DateTime.UtcNow >= _toastUntil) { _toastKind = null; EnsureLoop(); }

                if (_slowTicks % 2 == 0 && _hwnd != IntPtr.Zero)
                {
                    bool fullscreen = Native.IsForegroundFullscreen(_hwnd);
                    bool fs = App.Config.HideInFullscreen && fullscreen;
                    if (fs != _fsHidden)
                    {
                        _fsHidden = fs;
                        Root.Visibility = fs ? Visibility.Hidden : Visibility.Visible;
                        EnsureLoop();
                    }
                    // wyspa ma być widoczna nad filmem / grą na pełnym ekranie: jeśli coś ją zasłania, wyciągnij ją na wierzch
                    if (fullscreen && !App.Config.HideInFullscreen && Pill.ActualWidth > 2 && Pill.IsVisible)
                    {
                        Point c = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
                        if (!Native.IsOnTopAt(_hwnd, (int)c.X, (int)c.Y)) Native.ForceTopmost(_hwnd);
                    }
                }
                if (_slowTicks % 12 == 0 && _hwnd != IntPtr.Zero && !_fsHidden) Native.KeepTopmost(_hwnd);
                if (_slowTicks % 8 == 0 && App.Config.VolumeOsd) Native.SetVolumeOsdVisible(false);
                if (_snap != null && _snap.Playing) EnsureLoop();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }
    }
}
