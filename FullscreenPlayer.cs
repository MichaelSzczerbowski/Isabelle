using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace IslandPlayer
{
    /// <summary>
    /// Tryb „Teraz gra” na cały ekran: rozmyta, poruszająca się okładka w tle, duża okładka, tytuł,
    /// pasek postępu, przyciski i tekst piosenki przewijany na żywo. Esc / X zamyka.
    /// </summary>
    public sealed class FullscreenPlayer : Window
    {
        private static FullscreenPlayer _current;
        public static bool IsOpen => _current != null;

        public static void Toggle()
        {
            if (_current != null) { _current.Close(); return; }
            if (App.Island?.Snapshot == null) return;
            _current = new FullscreenPlayer();
            _current.Closed += (s, e) => _current = null;
            _current.Show();
            _current.Activate();
        }

        private readonly IslandWindow _island = App.Island;
        private readonly Image _bg1 = new Image(), _bg2 = new Image();
        private readonly RotateTransform _rot1 = new RotateTransform(), _rot2 = new RotateTransform();
        private readonly ScaleTransform _sc1 = new ScaleTransform(1.5, 1.5), _sc2 = new ScaleTransform(1.9, 1.9);
        private readonly Border _art = new Border();
        private readonly ImageBrush _artBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
        private readonly TextBlock _title = new TextBlock(), _artist = new TextBlock(), _tPos = new TextBlock(), _tDur = new TextBlock();
        private readonly Border _trackFill = new Border();
        private readonly Grid _track = new Grid();
        private readonly System.Windows.Shapes.Path _playIcon = new System.Windows.Shapes.Path();
        private readonly Grid _lyricsHost = new Grid { ClipToBounds = true };
        private readonly StackPanel _lyricsStack = new StackPanel();
        private readonly TranslateTransform _lyricsShift = new TranslateTransform();
        private readonly ColumnDefinition _lyricsCol = new ColumnDefinition();
        private readonly FrameworkElement _chrome;
        private readonly List<TextBlock> _lines = new List<TextBlock>();

        private string _key, _coverKey;
        private BitmapSource _shownCover;
        private IReadOnlyList<LyricLine> _shownLyrics;
        private int _lyricIdx = -2;
        private double _shift, _phase;
        private DateTime _lastFrame = DateTime.UtcNow, _lastMouse = DateTime.UtcNow;
        private Point _lastMousePos;

        private const string PlayGeo = "M0,0 L14,8 L0,16 Z";
        private const string PauseGeo = "M0,0 H5 V16 H0 Z M9,0 H14 V16 H9 Z";

        private FullscreenPlayer()
        {
            Title = "Isabelle";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = true;
            Topmost = true;
            Background = Brushes.Black;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            UseLayoutRounding = true;

            // na monitorze, na którym jest kursor
            var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
            var src = PresentationSource.FromVisual(_island);
            double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = (screen.Bounds.Left + 50) * sx;
            Top = (screen.Bounds.Top + 50) * sy;
            Width = 800;
            Height = 600;
            Loaded += (s, e) => WindowState = WindowState.Maximized;

            var root = new Grid { ClipToBounds = true };
            Content = root;

            // ---- tło: dwie warstwy rozmytej okładki, powoli krążące ----
            // w Canvas, bo zwykła siatka przycina duży obrazek do rozmiaru okna i przy obrocie widać jego rogi
            var bgHost = new Canvas { ClipToBounds = true };
            root.Children.Add(bgHost);
            foreach (var (img, rot, sc, op) in new[] { (_bg1, _rot1, _sc1, 1.0), (_bg2, _rot2, _sc2, 0.55) })
            {
                img.Stretch = Stretch.UniformToFill;
                img.Opacity = op;
                img.RenderTransformOrigin = new Point(0.5, 0.5);
                var g = new TransformGroup();
                g.Children.Add(sc);
                g.Children.Add(rot);
                img.RenderTransform = g;
                img.Effect = new BlurEffect { Radius = 110, RenderingBias = RenderingBias.Performance, KernelType = KernelType.Gaussian };
                bgHost.Children.Add(img);
            }
            root.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)) });

            // ---- zawartość ----
            var content = new Grid { Margin = new Thickness(90, 70, 90, 70) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(_lyricsCol);
            root.Children.Add(content);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(left);

            _art.CornerRadius = new CornerRadius(22);
            _art.Background = _artBrush;
            _art.Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 16, Opacity = 0.5, Direction = 270, Color = Colors.Black };
            left.Children.Add(_art);

            _title.FontSize = 40;
            _title.FontWeight = FontWeights.Bold;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _title.Margin = new Thickness(0, 34, 0, 0);
            left.Children.Add(_title);
            _artist.FontSize = 26;
            _artist.Foreground = new SolidColorBrush(Color.FromArgb(185, 255, 255, 255));
            _artist.TextTrimming = TextTrimming.CharacterEllipsis;
            _artist.Margin = new Thickness(0, 4, 0, 0);
            left.Children.Add(_artist);

            // pasek postępu
            _track.Height = 22;
            _track.Margin = new Thickness(0, 26, 0, 0);
            _track.Background = Brushes.Transparent;
            _track.Cursor = Cursors.Hand;
            _track.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center });
            _trackFill.Height = 6;
            _trackFill.CornerRadius = new CornerRadius(3);
            _trackFill.Background = Brushes.White;
            _trackFill.HorizontalAlignment = HorizontalAlignment.Left;
            _trackFill.VerticalAlignment = VerticalAlignment.Center;
            _track.Children.Add(_trackFill);
            _track.MouseLeftButtonUp += (s, e) =>
            {
                var snap = _island.Snapshot;
                if (snap == null || snap.Duration <= 0 || _track.ActualWidth <= 0) return;
                _island.SeekTo(Math.Clamp(e.GetPosition(_track).X / _track.ActualWidth, 0, 1) * snap.Duration);
                _lyricIdx = -2;
            };
            left.Children.Add(_track);
            var times = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            foreach (var t in new[] { _tPos, _tDur })
            {
                t.FontSize = 15;
                t.Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
            }
            _tDur.HorizontalAlignment = HorizontalAlignment.Right;
            times.Children.Add(_tPos);
            times.Children.Add(_tDur);
            left.Children.Add(times);

            // przyciski
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
            buttons.Children.Add(RoundButton("M16,0 V16 L4,8 Z M0,0 H3 V16 H0 Z", 56, 20, () => _island.Previous()));
            _playIcon.Data = Geometry.Parse(PauseGeo);
            buttons.Children.Add(RoundButton(null, 76, 26, () => _island.TogglePlay(), _playIcon, filled: true));
            buttons.Children.Add(RoundButton("M0,0 V16 L12,8 Z M13,0 H16 V16 H13 Z", 56, 20, () => _island.Next()));
            left.Children.Add(buttons);

            // ---- tekst piosenki ----
            Grid.SetColumn(_lyricsHost, 1);
            _lyricsHost.Margin = new Thickness(80, 0, 0, 0);
            _lyricsHost.OpacityMask = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Black, 0.22),
                new GradientStop(Colors.Black, 0.78), new GradientStop(Colors.Transparent, 1),
            }, 90);
            var canvas = new Canvas();
            _lyricsStack.RenderTransform = _lyricsShift;
            canvas.Children.Add(_lyricsStack);
            _lyricsHost.Children.Add(canvas);
            _lyricsHost.SizeChanged += (s, e) => _lyricsStack.Width = Math.Max(100, _lyricsHost.ActualWidth);
            _lyricsHost.MouseWheel += (s, e) => { _shift += e.Delta * 0.6; _manualUntil = DateTime.UtcNow.AddSeconds(3); e.Handled = true; };
            content.Children.Add(_lyricsHost);

            // ---- zamknij ----
            var close = RoundButton("M0,0 L12,12 M12,0 L0,12", 44, 14, Close, stroke: true);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Margin = new Thickness(0, 26, 30, 0);
            root.Children.Add(close);
            _chrome = close;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape || e.Key == Key.F11) Close();
                else if (e.Key == Key.Space) _island.TogglePlay();
                else if (e.Key == Key.Right) _island.Next();
                else if (e.Key == Key.Left) _island.Previous();
            };
            MouseMove += (s, e) =>
            {
                var p = e.GetPosition(this);
                if ((p - _lastMousePos).Length < 2) return;
                _lastMousePos = p;
                _lastMouse = DateTime.UtcNow;
            };
            SizeChanged += (s, e) => Layout();
            CompositionTarget.Rendering += OnFrame;
            Closed += (s, e) =>
            {
                CompositionTarget.Rendering -= OnFrame;
                _island.KeepPlayingAfter(_wasPlaying);
            };
        }

        private DateTime _manualUntil = DateTime.MinValue;
        private bool _wasPlaying;

        private Border RoundButton(string geo, double size, double icon, Action click, System.Windows.Shapes.Path custom = null,
                                   bool filled = false, bool stroke = false)
        {
            var path = custom ?? new System.Windows.Shapes.Path { Data = Geometry.Parse(geo) };
            path.Width = path.Height = icon;
            path.Stretch = Stretch.Uniform;
            if (stroke) { path.Stroke = Brushes.White; path.StrokeThickness = 2; path.StrokeStartLineCap = path.StrokeEndLineCap = PenLineCap.Round; }
            else path.Fill = filled ? Brushes.Black : Brushes.White;
            if (filled) path.Margin = new Thickness(3, 0, 0, 0);
            var normal = filled ? Brushes.White : new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            var b = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Margin = new Thickness(12, 0, 12, 0),
                Background = normal, Child = path, Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            };
            b.MouseEnter += (s, e) => b.Opacity = 0.85;
            b.MouseLeave += (s, e) => { b.Opacity = 1; ((ScaleTransform)b.RenderTransform).ScaleX = ((ScaleTransform)b.RenderTransform).ScaleY = 1; };
            b.MouseLeftButtonDown += (s, e) => { ((ScaleTransform)b.RenderTransform).ScaleX = ((ScaleTransform)b.RenderTransform).ScaleY = 0.93; e.Handled = true; };
            b.MouseLeftButtonUp += (s, e) => { ((ScaleTransform)b.RenderTransform).ScaleX = ((ScaleTransform)b.RenderTransform).ScaleY = 1; e.Handled = true; click(); };
            return b;
        }

        /// <summary>Rozmiary zależne od ekranu i od tego, czy jest tekst piosenki.</summary>
        private void Layout()
        {
            bool lyrics = _shownLyrics != null && _shownLyrics.Count > 0;
            _lyricsCol.Width = lyrics ? new GridLength(1.25, GridUnitType.Star) : new GridLength(0);
            _lyricsHost.Visibility = lyrics ? Visibility.Visible : Visibility.Collapsed;
            double h = ActualHeight, w = ActualWidth;
            if (h <= 0) return;
            double size = Math.Min(h * 0.5, (lyrics ? w * 0.42 : w * 0.6) - 180);
            size = Math.Max(200, size);
            _art.Width = _art.Height = size;
            foreach (var el in new FrameworkElement[] { _title, _artist, _track })
                el.Width = Math.Max(size, lyrics ? size : Math.Min(w * 0.5, size * 1.5));
            ((FrameworkElement)_tPos.Parent).Width = _track.Width;
            // kwadrat większy niż przekątna ekranu - obrócony nigdy nie odsłania rogów
            double bgSize = Math.Sqrt(w * w + h * h) * 1.05;
            foreach (var img in new[] { _bg1, _bg2 })
            {
                img.Width = img.Height = bgSize;
                Canvas.SetLeft(img, (w - bgSize) / 2);
                Canvas.SetTop(img, (h - bgSize) / 2);
            }
            foreach (var l in _lines) l.FontSize = LyricSize();
        }

        private double LyricSize() => Math.Clamp(ActualHeight / 24, 24, 54);

        private void OnFrame(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            double dt = Math.Min(0.1, (now - _lastFrame).TotalSeconds);
            _lastFrame = now;

            var snap = _island.Snapshot;
            if (snap == null) { Close(); return; }

            if (snap.Key != _key)
            {
                _key = snap.Key;
                _title.Text = snap.Title;
                _artist.Text = snap.Artist;
                _shownLyrics = null;
                _lyricIdx = -2;
                _island.EnsureLyrics();
                BuildLyrics(null);
            }
            var cover = _island.Cover;
            if (!ReferenceEquals(cover, _shownCover))
            {
                _shownCover = cover;
                _artBrush.ImageSource = cover;
                _bg1.Source = _bg2.Source = cover;
                _art.Background = cover != null ? _artBrush : (Brush)new SolidColorBrush(Color.FromRgb(42, 42, 48));
            }
            var lyrics = _island.Lyrics;
            if (!ReferenceEquals(lyrics, _shownLyrics) && _island.LyricsKey == _key)
            {
                _shownLyrics = lyrics;
                BuildLyrics(lyrics);
            }

            // postęp i przycisk
            double pos = _island.Position, dur = snap.Duration;
            _trackFill.Width = dur > 0 && _track.ActualWidth > 0 ? Math.Max(6, _track.ActualWidth * Math.Clamp(pos / dur, 0, 1)) : 0;
            _tPos.Text = Time(pos);
            _tDur.Text = dur > 0 ? "-" + Time(dur - pos) : "";
            bool playing = _island.Playing;
            _wasPlaying = playing;
            string geo = playing ? PauseGeo : PlayGeo;
            if (_playIcon.Tag as string != geo) { _playIcon.Tag = geo; _playIcon.Data = Geometry.Parse(geo); }

            // tło krąży wolniej podczas pauzy
            _phase += dt * (playing ? 1 : 0.25);
            _rot1.Angle = _phase * 4;
            _rot2.Angle = -_phase * 3 + 40;
            _sc1.ScaleX = _sc1.ScaleY = 1.5 + Math.Sin(_phase * 0.35) * 0.12;
            _sc2.ScaleX = _sc2.ScaleY = 1.9 + Math.Cos(_phase * 0.27) * 0.15;

            UpdateLyrics(pos, dt);

            // przycisk zamknięcia i kursor znikają po chwili bezruchu
            bool idle = (now - _lastMouse).TotalSeconds > 3;
            double target = idle ? 0 : 1;
            _chrome.Opacity += (target - _chrome.Opacity) * Math.Min(1, dt * 8);
            Cursor = idle ? Cursors.None : null;
        }

        private void BuildLyrics(IReadOnlyList<LyricLine> lyrics)
        {
            _lyricsStack.Children.Clear();
            _lines.Clear();
            _shift = 0;
            _lyricsShift.Y = 0;
            if (lyrics != null)
            {
                foreach (var l in lyrics)
                {
                    var tb = new TextBlock
                    {
                        Text = l.Instrumental ? "♪" : l.Text, FontSize = LyricSize(), FontWeight = FontWeights.Bold,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 22), Opacity = 0.3,
                        Cursor = Cursors.Hand, Tag = l.Time, Background = Brushes.Transparent,
                    };
                    tb.MouseLeftButtonUp += (s, e) => { _island.SeekTo((double)((TextBlock)s).Tag); _lyricIdx = -2; _manualUntil = DateTime.MinValue; };
                    _lines.Add(tb);
                    _lyricsStack.Children.Add(tb);
                }
            }
            Layout();
        }

        private void UpdateLyrics(double pos, double dt)
        {
            if (_lines.Count == 0 || _shownLyrics == null) return;
            int idx = -1;
            for (int i = 0; i < _shownLyrics.Count; i++)
            {
                if (_shownLyrics[i].Time <= pos + 0.25) idx = i;
                else break;
            }
            if (idx != _lyricIdx)
            {
                _lyricIdx = idx;
                for (int i = 0; i < _lines.Count; i++)
                {
                    double op = i == idx ? 1 : Math.Abs(i - idx) == 1 ? 0.45 : 0.28;
                    _lines[i].BeginAnimation(OpacityProperty, new DoubleAnimation(op, TimeSpan.FromSeconds(0.35)));
                }
            }
            if (DateTime.UtcNow < _manualUntil)
            {
                _lyricsShift.Y = _shift;
                return;
            }
            // bieżąca linijka trochę powyżej środka
            double target = _lyricsHost.ActualHeight * 0.4;
            if (idx >= 0 && idx < _lines.Count)
            {
                var el = _lines[idx];
                double top = el.TranslatePoint(new Point(0, 0), _lyricsStack).Y;
                target -= top + el.ActualHeight / 2;
            }
            _shift += (target - _shift) * Math.Min(1, dt * 6);
            _lyricsShift.Y = _shift;
        }

        private static string Time(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }
}
