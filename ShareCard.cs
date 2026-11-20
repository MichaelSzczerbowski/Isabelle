using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>
    /// Karta „teraz słucham” do udostępnienia: okładka na rozmytym tle, tytuł, wykonawca, pasek postępu
    /// i podpis isabelle. Renderowana do PNG 1080×1350 (pasuje na Instagram i Discorda).
    /// </summary>
    public static class ShareCard
    {
        private const double W = 1080, H = 1350, ArtSize = 760, Left = (W - ArtSize) / 2;

        public static BitmapSource Render(BitmapSource cover, string title, string artist,
                                          double position, double duration, Color accent)
        {
            var root = new Canvas { Width = W, Height = H, Background = new SolidColorBrush(Color.FromRgb(16, 16, 20)), ClipToBounds = true };

            // tło: powiększona, mocno rozmyta okładka + przyciemnienie
            if (cover != null)
            {
                var bg = new Image { Source = cover, Stretch = Stretch.UniformToFill, Width = H * 1.25, Height = H * 1.25,
                                     Effect = new BlurEffect { Radius = 140, RenderingBias = RenderingBias.Quality } };
                Canvas.SetLeft(bg, (W - H * 1.25) / 2);
                Canvas.SetTop(bg, (H - H * 1.25) / 2);
                root.Children.Add(bg);
            }
            else
            {
                root.Background = new LinearGradientBrush(Mix(accent, Colors.Black, 0.55), Color.FromRgb(12, 12, 16), 90);
            }
            root.Children.Add(new Rectangle
            {
                Width = W, Height = H,
                Fill = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(90, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(150, 0, 0, 0), 0.55),
                    new GradientStop(Color.FromArgb(215, 0, 0, 0), 1),
                }, 90),
            });

            // okładka
            var art = new Border
            {
                Width = ArtSize, Height = ArtSize, CornerRadius = new CornerRadius(36),
                Background = cover != null ? new ImageBrush(cover) { Stretch = Stretch.UniformToFill }
                                           : (Brush)new SolidColorBrush(Color.FromRgb(42, 42, 48)),
                Effect = new DropShadowEffect { BlurRadius = 70, ShadowDepth = 18, Opacity = 0.55, Color = Colors.Black, Direction = 270 },
            };
            Canvas.SetLeft(art, Left);
            Canvas.SetTop(art, 120);
            root.Children.Add(art);

            // tytuł i wykonawca
            var titleText = new TextBlock
            {
                Text = title ?? "", Width = ArtSize, FontSize = 54, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(titleText, Left);
            Canvas.SetTop(titleText, 928);
            root.Children.Add(titleText);

            var artistText = new TextBlock
            {
                Text = artist ?? "", Width = ArtSize, FontSize = 36, Foreground = new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)),
                FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(artistText, Left);
            Canvas.SetTop(artistText, 1000);
            root.Children.Add(artistText);

            // pasek postępu
            if (duration > 0)
            {
                double frac = Math.Clamp(position / duration, 0, 1);
                var track = new Rectangle { Width = ArtSize, Height = 10, RadiusX = 5, RadiusY = 5, Fill = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) };
                Canvas.SetLeft(track, Left);
                Canvas.SetTop(track, 1090);
                root.Children.Add(track);
                var fill = new Rectangle { Width = Math.Max(10, ArtSize * frac), Height = 10, RadiusX = 5, RadiusY = 5, Fill = new SolidColorBrush(Bright(accent)) };
                Canvas.SetLeft(fill, Left);
                Canvas.SetTop(fill, 1090);
                root.Children.Add(fill);

                var timeBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
                var t1 = new TextBlock { Text = Time(position), FontSize = 26, Foreground = timeBrush, FontFamily = new FontFamily("Segoe UI") };
                Canvas.SetLeft(t1, Left);
                Canvas.SetTop(t1, 1114);
                root.Children.Add(t1);
                var t2 = new TextBlock { Text = Time(duration), FontSize = 26, Foreground = timeBrush, FontFamily = new FontFamily("Segoe UI"),
                                         Width = 200, TextAlignment = TextAlignment.Right };
                Canvas.SetLeft(t2, Left + ArtSize - 200);
                Canvas.SetTop(t2, 1114);
                root.Children.Add(t2);
            }

            // podpis: słupki + odręczne „isabelle”
            var footer = new StackPanel { Orientation = Orientation.Horizontal };
            var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (h, c) in new[] { (18.0, Colors.White), (34.0, Color.FromRgb(255, 92, 160)), (24.0, Colors.White) })
                bars.Children.Add(new Rectangle { Width = 7, Height = h, RadiusX = 3.5, RadiusY = 3.5, Fill = new SolidColorBrush(c),
                                                  Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            footer.Children.Add(bars);
            var logo = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(IslandWindow.HandwritingPath), Stretch = Stretch.Uniform, Height = 40,
                Stroke = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), StrokeThickness = 3.2,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            footer.Children.Add(logo);
            footer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(footer, (W - footer.DesiredSize.Width) / 2);
            Canvas.SetTop(footer, 1238);
            root.Children.Add(footer);

            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            root.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>Zapisuje kartę w Obrazy\Isabelle i kopiuje do schowka (obraz + plik). Zwraca ścieżkę pliku.</summary>
        public static string SaveAndCopy(BitmapSource card, string title, string artist)
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Isabelle");
            Directory.CreateDirectory(dir);
            string name = Safe($"{artist} - {title}");
            if (name.Length == 0) name = "isabelle";
            string path = System.IO.Path.Combine(dir, name + ".png");
            using (var fs = File.Create(path))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(card));
                enc.Save(fs);
            }

            var data = new DataObject();
            data.SetImage(card);
            data.SetFileDropList(new StringCollection { path });
            for (int i = 0; i < 5; i++)
            {
                // schowek bywa chwilowo zajęty przez inny program
                try { Clipboard.SetDataObject(data, true); break; }
                catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(60); }
            }
            return path;
        }

        private static string Safe(string s)
        {
            var bad = System.IO.Path.GetInvalidFileNameChars();
            var clean = new string(s.Where(ch => !bad.Contains(ch)).ToArray()).Trim(' ', '-', '.');
            return clean.Length > 80 ? clean.Substring(0, 80).Trim() : clean;
        }

        private static string Time(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private static Color Mix(Color a, Color b, double k) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

        /// <summary>Kolor akcentu podniesiony do jasności, żeby był widoczny na ciemnym tle.</summary>
        private static Color Bright(Color c)
        {
            double max = Math.Max(c.R, Math.Max(c.G, c.B));
            return max < 140 ? Mix(c, Colors.White, 0.45) : c;
        }
    }
}
