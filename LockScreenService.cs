using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>
    /// Ekran blokady z okładką obecnej piosenki: rozmyte tło z okładki, na środku okładka, tytuł i wykonawca.
    /// Poprzedni obraz ekranu blokady zapamiętujemy i przywracamy po wyłączeniu opcji albo zamknięciu Isabelle.
    /// </summary>
    public static class LockScreenService
    {
        private static int _flip;
        private static string OriginalPath => System.IO.Path.Combine(Settings.Folder, "lockscreen_original.jpg");

        /// <summary>Ustawia ekran blokady (wołać z wątku interfejsu - rysowanie obrazka).</summary>
        public static async void Apply(BitmapSource cover, string title, string artist)
        {
            if (cover == null) return;
            try
            {
                var c = App.Config;
                if (!c.LockScreenActive)
                {
                    await SaveOriginal();
                    c.LockScreenActive = true;
                    c.Save();
                }

                var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                int w = Math.Max(800, screen.Width), h = Math.Max(600, screen.Height);
                var img = Render(cover, title, artist, w, h);

                // dwie nazwy na zmianę - Windows potrafi trzymać w pamięci poprzedni plik o tej samej nazwie
                _flip = 1 - _flip;
                string path = System.IO.Path.Combine(Settings.Folder, "lockscreen_" + _flip + ".jpg");
                var enc = new JpegBitmapEncoder { QualityLevel = 92 };
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(path)) enc.Save(fs);
                await SetImage(path);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        /// <summary>Przywraca obraz ekranu blokady sprzed włączenia funkcji.</summary>
        public static void Restore(bool wait = false)
        {
            var c = App.Config;
            if (!c.LockScreenActive) return;
            c.LockScreenActive = false;
            c.Save();
            if (!File.Exists(OriginalPath)) return;
            var t = Task.Run(() => SetImage(OriginalPath));
            if (wait)
            {
                try { t.Wait(3000); } catch { }
            }
        }

        private static async Task SetImage(string path)
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
            }
            catch (FileNotFoundException)
            {
                // znany kaprys Windows przy pierwszym wywołaniu - druga próba zwykle się udaje
                await Task.Delay(400);
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
            }
        }

        private static async Task SaveOriginal()
        {
            try
            {
                var stream = Windows.System.UserProfile.LockScreen.GetImageStream();
                if (stream == null) return;
                using var src = stream.AsStreamForRead();
                using var dst = File.Create(OriginalPath);
                await src.CopyToAsync(dst);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private static BitmapSource Render(BitmapSource cover, string title, string artist, int w, int h)
        {
            var root = new Canvas { Width = w, Height = h, ClipToBounds = true, Background = Brushes.Black };

            // tło: okładka rozmyta raz w małej rozdzielczości i rozciągnięta na cały ekran
            var blurred = Blur(cover);
            double bg = Math.Max(w, h) * 1.15;
            var bgImg = new Image { Source = blurred, Width = bg, Height = bg, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(bgImg, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(bgImg, (w - bg) / 2);
            Canvas.SetTop(bgImg, (h - bg) / 2);
            root.Children.Add(bgImg);
            root.Children.Add(new Rectangle { Width = w, Height = h, Fill = new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)) });

            // Windows 11 ma zegar na środku u góry - wtedy okładka niżej
            bool win11 = Environment.OSVersion.Version.Build >= 22000;
            double size = h * (win11 ? 0.34 : 0.4);
            double top = win11 ? h * 0.38 : h * 0.2;

            var art = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.04),
                Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill },
                Effect = new DropShadowEffect { BlurRadius = size * 0.12, ShadowDepth = size * 0.03, Opacity = 0.5, Direction = 270 },
            };
            Canvas.SetLeft(art, (w - size) / 2);
            Canvas.SetTop(art, top);
            root.Children.Add(art);

            var t1 = new TextBlock
            {
                Text = title ?? "", FontSize = h * 0.034, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"), TextAlignment = TextAlignment.Center, Width = w * 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(t1, w * 0.15);
            Canvas.SetTop(t1, top + size + h * 0.03);
            root.Children.Add(t1);
            var t2 = new TextBlock
            {
                Text = artist ?? "", FontSize = h * 0.024, Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                FontFamily = new FontFamily("Segoe UI"), TextAlignment = TextAlignment.Center, Width = w * 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(t2, w * 0.15);
            Canvas.SetTop(t2, top + size + h * 0.03 + h * 0.05);
            root.Children.Add(t2);

            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            rtb.Freeze();
            return rtb;
        }

        private static BitmapSource Blur(BitmapSource cover)
        {
            const int size = 256;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            var img = new Image { Source = cover, Stretch = Stretch.UniformToFill, Width = size * 1.4, Height = size * 1.4,
                                  Effect = new BlurEffect { Radius = 24, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
            Canvas.SetLeft(img, -size * 0.2);
            Canvas.SetTop(img, -size * 0.2);
            canvas.Children.Add(img);
            canvas.Measure(new Size(size, size));
            canvas.Arrange(new Rect(0, 0, size, size));
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(canvas);
            rtb.Freeze();
            return rtb;
        }
    }
}
