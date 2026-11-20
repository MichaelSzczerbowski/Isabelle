using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>
    /// tło „liquid glass” - miękkie, powoli płynące plamy w kolorach aktualnej okładki / logo stacji,
    /// przykryte przyciemnioną szybą z delikatnym połyskiem. Plamy liczone raz w małej rozdzielczości
    /// i tylko przesuwane/obracane, więc tło nie obciąża karty graficznej.
    /// </summary>
    public sealed class GlassBackground : Grid
    {
        private readonly Canvas _host = new Canvas { ClipToBounds = true };
        private readonly Image _a = new Image(), _b = new Image();
        private readonly RotateTransform _rotA = new RotateTransform(), _rotB = new RotateTransform();
        private readonly ScaleTransform _scA = new ScaleTransform(1.6, 1.6), _scB = new ScaleTransform(1.9, 1.9);
        private readonly TranslateTransform _mvA = new TranslateTransform(), _mvB = new TranslateTransform();
        private BitmapSource _source;
        private Color[] _colors;
        private double _phase;
        private DateTime _last = DateTime.UtcNow;

        public GlassBackground()
        {
            IsHitTestVisible = false;
            Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(12, 12, 16)) });
            Children.Add(_host);
            foreach (var (img, rot, sc, mv, op) in new[] { (_a, _rotA, _scA, _mvA, 1.0), (_b, _rotB, _scB, _mvB, 0.6) })
            {
                img.Stretch = Stretch.Fill;
                img.Opacity = op;
                img.RenderTransformOrigin = new Point(0.5, 0.5);
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
                var g = new TransformGroup();
                g.Children.Add(sc);
                g.Children.Add(rot);
                g.Children.Add(mv);
                img.RenderTransform = g;
                _host.Children.Add(img);
            }
            // szyba: przyciemnienie (czytelność) + połysk od góry + delikatna winieta
            Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(118, 8, 8, 12)) });
            Children.Add(new Border
            {
                Background = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(34, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(8, 255, 255, 255), 0.35),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6),
                }, new Point(0, 0), new Point(0.6, 1)),
            });
            Children.Add(new Border
            {
                Background = new RadialGradientBrush
                {
                    RadiusX = 0.85, RadiusY = 0.85,
                    GradientStops = new GradientStopCollection { new GradientStop(Colors.Transparent, 0.55), new GradientStop(Color.FromArgb(90, 0, 0, 0), 1) },
                },
            });

            SizeChanged += (s, e) => Layout();
            Loaded += (s, e) => CompositionTarget.Rendering += OnFrame;
            Unloaded += (s, e) => CompositionTarget.Rendering -= OnFrame;
            // bez domyślnych kolorów - pierwsze prawdziwe kolory (okładki / logo) ustawiamy od razu, bez przenikania
            // (wcześniej najpierw mignął różowy, a dopiero potem przenikał się w kolory piosenki)
        }

        /// <summary>Nowe kolory z okładki/logo (albo z akcentu, gdy obrazka brak). Zmiana płynnie się przenika.</summary>
        public void SetColors(BitmapSource source, Color accent)
        {
            if (source != null && ReferenceEquals(source, _source)) return;
            _source = source;
            var colors = source != null ? Palette(source) : new[] { accent, Shift(accent, 40), Shift(accent, -50), Color.FromRgb(40, 40, 70) };
            if (_colors != null && colors.SequenceEqual(_colors)) return;
            _colors = colors;
            var bmp = Render(colors);
            // pierwsze kolory albo okno jeszcze niewidoczne - od razu; później - łagodne przenikanie
            bool instant = !IsLoaded || _a.Source == null;
            foreach (var img in new[] { _a, _b })
            {
                if (instant) { img.Source = bmp; continue; }
                var old = img.Opacity;
                var fadeOut = new DoubleAnimation(0.15, TimeSpan.FromSeconds(0.45));
                fadeOut.Completed += (s, e) =>
                {
                    img.Source = bmp;
                    img.BeginAnimation(OpacityProperty, new DoubleAnimation(old, TimeSpan.FromSeconds(0.9)) { FillBehavior = FillBehavior.Stop });
                };
                img.BeginAnimation(OpacityProperty, fadeOut);
            }
        }

        private void Layout()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            // kwadrat większy niż przekątna - przy obrocie nigdy nie widać rogów
            double size = Math.Sqrt(w * w + h * h) * 1.05;
            foreach (var img in new[] { _a, _b })
            {
                img.Width = img.Height = size;
                Canvas.SetLeft(img, (w - size) / 2);
                Canvas.SetTop(img, (h - size) / 2);
            }
        }

        private Window _host;

        private void OnFrame(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            // okno zminimalizowane / schowane - nic nie liczymy (zero pracy w tle)
            _host ??= Window.GetWindow(this);
            if (!IsVisible || _host != null && _host.WindowState == WindowState.Minimized) { _last = now; return; }
            // kolory płyną bardzo wolno - 30 kl./s wygląda tak samo jak 144, a to kilka razy mniej pracy
            // (okno przezroczyste odświeża się programowo, więc każda klatka sporo kosztuje); w tle jeszcze rzadziej
            double minDt = _host != null && !_host.IsActive ? 1.0 / 15 : 1.0 / 30;
            if ((now - _last).TotalSeconds < minDt) return;
            double dt = Math.Min(0.1, (now - _last).TotalSeconds);
            _last = now;
            _phase += dt;
            _rotA.Angle = _phase * 7;
            _rotB.Angle = -_phase * 5 + 60;
            _scA.ScaleX = _scA.ScaleY = 1.55 + Math.Sin(_phase * 0.45) * 0.12;
            _scB.ScaleX = _scB.ScaleY = 1.9 + Math.Cos(_phase * 0.33) * 0.15;
            double w = ActualWidth * 0.08, h = ActualHeight * 0.08;
            _mvA.X = Math.Sin(_phase * 0.23) * w;
            _mvA.Y = Math.Cos(_phase * 0.19) * h;
            _mvB.X = Math.Cos(_phase * 0.17) * w;
            _mvB.Y = Math.Sin(_phase * 0.21) * h;
        }

        /// <summary>Plamy kolorów, rozmyte raz w małej rozdzielczości (potem tylko rozciągane - bardzo gładko).</summary>
        private static BitmapSource Render(Color[] c)
        {
            const int S = 96;
            var canvas = new Canvas { Width = S, Height = S, Background = new SolidColorBrush(Mix(c[0], Colors.Black, 0.55)) };
            var spots = new (double x, double y, double r, int ci)[]
            {
                (0.22, 0.25, 0.55, 0), (0.78, 0.30, 0.50, 1), (0.30, 0.78, 0.50, 2), (0.75, 0.75, 0.55, 3), (0.5, 0.5, 0.35, 1),
            };
            foreach (var (x, y, r, ci) in spots)
            {
                double d = S * r;
                var e = new Ellipse { Width = d, Height = d, Fill = new SolidColorBrush(c[ci % c.Length]), Opacity = 0.95 };
                Canvas.SetLeft(e, S * x - d / 2);
                Canvas.SetTop(e, S * y - d / 2);
                canvas.Children.Add(e);
            }
            canvas.Effect = new BlurEffect { Radius = 22, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
            var host = new Grid { Width = S, Height = S, ClipToBounds = true, Background = new SolidColorBrush(Mix(c[0], Colors.Black, 0.55)) };
            host.Children.Add(canvas);
            host.Measure(new Size(S, S));
            host.Arrange(new Rect(0, 0, S, S));
            var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>4 wyraziste, różne kolory z obrazka (najczęstsze odcienie, z preferencją nasyconych).</summary>
        private static Color[] Palette(BitmapSource src)
        {
            try
            {
                var small = new TransformedBitmap(new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0),
                    new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
                int w = small.PixelWidth, h = small.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                small.CopyPixels(px, stride, 0);
                var buckets = new Dictionary<int, (double r, double g, double b, double weight)>();
                for (int i = 0; i < px.Length; i += 4)
                {
                    if (px[i + 3] < 128) continue;
                    byte b = px[i], g = px[i + 1], r = px[i + 2];
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    double sat = max <= 0 ? 0 : (max - min) / max, val = max / 255.0;
                    if (val < 0.12) continue;
                    double hue = Hue(r, g, b);
                    int key = sat < 0.18 ? 1000 + (int)(val * 4) : (int)(hue / 30);     // szarości osobno
                    double weight = 0.4 + sat * 1.6;
                    buckets.TryGetValue(key, out var acc);
                    buckets[key] = (acc.r + r * weight, acc.g + g * weight, acc.b + b * weight, acc.weight + weight);
                }
                var colors = buckets.OrderByDescending(kv => kv.Value.weight).Take(4)
                    .Select(kv => Vivid(Color.FromRgb((byte)(kv.Value.r / kv.Value.weight), (byte)(kv.Value.g / kv.Value.weight), (byte)(kv.Value.b / kv.Value.weight))))
                    .ToList();
                if (colors.Count == 0) colors.Add(Color.FromRgb(255, 92, 160));
                while (colors.Count < 4) colors.Add(Shift(colors[colors.Count % Math.Max(1, colors.Count)], 35 * colors.Count));
                return colors.ToArray();
            }
            catch
            {
                return new[] { Color.FromRgb(255, 92, 160), Color.FromRgb(120, 80, 255), Color.FromRgb(40, 160, 255), Color.FromRgb(40, 40, 70) };
            }
        }

        private static double Hue(double r, double g, double b)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            if (d <= 0) return 0;
            double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
            return h < 0 ? h + 360 : h;
        }

        /// <summary>Kolor podbity do żywego, ale nie jaskrawego (dobrze wygląda pod przyciemnioną szybą).</summary>
        private static Color Vivid(Color c)
        {
            double max = Math.Max(c.R, Math.Max(c.G, c.B));
            if (max < 1) return Color.FromRgb(60, 60, 80);
            double k = Math.Min(2.2, 210 / max);
            return Color.FromRgb((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k));
        }

        /// <summary>Przesunięcie odcienia (w stopniach) - do dopełnienia palety.</summary>
        private static Color Shift(Color c, double degrees)
        {
            double h = Hue(c.R, c.G, c.B) + degrees, max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            double s = max <= 0 ? 0 : (max - min) / max, v = max / 255.0;
            if (s < 0.15) s = 0.5;
            h = (h % 360 + 360) % 360;
            double cc = v * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc;
            (double r, double g, double b) = h < 60 ? (cc, x, 0.0) : h < 120 ? (x, cc, 0.0) : h < 180 ? (0.0, cc, x) : h < 240 ? (0.0, x, cc) : h < 300 ? (x, 0.0, cc) : (cc, 0.0, x);
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        private static Color Mix(Color a, Color b, double k) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    }
}
