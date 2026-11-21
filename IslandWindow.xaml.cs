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
        private double Stiff = 0.11, Damp = 0.66;

        private readonly MediaService _media = new MediaService();
        private AudioService _audio;
        private PlaybackExtras _extras;
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
            _collapseTimer.Tick += (s, e) => { _collapseTimer.Stop(); if (!_hover && !_dragging && !_pinned) { _expanded = false; EnsureLoop(); } };
            _pinTimer.Tick += (s, e) => CheckPinnedClick();
            _slowTimer.Tick += (s, e) => SlowTick();
            _wallTimer.Tick += (s, e) =>
            {
                _wallTimer.Stop();
                if (App.Config.AlbumWallpaper && _cover != null && _snap != null && IsPlaying)
                    WallpaperService.Apply(_cover, _snap.Title, _snap.Artist);
            };
            _lockTimer.Tick += (s, e) =>
            {
                _lockTimer.Stop();
                if (App.Config.LockScreenCover && _cover != null && _snap != null && _coverKey == _snap.Key)
                    LockScreenService.Apply(_cover, _snap.Title, _snap.Artist);
            };

            Pill.MouseEnter += (s, e) => { _hover = true; _collapseTimer.Stop(); if (!_expanded && !_moving && !App.Config.DotMode && !App.Config.ClickToOpen) _expandTimer.Start(); };
            Pill.MouseLeave += (s, e) =>
            {
                _hover = false;
                _expandTimer.Stop();
                if (!_dragging && !_pinned) _collapseTimer.Start();
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
            BuildLibraryHandle();
            FullBtn.Click += (s, e) => FullscreenPlayer.Toggle();
            ArtHost.Cursor = Cursors.Hand;
            ArtHost.MouseLeftButtonDown += (s, e) => { if (_expanded && _snap != null) e.Handled = true; };
            ArtHost.MouseLeftButtonUp += (s, e) =>
            {
                if (!_expanded || _snap == null) return;
                e.Handled = true;
                ToggleTrackInfo();
            };
            foreach (var v in new FrameworkElement[] { CompactView, ToastView, ExpandedView, IdleExpandedView, CompactVView, ToastVView })
                v.RenderTransform = _swipeShift;
            OpenSpotifyBtn.Click += (s, e) => OpenSpotify();

            SeekArea.MouseLeftButtonDown += SeekDown;
            SeekArea.MouseMove += SeekMove;
            SeekArea.MouseLeftButtonUp += SeekUp;
            SeekArea.MouseEnter += (s, e) => Thumb.Opacity = 1;
            SeekArea.MouseLeave += (s, e) => { if (!_dragging) Thumb.Opacity = 0; };

            PillInner.Clip = _clip;
            TitleClip.SizeChanged += (s, e) => Marquee.Update(TitleClip, TitleText, false);
            LyricsPanel.MouseWheel += LyricsWheel;
            CompactTitleClip.SizeChanged += (s, e) => Marquee.Update(CompactTitleClip, CompactTitleText, true);
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
            // rozmiar i pozycję okna ustawiamy sami (jednym wywołaniem, w pikselach) - bez skakania między monitorami
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            DpiChanged += (s2, e2) => { if (!_moving) Dispatcher.BeginInvoke(new Action(PlaceWindow), DispatcherPriority.Background); };
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            RegisterHotkeys();
        }

        private DateTime _startupUntil;
        private bool Startup => DateTime.UtcNow < _startupUntil;

        /// <summary>
        /// "isabelle" pisane jak "hello" w iPhonie: jedna ciągła linia rysowana piórem od lewej do prawej,
        /// a na końcu pojawia się kropka nad „i”.
        /// </summary>
        internal const string HandwritingPath = "M 20 66 C 26 64, 30 56, 32 48 C 31 58, 30 66, 34 70 C 38 72, 44 64, 48 56 C 50 52, 52 48, 54 47 C 58 52, 60 60, 56 67 C 53 72, 46 70, 47 66 C 52 70, 62 68, 68 60 C 72 52, 80 46, 86 50 C 78 46, 68 56, 72 65 C 76 72, 86 64, 90 50 C 88 60, 88 68, 93 70 C 98 71, 102 64, 105 58 C 110 44, 116 22, 110 16 C 104 12, 100 26, 102 40 C 103 52, 104 62, 106 70 C 108 60, 116 50, 124 56 C 129 62, 122 72, 112 68 C 118 69, 126 64, 132 58 C 140 56, 146 49, 141 46 C 134 45, 128 58, 133 66 C 137 72, 147 68, 154 60 C 160 48, 166 26, 160 18 C 155 13, 150 26, 152 40 C 153 54, 154 64, 159 69 C 163 71, 168 64, 172 58 C 178 48, 184 26, 178 18 C 173 13, 168 26, 170 40 C 171 54, 172 64, 177 69 C 181 71, 186 64, 190 58 C 198 56, 204 49, 199 46 C 192 45, 186 58, 191 66 C 195 72, 205 70, 214 60";

        private void AnimateHandwriting()
        {
            try
            {
                StartupText.Children.Clear();
                const double scale = 0.5, thick = 1.7;
                var geo = PathGeometry.CreateFromGeometry(Geometry.Parse(HandwritingPath));
                var flat = geo.GetFlattenedPathGeometry(0.1, ToleranceType.Absolute);
                Rect b = flat.Bounds;
                if (b.IsEmpty) return;
                double len = 0;
                foreach (var f in flat.Figures)
                {
                    Point prev = f.StartPoint;
                    foreach (var seg in f.Segments)
                    {
                        IEnumerable<Point> pts = seg is PolyLineSegment pl ? pl.Points
                                               : seg is LineSegment ls ? new[] { ls.Point }
                                               : (IEnumerable<Point>)Array.Empty<Point>();
                        foreach (var p in pts) { len += (p - prev).Length; prev = p; }
                    }
                }
                if (len <= 0) return;

                double pad = thick;
                var tf = new TransformGroup();
                tf.Children.Add(new TranslateTransform(-b.Left, -b.Top));
                tf.Children.Add(new ScaleTransform(scale, scale));
                tf.Children.Add(new TranslateTransform(pad, pad));
                geo.Transform = tf;
                StartupText.Width = b.Width * scale + pad * 2;
                StartupText.Height = b.Height * scale + pad * 2 + 2;

                var begin = TimeSpan.FromSeconds(0.4);     // najpierw wyspa wyskakuje
                double writeSec = 1.6;                     // czas pisania całego napisu
                double dashLen = len * scale / thick + 2;  // długość kreski w jednostkach grubości linii
                var stroke = new System.Windows.Shapes.Path
                {
                    Data = geo, Stroke = Brushes.White, StrokeThickness = thick,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    StrokeDashArray = new DoubleCollection { dashLen, dashLen }, StrokeDashOffset = dashLen,
                };
                stroke.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
                    new DoubleAnimation(dashLen, 0, TimeSpan.FromSeconds(writeSec))
                    {
                        BeginTime = begin,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    });
                StartupText.Children.Add(stroke);

                // kropka nad „i” na sam koniec
                double r = thick * 0.8;
                var dot = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = Brushes.White, Opacity = 0 };
                Canvas.SetLeft(dot, (33 - b.Left) * scale + pad - r);
                Canvas.SetTop(dot, (37 - b.Top) * scale + pad - r);
                StartupText.Children.Add(dot);
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2))
                {
                    BeginTime = begin + TimeSpan.FromSeconds(writeSec + 0.05),
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (App.Config.StartupAnimation)
            {
                // wyspa wyrasta z niczego, pokazuje logo i chowa się do zwykłego rozmiaru
                _startupUntil = DateTime.UtcNow.AddSeconds(2.9);
                _w = _h = 0;
                AnimateHandwriting();
            }
            ApplySettings();
            _audio = new AudioService(Dispatcher);
            _extras = new PlaybackExtras(this, _audio, _media);
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

        private string _placeKey;
        private bool _wallpaperWasOn;

        public void ApplySettings()
        {
            var c = App.Config;
            double sc = Math.Clamp(c.Scale, 0.7, 1.6);
            RootScale.ScaleX = RootScale.ScaleY = sc;
            // okno przestawiamy tylko, gdy zmieniło się coś od położenia - inaczej każda zmiana ustawień
            // rozwijała okno na pełny rozmiar i z powrotem (miganie)
            string placeKey = c.Edge + "|" + sc + "|" + c.CustomPosition + "|" + c.AnchorX + "|" + c.AnchorY + "|" + c.MonitorName;
            if (placeKey != _placeKey)
            {
                _placeKey = placeKey;
                PlaceWindow();
            }

            RegisterHotkeys();
            if (!c.VolumeOsd) Native.SetVolumeOsdVisible(true);
            if (c.Lyrics && _snap != null && _lyricsKey != _snap.Key) LoadLyrics();
            if (c.CoverAnimation == "off") ArtScale.ScaleX = ArtScale.ScaleY = 1;
            UpdateLyricsButton();
            ApplyTexts();
            // ---- personalizacja ----
            switch (c.AnimSpeed)
            {
                case "calm": Stiff = 0.07; Damp = 0.72; break;
                case "fast": Stiff = 0.17; Damp = 0.6; break;
                default: Stiff = 0.11; Damp = 0.66; break;
            }
            Pill.Background = c.IslandStyle == "glass" ? new SolidColorBrush(Color.FromArgb(0xB8, 12, 12, 14))
                            : c.IslandStyle == "graphite" ? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1F))
                            : (Brush)Brushes.Black;
            ApplyAccent();
            if (_lyrics != null && _lyricsSizeBuilt != c.LyricsSize) BuildLyrics();
            // tytuł w małej wyspie: szersza pigułka, napis na środku
            CompactView.Width = c.CompactTitle ? 280 : 180;
            CompactTitleClip.Visibility = c.CompactTitle ? Visibility.Visible : Visibility.Collapsed;
            ApplyExtras();
            EnsureLoop();
        }

        private void ApplyTexts()
        {
            IdleTitle.Text = L.T("Nic nie gra", "Nothing playing");
            IdleSub.Text = L.T("Włącz muzykę w dowolnej aplikacji", "Play music in any app");
            ToastLabel.Text = ToastVLabel.Text = L.T("TERAZ GRA", "NOW PLAYING");
            LyricsBtn.ToolTip = L.T("Tekst piosenki", "Lyrics");
            FullBtn.ToolTip = L.T("Pełny ekran", "Full screen");
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
                        _pinned = false;
                        _pinTimer.Stop();
                        _expanded = !_expanded;
                        if (_expanded) _collapseTimer.Stop();
                        EnsureLoop();
                        break;
                    case 7: FullscreenPlayer.Toggle(); break;
                    case 8: LibraryWindow.ShowWindow(); break;
                    case 9: QuickSearchWindow.Toggle(); break;
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
            else if (msg == WM_MOUSEHWHEEL && App.Config.Gestures)
            {
                OnHorizontalWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Cleanup()
        {
            try { _extras?.Stop(); } catch { }
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    foreach (var a in Hotkey.Actions) Native.UnregisterHotKey(_hwnd, a.id);
                }
                Native.SetVolumeOsdVisible(true);
                _overlay.Stop();
                _discord.Dispose();
                if (App.Config.WallpaperActive) WallpaperService.Restore(wait: true);
                if (App.Config.LockScreenActive) LockScreenService.Restore(wait: true);
                if (_ducked && _audio != null) _audio.Volume = _duckSaved;
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
                if (_infoOn) LoadTrackInfo(snap);
                if (App.Config.DiscordEnabled) LoadCoverUrl(snap);
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

            Marquee.SetText(TitleClip, TitleText, snap.Title, false);
            ArtistText.Text = snap.Artist;
            ToastTitle.Text = snap.Title;
            Marquee.SetText(CompactTitleClip, CompactTitleText, snap.Title, true);
            ToastArtist.Text = snap.Artist;
            ToastVTitle.Text = snap.Title;
            ToastVArtist.Text = snap.Artist;
            AppText.Text = MediaService.PrettyAppName(snap.AppId);
            if (_audio != null)
            {
                string prevApp = _audio.TargetApp;
                if (App.Config.RememberAppVolume && prevApp != snap.AppId)
                {
                    // każdy odtwarzacz ma swoją głośność: zapisz starą, przywróć zapamiętaną dla nowego
                    var vols = App.Config.AppVolumes ??= new Dictionary<string, float>();
                    if (!string.IsNullOrEmpty(prevApp)) vols[prevApp] = _audio.Volume;
                    _audio.TargetApp = snap.AppId;
                    if (vols.TryGetValue(snap.AppId, out float saved)) _audio.Volume = saved;
                    App.Config.Save();
                }
                _audio.TargetApp = snap.AppId;
            }
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
            if (App.Config.AlbumWallpaper) { _wallTimer.Stop(); _wallTimer.Start(); }
            if (App.Config.LockScreenCover) { _lockTimer.Stop(); _lockTimer.Start(); }
            ArtBrush.ImageSource = bmp;
            CompactArtBrush.ImageSource = bmp;
            ToastArtBrush.ImageSource = bmp;
            CompactVArtBrush.ImageSource = bmp;
            ToastVArtBrush.ImageSource = bmp;
            BgCover.Source = bmp;
            BgCover2.Source = bmp;
            SetAccent(accent);
        }

        private Color _coverAccent = Color.FromRgb(30, 215, 96);

        /// <summary>Kolor z okładki - użyty, gdy w personalizacji wybrano "Z okładki".</summary>
        private void SetAccent(Color c)
        {
            _coverAccent = c;
            ApplyAccent();
        }

        private static Color PresetAccent(string key, Color fallback)
        {
            switch (key)
            {
                case "pink": return Color.FromRgb(255, 92, 160);
                case "green": return Color.FromRgb(30, 215, 96);
                case "blue": return Color.FromRgb(64, 156, 255);
                case "purple": return Color.FromRgb(160, 120, 255);
                case "orange": return Color.FromRgb(255, 159, 10);
                case "red": return Color.FromRgb(255, 69, 58);
                case "white": return Color.FromRgb(235, 235, 240);
                default: return fallback;
            }
        }

        /// <summary>Kolor akcentu, słupki i pasek postępu wg personalizacji.</summary>
        private void ApplyAccent()
        {
            var c = App.Config;
            _accent = PresetAccent(c.Accent, _coverAccent);
            _accentBrush = new SolidColorBrush(_accent);
            _accentBrush.Freeze();
            Glow.Background = _accentBrush;
            ToastLabel.Foreground = _accentBrush;
            ToastVLabel.Foreground = _accentBrush;
            Brush bars = c.BarsColor == "white" ? Brushes.White : _accentBrush;
            foreach (var b in new[] { CompactBars, CompactVBars, ToastBars, ExpBars })
            {
                b.Fill = bars;
                b.Mode = c.BarsStyle == "dots" ? "dots" : "bars";
                b.Visibility = c.BarsStyle == "none" ? Visibility.Hidden : Visibility.Visible;
            }
            foreach (var child in LyricsStack.Children)
                if (child is BarsControl lb) { lb.Fill = bars; lb.Mode = c.BarsStyle == "dots" ? "dots" : "bars"; }
            TrackFill.Background = c.AccentProgress ? _accentBrush : Brushes.White;
            Thumb.Fill = c.AccentProgress ? _accentBrush : Brushes.White;
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

        // ================= pasek „Moja muzyka” =================
        private readonly Border _libHandle = new Border();

        /// <summary>
        /// Cienki pasek na dole rozwiniętej wyspy (jak „kreska” na dole iPhone'a). Po najechaniu poszerza się
        /// i jaśnieje, kliknięcie otwiera „Moją muzykę” tuż pod wyspą - okno „wysuwa się” z wyspy.
        /// </summary>
        private void BuildLibraryHandle()
        {
            var bar = new Border
            {
                Width = 38, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            _libHandle.Child = bar;
            _libHandle.Width = 140;
            _libHandle.Height = 16;
            _libHandle.Background = Brushes.Transparent;     // większe pole do kliknięcia niż sam pasek
            _libHandle.HorizontalAlignment = HorizontalAlignment.Center;
            _libHandle.VerticalAlignment = VerticalAlignment.Bottom;
            _libHandle.Margin = new Thickness(0, 0, 0, 3);
            _libHandle.Cursor = Cursors.Hand;
            _libHandle.Opacity = 0;
            _libHandle.IsHitTestVisible = false;
            _libHandle.ToolTip = L.T("Moja muzyka i radio", "My music and radio");
            var dur = TimeSpan.FromMilliseconds(160);
            _libHandle.MouseEnter += (s, e) =>
            {
                bar.BeginAnimation(WidthProperty, new DoubleAnimation(64, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                bar.Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            };
            _libHandle.MouseLeave += (s, e) =>
            {
                bar.BeginAnimation(WidthProperty, new DoubleAnimation(38, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                bar.Background = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            };
            _libHandle.MouseLeftButtonDown += (s, e) => e.Handled = true;
            _libHandle.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                // punkt pod środkiem wyspy (w pikselach ekranu) - tam „wysunie się” okno
                var p = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight));
                LibraryWindow.OpenFromIsland(p);
            };
            PillInner.Children.Add(_libHandle);
        }

        // ================= dla trybu pełnoekranowego =================
        public MediaSnapshot Snapshot => _snap;
        public BitmapSource Cover => _cover;
        public IReadOnlyList<LyricLine> Lyrics => _lyrics;
        public string LyricsKey => _lyricsKey;
        public double Position => DisplayPosition();
        public bool Playing => IsPlaying;
        public (double level, double max) AudioLevel => (_peakS, _peakMax);
        public bool MicBusy => _micActive;
        public float ReadPeak() => _audio?.ReadPeak() ?? 0;
        public bool ForegroundFullscreen => _fgFullscreen && !FullscreenPlayer.IsOpen;
        public void TogglePlay() => PlayPause();
        public void Next() => _media.Next();
        public void Previous() => _media.Previous();
        public void SeekTo(double seconds)
        {
            if (_snap == null) return;
            _dragPos = _snap.Duration > 0 ? Math.Clamp(seconds, 0, _snap.Duration) : Math.Max(0, seconds);
            SendSeek(true);
        }
        /// <summary>
        /// Po zamknięciu trybu pełnoekranowego muzyka ma grać dalej: jeśli grała, a odtwarzacz
        /// sam się zatrzymał (np. przez zmianę okna na pierwszym planie), wznawiamy ją.
        /// </summary>
        public async void KeepPlayingAfter(bool wasPlaying)
        {
            if (!wasPlaying) return;
            var key = _snap?.Key;
            foreach (int wait in new[] { 500, 1200 })
            {
                await System.Threading.Tasks.Task.Delay(wait);
                if (_snap == null || _snap.Key != key) return;
                if (!IsPlaying) { _media.PlayPause(); return; }
            }
        }

        /// <summary>Wczytuje tekst piosenki nawet przy wyłączonym tekście w wyspie.</summary>
        public void EnsureLyrics()
        {
            if (_snap != null && _lyricsKey != _snap.Key) LoadLyrics();
        }

        // ================= informacje o utworze =================
        private bool _infoOn;
        private bool InfoVisible => _infoOn && _snap != null;

        private void ToggleTrackInfo()
        {
            _infoOn = !_infoOn;
            if (_infoOn) LoadTrackInfo(_snap);
            EnsureLoop();
        }

        private async void LoadTrackInfo(MediaSnapshot snap)
        {
            if (snap == null) return;
            string key = snap.Key;
            SetInfoLines((L.T("Szukam informacji…", "Looking up info…"), null), (null, null), (null, null));
            var info = await TrackInfoService.Get(snap.Title, snap.Artist);
            if (_snap == null || _snap.Key != key) return;
            if (info == null)
            {
                SetInfoLines((L.T("Brak informacji o tym utworze", "No info about this track"), null), (null, null), (null, null));
                return;
            }
            double len = _snap.Duration > 0 ? _snap.Duration : info.Length;
            string album = info.Album + (info.Single ? L.T("  (singiel)", "  (single)") : "");
            string track = info.TrackNumber > 0 && info.TrackCount > 1 && !info.Single
                ? $"{info.TrackNumber} {L.T("z", "of")} {info.TrackCount}" : null;
            SetInfoLines(
                (L.T("ALBUM", "ALBUM"), album.Length > 0 ? album : "—", null, null),
                (L.T("ROK", "YEAR"), info.Year > 0 ? info.Year.ToString() : "—", L.T("GATUNEK", "GENRE"), info.Genre.Length > 0 ? info.Genre : "—"),
                (track != null ? L.T("UTWÓR", "TRACK") : L.T("DŁUGOŚĆ", "LENGTH"), track ?? Time(len),
                 track != null ? L.T("DŁUGOŚĆ", "LENGTH") : null, track != null ? Time(len) : null));
        }

        private static string Time(double s)
        {
            if (s <= 0) return "—";
            var t = TimeSpan.FromSeconds(s);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private void SetInfoLines((string label, string value) a, (string label, string value) b, (string label, string value) c)
            => SetInfoLines((a.label, a.value, null, null), (b.label, b.value, null, null), (c.label, c.value, null, null));

        private void SetInfoLines((string l1, string v1, string l2, string v2) a, (string l1, string v1, string l2, string v2) b,
                                  (string l1, string v1, string l2, string v2) c)
        {
            Fill(InfoLine1, a);
            Fill(InfoLine2, b);
            Fill(InfoLine3, c);

            static void Fill(TextBlock tb, (string l1, string v1, string l2, string v2) x)
            {
                tb.Inlines.Clear();
                void Pair(string label, string value)
                {
                    if (label == null) return;
                    if (value == null) { tb.Inlines.Add(new System.Windows.Documents.Run(label) { Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)) }); return; }
                    tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ")
                    {
                        FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                    });
                    tb.Inlines.Add(new System.Windows.Documents.Run(value));
                }
                Pair(x.l1, x.v1);
                if (x.l2 != null) tb.Inlines.Add(new System.Windows.Documents.Run("     "));
                Pair(x.l2, x.v2);
            }
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
            if (IsOwnRadio) { _lyrics = null; _lyricsKey = snap.Key; return; }
            string key = snap.Key;
            _lyricsKey = key;
            List<LyricLine> lines;
            try
            {
                // nasz plik z biblioteki: tekst zapisany przy utworze (od razu, także offline)
                var track = (snap.AppId ?? "").IndexOf("Isabelle", StringComparison.OrdinalIgnoreCase) >= 0 ? LocalPlayer.CurrentTrack : null;
                if (track != null && track.LyricsPath == null) await LyricsFinder.Find(track);
                if (track != null && !string.IsNullOrEmpty(track.LyricsPath) && System.IO.File.Exists(track.LyricsPath))
                    lines = LyricsService.FromFile(track.LyricsPath, snap.Duration > 0 ? snap.Duration : track.Duration);
                else
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

        private string _lyricsSizeBuilt;

        private void BuildLyrics()
        {
            _lyricsSizeBuilt = App.Config.LyricsSize;
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
                    el = new BarsControl
                    {
                        Width = 24, Height = 14, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12),
                        Fill = App.Config.BarsColor == "white" ? Brushes.White : _accentBrush,
                        Mode = App.Config.BarsStyle == "dots" ? "dots" : "bars",
                    };
                }
                else
                {
                    el = new TextBlock
                    {
                        Text = line.Text,
                        FontSize = App.Config.LyricsSize == "s" ? 13.5 : App.Config.LyricsSize == "l" ? 18 : 15.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                }
                el.Opacity = 0.28;
                double lineTime = line.Time;
                if (el is TextBlock)
                {
                    // klik w linijkę = przewiń piosenkę do tego miejsca
                    el.Cursor = Cursors.Hand;
                    el.MouseLeftButtonDown += (s, e) => e.Handled = true;
                    el.MouseLeftButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        if (_snap == null) return;
                        _dragPos = lineTime;
                        SendSeek(true);
                        _lyricsManualUntil = DateTime.MinValue;
                    };
                }
                LyricsStack.Children.Add(el);
            }
        }

        private bool LyricsVisible => App.Config.Lyrics && _lyrics != null && _lyrics.Count > 0 && !IsOwnRadio;
        /// <summary>Gra nasze radio - tekst piosenki nie ma sensu (wyłączony, przycisk schowany).</summary>
        private bool IsOwnRadio => LocalPlayer.CurrentStation != null && (_snap?.AppId ?? "").IndexOf("Isabelle", StringComparison.OrdinalIgnoreCase) >= 0;

        // ---- ręczne przewijanie tekstu kółkiem ----
        private DateTime _lyricsManualUntil = DateTime.MinValue;
        private bool _lyricsWasManual;

        private void LyricsWheel(object sender, MouseWheelEventArgs e)
        {
            if (!LyricsVisible) return;
            e.Handled = true; // kółko nad tekstem przewija tekst, a nie zmienia głośność
            double y = LyricsShift.Y;
            LyricsShift.BeginAnimation(TranslateTransform.YProperty, null);
            double max = LyricsPanel.Height / 2, min = LyricsPanel.Height / 2 - LyricsStack.ActualHeight;
            LyricsShift.Y = Math.Max(min, Math.Min(max, y + e.Delta / 120.0 * 30));
            _lyricsManualUntil = DateTime.UtcNow.AddSeconds(4);
            EnsureLoop();
        }

        private void UpdateLyrics(double pos, int steps)
        {
            if (!LyricsVisible || LyricsStack.Children.Count != _lyrics.Count) return;
            int idx = 0;
            for (int i = 0; i < _lyrics.Count; i++)
            {
                if (_lyrics[i].Time <= pos + 0.25) idx = i;
                else break;
            }

            bool manual = DateTime.UtcNow < _lyricsManualUntil;
            if (_lyricsWasManual && !manual) _lyricIdx = -2; // koniec przewijania - wróć do bieżącej linijki
            _lyricsWasManual = manual;
            if (manual)
            {
                // podczas przewijania wszystkie linijki czytelne, bieżąca wyróżniona
                for (int i = 0; i < LyricsStack.Children.Count; i++)
                {
                    var el = (UIElement)LyricsStack.Children[i];
                    double t = i == idx ? 1 : 0.62;
                    el.Opacity += (t - el.Opacity) * Math.Min(1, 0.2 * steps);
                }
                return;
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
            if (Startup) return _vertical ? (40, 120) : (190, 46);
            if (App.Config.DotMode && !_expanded && !ToastActive("vol")) return (30, 30);
            if (_vertical)
            {
                if (ToastActive("vol")) return (36, 220);
                if (_expanded) return has ? (260, LyricsVisible || InfoVisible ? 504 : 408) : (220, 130);
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
            // +12 px na dole: cienki pasek otwierający „Moją muzykę”
            if (_expanded) return has ? (370, (LyricsVisible || InfoVisible ? 272 : 176) + 12) : (300, 96 + 12);
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
            double maxR = App.Config.Corners == "square" ? 10 : App.Config.Corners == "soft" ? 18 : 38;
            double r = Math.Min(Math.Min(w, h) / 2, maxR);

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
            bool st = Startup;
            bool vol = !st && ToastActive("vol");
            bool dot = !st && App.Config.DotMode;
            bool track = !st && !vol && !_expanded && has && !dot && ToastActive("track");
            bool near = Math.Abs(tw - w) < 40 && Math.Abs(th - h) < 30;
            double expOp = Fade(ExpandedView, !st && !vol && _expanded && has ? 1 : 0, near, steps);
            Fade(IdleExpandedView, !st && !vol && _expanded && !has ? 1 : 0, near, steps);
            // pasek „Moja muzyka” na dole rozwiniętej wyspy (tylko w poziomie)
            double handleOp = _vertical ? 0 : Math.Max(ExpandedView.Opacity, IdleExpandedView.Opacity);
            if (Math.Abs(_libHandle.Opacity - handleOp) > 0.01) _libHandle.Opacity = handleOp;
            _libHandle.IsHitTestVisible = handleOp > 0.6;
            bool hz = !_vertical;
            bool compact = !st && !vol && !_expanded && !track && has && !dot;
            bool idle = !st && !vol && !_expanded && !has && !dot;
            Fade(DotView, dot && !vol && !_expanded ? 1 : 0, near, steps);
            Fade(StartupView, st ? 1 : 0, near, steps);
            StartupText.Visibility = hz ? Visibility.Visible : Visibility.Collapsed;
            if (StartupView.Opacity > 0) StartupBars.Update(t, true, 0.6 + 0.4 * Math.Abs(Math.Sin(t * 2.3)), 1);
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
            if (bg && expOp > 0)
            {
                // obrazek tła zawsze na środku wyspy (Canvas nie centruje sam)
                double bx = (w - BgCover.Width) / 2, by = (h - BgCover.Height) / 2;
                System.Windows.Controls.Canvas.SetLeft(BgCover, bx);
                System.Windows.Controls.Canvas.SetTop(BgCover, by);
                System.Windows.Controls.Canvas.SetLeft(BgCover2, bx);
                System.Windows.Controls.Canvas.SetTop(BgCover2, by);
            }
            AnimateBackground(bg && expOp > 0, expOp, dt, IsPlaying);
            BgDim.Opacity = bg ? expOp : 0;

            if (!_expanded && _infoOn) _infoOn = false;     // po zwinięciu wyspy wraca zwykły widok
            LyricsPanel.Visibility = LyricsVisible && !_infoOn ? Visibility.Visible : Visibility.Collapsed;
            var lyricsBtnVis = IsOwnRadio ? Visibility.Collapsed : Visibility.Visible;
            if (LyricsBtn.Visibility != lyricsBtnVis) LyricsBtn.Visibility = lyricsBtnVis;
            TrackInfoPanel.Visibility = InfoVisible ? Visibility.Visible : Visibility.Collapsed;

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
                string ps = Fmt(pos), ds = dur > 0 ? Fmt(dur) : L.T("na żywo", "live");
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
            bool croppable = !st && settled && !_expanded && !_dragging && !_moving && _toastKind == null && !midFade && !fading;
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
            needed = needed || (croppable && _crop == null) || st || StartupView.Opacity > 0;

            if (!needed) StopLoop();
        }

        /// <summary>Apple Music: dwie rozmyte kopie okładki powoli płyną i obracają się, lekko "oddychając" z muzyką.</summary>
        private void AnimateBackground(bool on, double expOp, double dt, bool playing)
        {
            if (!on || !App.Config.AnimatedBackground)
            {
                BgCover2.Opacity = 0;
                _bgScale1.ScaleX = _bgScale1.ScaleY = 1.8; // mocno przybliżona - widać kolory, nie obrazek
                _bgRot1.Angle = 0;
                _bgMove1.X = _bgMove1.Y = 0;
                return;
            }
            _bgPhase += Math.Min(dt, 0.1) * (playing ? 2.3 : 0.7); // szybszy, wyraźniejszy ruch (jak w Apple Music)
            double a = _bgPhase;
            double level = _peakMax > 0.02 ? Math.Min(1, _peakS / Math.Max(0.05, _peakMax * 0.8)) : 0;
            double beat = playing ? level * 0.05 : 0;

            double s1 = 2.1 + 0.12 * Math.Sin(a * 0.45) + beat * 2;
            _bgScale1.ScaleX = _bgScale1.ScaleY = s1;
            _bgRot1.Angle = (a * 9) % 360;
            _bgMove1.X = 95 * Math.Sin(a * 0.31);
            _bgMove1.Y = 70 * Math.Cos(a * 0.27);

            double s2 = 2.6 + 0.15 * Math.Cos(a * 0.38) + beat * 2;
            _bgScale2.ScaleX = -s2;   // lustrzane odbicie - inne plamy koloru
            _bgScale2.ScaleY = s2;
            _bgRot2.Angle = (-a * 7) % 360;
            _bgMove2.X = 105 * Math.Cos(a * 0.23);
            _bgMove2.Y = 75 * Math.Sin(a * 0.35);
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

        // ================= gesty =================
        private const int WM_MOUSEHWHEEL = 0x020E;
        private readonly TranslateTransform _swipeShift = new TranslateTransform();
        private DateTime _downAt;
        private bool _swiping;
        private int _hAcc;
        private DateTime _hLast, _hCooldown;

        /// <summary>Przewijanie w poziomie: dwa palce na touchpadzie albo przechylenie kółka myszy.</summary>
        private void OnHorizontalWheel(int delta)
        {
            var now = DateTime.UtcNow;
            if (now < _hCooldown) return;
            if ((now - _hLast).TotalMilliseconds > 350) _hAcc = 0;
            _hLast = now;
            _hAcc += delta;
            if (Math.Abs(_hAcc) >= 240)
            {
                int dir = _hAcc > 0 ? 1 : -1;
                _hAcc = 0;
                _hCooldown = now.AddMilliseconds(700);
                SwipeTrack(dir);
                return;
            }
            _swipeShift.BeginAnimation(TranslateTransform.XProperty, null);
            _swipeShift.X = -Math.Sign(_hAcc) * Math.Min(14, Math.Abs(_hAcc) / 18.0);
        }

        /// <summary>dir = 1 następny utwór, -1 poprzedni. Nowa zawartość wjeżdża z przeciwnej strony.</summary>
        private void SwipeTrack(int dir)
        {
            if (dir > 0) _media.Next(); else _media.Previous();
            SpringBack(dir);
        }

        private void SpringBack(int dir)
        {
            double from = dir == 0 ? _swipeShift.X : dir * 22;
            _swipeShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(from, 0, TimeSpan.FromSeconds(dir == 0 ? 0.22 : 0.38))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
            _swipeShift.X = 0;
        }

        private void PillDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDown = true;
            _downPx = CursorPx();
            _downAt = DateTime.UtcNow;
            _swiping = false;
        }

        private void PillMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _mouseDown = false; return; }
            Point p = CursorPx();
            double dx = p.X - _downPx.X, dy = p.Y - _downPx.Y;
            if (_swiping)
            {
                // trzymane dłużej albo pociągnięte w pionie = jednak przesuwanie wyspy
                if ((DateTime.UtcNow - _downAt).TotalMilliseconds < 450 && Math.Abs(dy) < 30 * Dpi)
                {
                    _swipeShift.BeginAnimation(TranslateTransform.XProperty, null);
                    _swipeShift.X = Math.Sign(dx) * Math.Min(18, Math.Abs(dx) / Dpi * 0.35);
                    return;
                }
                _swiping = false;
                _swipeShift.X = 0;
            }
            if (!_moving)
            {
                if (Math.Abs(dx) < 6 && Math.Abs(dy) < 6) return;
                if (App.Config.Gestures && Math.Abs(dx) > Math.Abs(dy) * 1.5 && (DateTime.UtcNow - _downAt).TotalMilliseconds < 250)
                {
                    // szybki ruch w bok - może to gest zmiany utworu
                    _swiping = true;
                    Pill.CaptureMouse();
                    return;
                }
                _moving = true;
                _expandTimer.Stop();
                _expanded = false;
                _pinned = false;
                _pinTimer.Stop();
                Pill.CaptureMouse();
                SetAlign("top");
                ApplyOrientation(false);
                _crop = null;
                System.Windows.Controls.Canvas.SetLeft(Root, 0);
                System.Windows.Controls.Canvas.SetTop(Root, 0);
                EnsureLoop();
            }
            // wyspa jedzie za kursorem
            double k = Sc * Dpi;
            SetBoundsPx(p.X - 260 * k, p.Y - (8 + Math.Max(16, _h / 2)) * k, 520 * k, 380 * k);
        }

        private void PillUp(object sender, MouseButtonEventArgs e)
        {
            bool wasDown = _mouseDown;
            _mouseDown = false;
            if (_swiping)
            {
                _swiping = false;
                Pill.ReleaseMouseCapture();
                e.Handled = true;
                double dx = CursorPx().X - _downPx.X;
                if (Math.Abs(dx) >= 40 * Dpi) SwipeTrack(dx < 0 ? 1 : -1);
                else SpringBack(0);
                return;
            }
            if (_moving)
            {
                _moving = false;
                Pill.ReleaseMouseCapture();
                e.Handled = true;
                SnapToEdge(CursorPx());
                return;
            }
            if (!wasDown) return;
            if (App.Config.ClickToPin)
            {
                // otwarty na stałe - zamknie go dopiero kliknięcie poza wyspą
                _pinned = true;
                _expanded = true;
                _collapseTimer.Stop();
                _pinTimer.Start();
                EnsureLoop();
            }
            else if (!_expanded)
            {
                _expanded = true;
                EnsureLoop();
            }
        }

        // ---- przypięcie po kliknięciu ----
        private bool _pinned;
        private readonly DispatcherTimer _pinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };

        private void CheckPinnedClick()
        {
            if (!_pinned) { _pinTimer.Stop(); return; }
            if (!Native.AnyMouseButtonDown()) return;
            Point p = CursorPx();
            if (Native.IsOnTopAt(_hwnd, (int)p.X, (int)p.Y) && Pill.IsMouseOver) return; // klik w samą wyspę
            Unpin();
        }

        private void Unpin()
        {
            _pinned = false;
            _pinTimer.Stop();
            if (!_hover) { _expanded = false; EnsureLoop(); }
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
            c.AnchorX = px.X;
            c.AnchorY = px.Y;
            c.AnchorPx = true;
            c.MonitorName = scr.DeviceName;
            c.Save();
            PlaceWindow();
            if (_hover) _collapseTimer.Stop();
        }

        /// <summary>Ustawia okno przy wybranej krawędzi; wyspa rośnie w stronę środka ekranu.</summary>
        private double _monScale = 1;

        /// <summary>Jedno wywołanie systemu: pozycja + rozmiar w pikselach (bez stanów pośrednich, które migały).</summary>
        private void SetBoundsPx(double x, double y, double w, double h)
        {
            if (_hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y),
                Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)), 0x0004 | 0x0010 | 0x0200); // NOZORDER|NOACTIVATE|NOOWNERZORDER
        }

        private static WinForms.Screen FindScreen(Settings c)
        {
            if (c.CustomPosition)
            {
                if (!string.IsNullOrEmpty(c.MonitorName))
                    foreach (var s in WinForms.Screen.AllScreens)
                        if (s.DeviceName == c.MonitorName) return s;
                return WinForms.Screen.FromPoint(new System.Drawing.Point((int)c.AnchorX, (int)c.AnchorY));
            }
            return WinForms.Screen.PrimaryScreen;
        }

        /// <summary>Ustawia okno przy wybranej krawędzi; wyspa rośnie w stronę środka ekranu. Wszystko w pikselach ekranu.</summary>
        public void PlaceWindow()
        {
            try
            {
                if (_hwnd == IntPtr.Zero) return;
                var c = App.Config;
                if (c.CustomPosition && !c.AnchorPx)
                {
                    // stary zapis (w DIP) -> piksele
                    c.AnchorX *= Dpi;
                    c.AnchorY *= Dpi;
                    c.AnchorPx = true;
                    c.Save();
                }
                double sc = Sc;
                string edge = c.Edge ?? "top";
                bool vertical = edge == "left" || edge == "right";
                ApplyOrientation(vertical);

                var scr = FindScreen(c);
                var wa = scr.WorkingArea;
                _monScale = Native.MonitorScale(wa.X + wa.Width / 2, wa.Y + wa.Height / 2);
                double k = sc * _monScale;
                double W = (vertical ? 300 : 520) * k, H = (vertical ? 600 : 380) * k;
                double ax = c.CustomPosition ? c.AnchorX : wa.Left + wa.Width / 2.0;
                double ay = c.CustomPosition ? c.AnchorY : wa.Top + wa.Height / 2.0;
                double Clamp(double v, double lo, double hi) => hi < lo ? lo : Math.Max(lo, Math.Min(hi, v));

                SetAlign(edge);
                double x, y;
                switch (edge)
                {
                    case "bottom": x = Clamp(ax - W / 2, wa.Left, wa.Right - W); y = wa.Bottom - H; break;
                    case "left": x = wa.Left; y = Clamp(ay - H / 2, wa.Top, wa.Bottom - H); break;
                    case "right": x = wa.Right - W; y = Clamp(ay - H / 2, wa.Top, wa.Bottom - H); break;
                    case "topleft": x = wa.Left; y = wa.Top; break;
                    case "topright": x = wa.Right - W; y = wa.Top; break;
                    case "bottomleft": x = wa.Left; y = wa.Bottom - H; break;
                    case "bottomright": x = wa.Right - W; y = wa.Bottom - H; break;
                    default: x = Clamp(ax - W / 2, wa.Left, wa.Right - W); y = wa.Top; break;
                }
                _baseLeft = x;
                _baseTop = y;
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
            double sc = Sc, k = sc * _monScale;
            Rect r = crop ?? new Rect(0, 0, Root.Width, Root.Height);
            System.Windows.Controls.Canvas.SetLeft(Root, -r.X * sc);
            System.Windows.Controls.Canvas.SetTop(Root, -r.Y * sc);
            SetBoundsPx(_baseLeft + r.X * k, _baseTop + r.Y * k, r.Width * k, r.Height * k);
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
            TitleClip.Height = v ? 23 : 22;
            Dispatcher.BeginInvoke(new Action(() => Marquee.Update(TitleClip, TitleText, false)), DispatcherPriority.Loaded);
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

        // ================= dodatki =================
        private readonly OverlayServer _overlay = new OverlayServer();
        private readonly DiscordPresence _discord = new DiscordPresence();
        private bool _fgFullscreen;
        private readonly DispatcherTimer _wallTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        private readonly DispatcherTimer _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private bool _lockWasOn;
        private DateTime _lastPlayingAt = DateTime.UtcNow;
        private bool _micActive, _pausedByMic, _ducked;
        private float _duckSaved;
        private DateTime _micIdleSince = DateTime.MinValue;

        /// <summary>Bieżąca i następna linijka tekstu piosenki (do nakładki i Discorda).</summary>
        private (string line, string next) CurrentLyric(double pos)
        {
            if (_lyrics == null || _lyrics.Count == 0) return ("", "");
            int idx = -1;
            for (int i = 0; i < _lyrics.Count; i++)
            {
                if (_lyrics[i].Time <= pos + 0.25) idx = i;
                else break;
            }
            string Line(int i) => i < 0 || i >= _lyrics.Count ? "" : _lyrics[i].Instrumental ? "♪" : _lyrics[i].Text;
            return (Line(idx), Line(idx + 1));
        }

        /// <summary>Włącza / wyłącza dodatki po zmianie ustawień.</summary>
        private void ApplyExtras()
        {
            var c = App.Config;
            if (c.OverlayEnabled) _overlay.Start(c.OverlayPort);
            else _overlay.Stop();
            if (!c.DiscordEnabled) _discord.Clear();
            else if (_snap != null && _coverUrlKey != _snap.Key) LoadCoverUrl(_snap);

            if (!c.AlbumWallpaper && c.WallpaperActive) WallpaperService.Restore();
            else if (c.AlbumWallpaper && !_wallpaperWasOn && _cover != null && IsPlaying) { _wallTimer.Stop(); _wallTimer.Start(); }
            _wallpaperWasOn = c.AlbumWallpaper;

            if (!c.LockScreenCover && c.LockScreenActive) LockScreenService.Restore();
            else if (c.LockScreenCover && !_lockWasOn && _cover != null) { _lockTimer.Stop(); _lockTimer.Start(); }
            _lockWasOn = c.LockScreenCover;

            if (!c.MicDuck) EndMicDuck();
        }

        public string OverlayStatus => _overlay.Running ? "http://localhost:" + _overlay.Port + "/" : _overlay.Error;

        // adres okładki do statusu na Discordzie (Discord nie przyjmie obrazka z pamięci, tylko link)
        private string _coverUrl = "", _coverUrlKey;

        private async void LoadCoverUrl(MediaSnapshot snap)
        {
            string key = snap.Key;
            var info = await TrackInfoService.Get(snap.Title, snap.Artist);
            if (_snap == null || _snap.Key != key) return;
            _coverUrl = info?.ArtworkUrl ?? "";
            _coverUrlKey = key;
        }

        /// <summary>Publikuje stan dla nakładki OBS i Discorda.</summary>
        private void PublishState()
        {
            var s = _snap;
            if (s == null)
            {
                OverlayServer.State = new NowPlayingState { Accent = Hex(_accent) };
                return;
            }
            double pos = DisplayPosition();
            var (line, next) = CurrentLyric(pos);
            OverlayServer.State = new NowPlayingState
            {
                Title = s.Title,
                Artist = s.Artist,
                App = MediaService.PrettyAppName(s.AppId),
                Playing = IsPlaying,
                Position = pos,
                Duration = s.Duration,
                Lyric = line,
                NextLyric = next,
                Accent = Hex(_accent),
                CoverKey = _cover != null ? (_coverKey ?? "") : "",
                CoverUrl = _coverUrlKey == s.Key ? _coverUrl : "",
                Cover = _cover,
            };
        }

        private static string Hex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        /// <summary>Rozmowa przez mikrofon: ściszenie albo pauza muzyki, potem powrót.</summary>
        private void CheckMic()
        {
            if (!App.Config.MicDuck || _snap == null) { EndMicDuck(); return; }
            bool inUse = MicWatcher.InUse();
            if (inUse)
            {
                _micIdleSince = DateTime.MinValue;
                if (_micActive) return;
                _micActive = true;
                bool canDuck = App.Config.MicMode == "duck" && _audio != null && _audio.IsAppVolume;
                if (canDuck)
                {
                    _duckSaved = _audio.Volume;
                    _ducked = true;
                    _ = DuckTo(_duckSaved * 0.2f);
                }
                else if (IsPlaying)
                {
                    // gdy nie da się ściszyć samej muzyki - pauza (nie ściszamy rozmówcy!)
                    _pausedByMic = true;
                    PlayPause();
                }
            }
            else if (_micActive)
            {
                if (_micIdleSince == DateTime.MinValue) _micIdleSince = DateTime.UtcNow;
                if ((DateTime.UtcNow - _micIdleSince).TotalSeconds >= 2) EndMicDuck();
            }
        }

        private void EndMicDuck()
        {
            if (!_micActive) return;
            _micActive = false;
            _micIdleSince = DateTime.MinValue;
            if (_ducked && _audio != null)
            {
                _ducked = false;
                _ = DuckTo(_duckSaved);
            }
            if (_pausedByMic)
            {
                _pausedByMic = false;
                if (!IsPlaying) PlayPause();
            }
        }

        private async System.Threading.Tasks.Task DuckTo(float target)
        {
            if (_audio == null) return;
            float from = _audio.Volume;
            for (int i = 1; i <= 12; i++)
            {
                _audio.Volume = from + (target - from) * i / 12f;
                await System.Threading.Tasks.Task.Delay(30);
            }
            _audio.Volume = target;
        }

        // ================= co 250 ms =================
        private int _clockMinute = -1;

        private void SlowTick()
        {
            _slowTicks++;
            try
            {
                var now = DateTime.Now;
                // zegar zmienia się raz na minutę - nie przerysowujemy go 4 razy na sekundę
                int minute = now.Hour * 60 + now.Minute;
                if (minute != _clockMinute)
                {
                    _clockMinute = minute;
                    IdleClock.Text = now.ToString("HH:mm");
                    IdleVClock.Text = now.ToString("HH") + "\n" + now.ToString("mm");
                }

                if (_snap != null && _coverKey != null && _coverKey != _snap.Key) ClearCover();
                if (_snap == null && _cover != null) ClearCover();

                if (_toastKind != null && DateTime.UtcNow >= _toastUntil) { _toastKind = null; EnsureLoop(); }

                if (_slowTicks % 2 == 0 && _hwnd != IntPtr.Zero)
                {
                    bool fullscreen = Native.IsForegroundFullscreen(_hwnd);
                    _fgFullscreen = fullscreen;
                    bool fs = (App.Config.HideInFullscreen && fullscreen) || FullscreenPlayer.IsOpen;
                    if (fs != _fsHidden)
                    {
                        _fsHidden = fs;
                        Root.Visibility = fs ? Visibility.Hidden : Visibility.Visible;
                        EnsureLoop();
                    }
                    // jeśli cokolwiek zasłania wyspę (zwykłe okno albo film na pełnym ekranie, gdy tak ustawiono) - wyciągnij ją na wierzch
                    if (!_fsHidden && !_moving && Pill.ActualWidth > 2 && Pill.IsVisible && (!fullscreen || !App.Config.HideInFullscreen))
                    {
                        Point c = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
                        if (!Native.IsOnTopAt(_hwnd, (int)c.X, (int)c.Y)) Native.ForceTopmost(_hwnd);
                    }
                }
                if (_slowTicks % 12 == 0 && _hwnd != IntPtr.Zero && !_fsHidden) Native.KeepTopmost(_hwnd);
                if (_slowTicks % 8 == 0 && App.Config.VolumeOsd) Native.SetVolumeOsdVisible(false);
                if (_snap != null && _snap.Playing) EnsureLoop();

                // ---- dodatki ----
                var cfg = App.Config;
                if (cfg.OverlayEnabled || cfg.DiscordEnabled) PublishState();
                if (_slowTicks % 4 == 0)
                {
                    if (cfg.DiscordEnabled) _discord.Update(OverlayServer.State, cfg.DiscordLyrics);
                    if (cfg.MicDuck || _micActive) CheckMic();
                }
                if (IsPlaying) _lastPlayingAt = DateTime.UtcNow;
                // tapeta wraca po minucie ciszy
                if (cfg.WallpaperActive && (DateTime.UtcNow - _lastPlayingAt).TotalSeconds > 60) WallpaperService.Restore();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }
    }
}
