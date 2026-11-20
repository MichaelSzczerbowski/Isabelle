using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IslandPlayer
{
    /// <summary>Tapeta z okładki: rozmyta okładka na cały pulpit + ostra okładka z tytułem na środku. Po muzyce wraca stara tapeta.</summary>
    public static class WallpaperService
    {
        private const int SPI_SETDESKWALLPAPER = 0x0014, SPI_GETDESKWALLPAPER = 0x0073;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(int action, int param, string vparam, int winIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(int action, int param, StringBuilder vparam, int winIni);

        private static int _flip;

        public static bool Active => App.Config.WallpaperActive;

        /// <summary>Ustawia tapetę z okładki (wołać z wątku interfejsu).</summary>
        public static void Apply(BitmapSource cover, string title, string artist)
        {
            if (cover == null) return;
            try
            {
                var c = App.Config;
                if (!c.WallpaperActive)
                {
                    // zapamiętaj obecną tapetę, żeby ją potem przywrócić
                    var sb = new StringBuilder(520);
                    SystemParametersInfo(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0);
                    c.OriginalWallpaper = sb.ToString();
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                    {
                        c.OriginalWallpaperStyle = k?.GetValue("WallpaperStyle") as string ?? "10";
                        c.OriginalTileWallpaper = k?.GetValue("TileWallpaper") as string ?? "0";
                    }
                    c.WallpaperActive = true;
                    c.Save();
                }

                var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                int w = Math.Max(800, screen.Width), h = Math.Max(600, screen.Height);
                BitmapSource img = Render(cover, title, artist, w, h);

                _flip = 1 - _flip; // dwie nazwy na zmianę - Windows czasem nie odświeża tej samej ścieżki
                string path = Path.Combine(Settings.Folder, "wallpaper_" + _flip + ".jpg");
                var enc = new JpegBitmapEncoder { QualityLevel = 92 };
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(path)) enc.Save(fs);

                Task.Run(() =>
                {
                    try
                    {
                        using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                        {
                            k?.SetValue("WallpaperStyle", "10"); // wypełnienie
                            k?.SetValue("TileWallpaper", "0");
                        }
                        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, 0x01 | 0x02);
                    }
                    catch (Exception ex) { Log.Error(ex); }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        /// <summary>Przywraca tapetę sprzed włączenia funkcji.</summary>
        public static void Restore(bool wait = false)
        {
            var c = App.Config;
            if (!c.WallpaperActive) return;
            string path = c.OriginalWallpaper ?? "";
            string style = c.OriginalWallpaperStyle ?? "10", tile = c.OriginalTileWallpaper ?? "0";
            c.WallpaperActive = false;
            c.Save();
            void Do()
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                    {
                        k?.SetValue("WallpaperStyle", style);
                        k?.SetValue("TileWallpaper", tile);
                    }
                    SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, 0x01 | 0x02);
                }
                catch (Exception ex) { Log.Error(ex); }
            }
            if (wait) Do();
            else Task.Run(Do);
        }

        /// <summary>
        /// Tapeta "w kolorach okładki": bez samego obrazka (twarze, napisy), tylko miękkie plamy jej głównych barw.
        /// </summary>
        private static BitmapSource Render(BitmapSource cover, string title, string artist, int w, int h)
        {
            var pal = Palette(cover, 4);
            Color baseColor = Shade(pal[0], 0.22);

            // liczone w małej rozdzielczości i rozciągnięte - bardzo miękkie przejścia, szybkie liczenie
            int sw = Math.Max(96, w / 12), sh = Math.Max(54, h / 12);
            var small = new Grid { Width = sw, Height = sh, ClipToBounds = true, Background = new SolidColorBrush(baseColor) };
            var spots = new (double x, double y, double size)[]
            {
                (0.18, 0.22, 0.95), (0.82, 0.28, 0.85), (0.30, 0.85, 0.80), (0.80, 0.82, 0.95),
            };
            var canvas = new Canvas { Width = sw, Height = sh };
            for (int i = 0; i < spots.Length; i++)
            {
                double d = sh * spots[i].size;
                var e = new System.Windows.Shapes.Ellipse
                {
                    Width = d * 1.4, Height = d, Opacity = 0.9,
                    Fill = new SolidColorBrush(Soften(pal[i % pal.Length])),
                };
                Canvas.SetLeft(e, sw * spots[i].x - d * 0.7);
                Canvas.SetTop(e, sh * spots[i].y - d * 0.5);
                canvas.Children.Add(e);
            }
            canvas.Effect = new BlurEffect { Radius = Math.Max(10, sh / 3.0), KernelType = KernelType.Gaussian };
            small.Children.Add(canvas);
            small.Measure(new Size(sw, sh));
            small.Arrange(new Rect(0, 0, sw, sh));
            var smallBmp = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
            smallBmp.Render(small);

            var root = new Grid { Width = w, Height = h, Background = new SolidColorBrush(baseColor) };
            var bg = new Image { Source = smallBmp, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(bg, BitmapScalingMode.HighQuality);
            root.Children.Add(bg);
            // delikatna winieta - ikony na pulpicie zostają czytelne
            var vignette = new RadialGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0x66, 0, 0, 0)) { RadiusX = 0.75, RadiusY = 0.75 };
            root.Children.Add(new Border { Background = vignette });
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            var full = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            full.Render(root);
            full.Freeze();
            return full;
        }

        /// <summary>Główne kolory okładki (najczęstsze i najbardziej wyraziste, różne od siebie).</summary>
        private static Color[] Palette(BitmapSource cover, int count)
        {
            var src = new FormatConvertedBitmap(cover, PixelFormats.Bgra32, null, 0);
            double sc = 32.0 / Math.Max(src.PixelWidth, src.PixelHeight);
            var tb = new TransformedBitmap(src, new ScaleTransform(sc, sc));
            int pw = tb.PixelWidth, ph = tb.PixelHeight, stride = pw * 4;
            var px = new byte[stride * ph];
            tb.CopyPixels(px, stride, 0);

            // kubełki 4 bity na kanał, punktacja = ilość * (0.35 + nasycenie)
            var score = new System.Collections.Generic.Dictionary<int, double>();
            var sum = new System.Collections.Generic.Dictionary<int, (double r, double g, double b, int n)>();
            for (int i = 0; i < px.Length; i += 4)
            {
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
                double max = Math.Max(r, Math.Max(g, b)) / 255.0, min = Math.Min(r, Math.Min(g, b)) / 255.0;
                double sat = max <= 0 ? 0 : (max - min) / max;
                score[key] = (score.TryGetValue(key, out var v) ? v : 0) + 0.35 + sat;
                var t = sum.TryGetValue(key, out var x) ? x : (0, 0, 0, 0);
                sum[key] = (t.r + r, t.g + g, t.b + b, t.n + 1);
            }
            var keys = new System.Collections.Generic.List<int>(score.Keys);
            keys.Sort((p, q) => score[q].CompareTo(score[p]));
            var result = new System.Collections.Generic.List<Color>();
            foreach (int k in keys)
            {
                var t = sum[k];
                var c = Color.FromRgb((byte)(t.r / t.n), (byte)(t.g / t.n), (byte)(t.b / t.n));
                bool distinct = true;
                foreach (var o in result)
                    if (Math.Abs(o.R - c.R) + Math.Abs(o.G - c.G) + Math.Abs(o.B - c.B) < 90) { distinct = false; break; }
                if (distinct) result.Add(c);
                if (result.Count == count) break;
            }
            while (result.Count < count) result.Add(result.Count > 0 ? Shade(result[0], 0.6) : Color.FromRgb(40, 40, 48));
            return result.ToArray();
        }

        /// <summary>Przyciemnia kolor (k = 0..1).</summary>
        private static Color Shade(Color c, double k) => Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

        /// <summary>Kolor na tapetę: nie za jasny (ikony mają być czytelne) i nie szary.</summary>
        private static Color Soften(Color c)
        {
            double max = Math.Max(c.R, Math.Max(c.G, c.B));
            double k = max > 200 ? 200 / max : 1;
            return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        }
    }
}
