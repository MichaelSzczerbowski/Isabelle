using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>
    /// grafika radia dla wyspy. Logo stacji (często mała ikonka .ico/.png z przezroczystym tłem)
    /// pobieramy sami i układamy na kwadratowym kafelku 400×400, żeby wyspa miała porządną okładkę.
    /// Okładki piosenek (z iTunes) przycinamy do kwadratu.
    /// </summary>
    public static class RadioArt
    {
        private static readonly HttpClient Http = CreateClient();
        private const int Size = 400;

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Isabelle/1.5");
            return c;
        }

        private static readonly System.Threading.SemaphoreSlim Gate = new System.Threading.SemaphoreSlim(6);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> Pending =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>>();
        private static string Folder
        {
            get
            {
                string d = System.IO.Path.Combine(Settings.Folder, "logos");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>
        /// Plik z kafelkiem logo stacji (400×400, zapamiętany na dysku na tydzień). Szukamy logo w możliwie
        /// najwyższej rozdzielczości: ikonka z bazy, „apple-touch-icon” ze strony stacji (zwykle 180 px)
        /// i ikonka z usługi Google (do 256 px) - wygrywa największa.
        /// </summary>
        public static Task<string> LogoFile(RadioStation st)
        {
            string key = Safe(st.Uuid.Length > 0 ? st.Uuid : st.Url);
            string path = System.IO.Path.Combine(Folder, key + ".png");
            try
            {
                if (File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays < 7) return Task.FromResult(path);
            }
            catch { }
            return Pending.GetOrAdd(key, _ => Build(st, path));
        }

        /// <summary>Kafelek z logo jako PNG (dla wyspy).</summary>
        public static async Task<byte[]> Logo(RadioStation st)
        {
            var path = await LogoFile(st);
            return path != null && File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        }

        private static async Task<string> Build(RadioStation st, string path)
        {
            await Gate.WaitAsync();
            try
            {
                var candidates = new System.Collections.Generic.List<string>();
                if (Uri.TryCreate(st.Favicon, UriKind.Absolute, out var fav) && (fav.Scheme == "https" || fav.Scheme == "http")) candidates.Add(fav.ToString());
                string host = null;
                if (Uri.TryCreate(st.Homepage, UriKind.Absolute, out var home)) host = home.Host;
                else if (fav != null) host = fav.Host;
                if (!string.IsNullOrEmpty(host))
                {
                    candidates.Add("https://" + host + "/apple-touch-icon.png");
                    candidates.Add("https://www.google.com/s2/favicons?domain=" + Uri.EscapeDataString(host) + "&sz=256");
                }
                var downloads = await Task.WhenAll(candidates.Select(Download));
                var png = await OnUi(() => ComposeBest(downloads, st.Name));
                if (png == null) return null;
                await File.WriteAllBytesAsync(path, png);
                return path;
            }
            catch (Exception ex)
            {
                Log.Info("radio logo: " + ex.Message);
                return null;
            }
            finally
            {
                Gate.Release();
                Pending.TryRemove(Safe(st.Uuid.Length > 0 ? st.Uuid : st.Url), out _);
            }
        }

        private static async Task<byte[]> Download(string url)
        {
            try
            {
                using var res = await Http.GetAsync(url);
                if (!res.IsSuccessStatusCode) return null;
                var data = await res.Content.ReadAsByteArrayAsync();
                return data.Length > 64 ? data : null;
            }
            catch { return null; }
        }

        /// <summary>Wybiera największy obrazek, który da się odczytać, i robi z niego kafelek.</summary>
        private static byte[] ComposeBest(byte[][] downloads, string name)
        {
            byte[] best = null;
            int bestSize = 0;
            foreach (var d in downloads)
            {
                if (d == null) continue;
                int size = PixelSize(d);
                // ikonka z Google ma domyślnie 16 px, gdy lepszej nie ma - liczymy ją tylko, gdy naprawdę duża
                if (size > bestSize) { bestSize = size; best = d; }
            }
            return Compose(best, true, name);
        }

        private static int PixelSize(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                int max = 0;
                foreach (var f in dec.Frames) max = Math.Max(max, Math.Min(f.PixelWidth, f.PixelHeight));
                return max;
            }
            catch { return 0; }
        }

        private static string Safe(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length > 80 ? sb.ToString(0, 80) : sb.ToString();
        }

        /// <summary>Okładka piosenki (kwadrat 400×400) albo null.</summary>
        public static async Task<byte[]> Cover(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                var data = await Http.GetByteArrayAsync(url);
                return await OnUi(() => Compose(data, false, null));
            }
            catch (Exception ex)
            {
                Log.Info("radio cover: " + ex.Message);
                return null;
            }
        }

        // rysowanie WPF musi iść w wątku interfejsu
        private static Task<byte[]> OnUi(Func<byte[]> f) =>
            Application.Current.Dispatcher.InvokeAsync(f).Task;

        private static byte[] Compose(byte[] data, bool logo, string name)
        {
            BitmapSource src = null;
            if (data != null && data.Length > 0)
            {
                try
                {
                    using var ms = new MemoryStream(data);
                    var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                    // .ico ma kilka rozmiarów - bierzemy największy
                    foreach (var f in dec.Frames)
                        if (src == null || f.PixelWidth > src.PixelWidth) src = f;
                }
                catch { src = null; }    // np. logo w SVG - WPF go nie odczyta
            }

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var full = new Rect(0, 0, Size, Size);
                if (src == null)
                {
                    // zapasowy kafelek: litera na ciemnym tle
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 46)), null, full);
                    string letter = string.IsNullOrEmpty(name) ? "♪" : name.Substring(0, 1).ToUpperInvariant();
                    var ft = new FormattedText(letter, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Size * 0.45,
                        Brushes.White, 1.0);
                    dc.DrawText(ft, new Point((Size - ft.Width) / 2, (Size - ft.Height) / 2));
                }
                else if (logo)
                {
                    // obcinamy puste / jednolite brzegi, żeby logo wypełniało kafelek, a nie pływało w marginesie
                    var (crop, transparent, edge) = Trim(src);
                    if (!transparent && crop.Width >= src.PixelWidth * 0.92 && crop.Height >= src.PixelHeight * 0.92)
                    {
                        src = Smooth(src);
                        // logo z własnym tłem na całą powierzchnię (np. pomarańczowy kwadrat) - na pełny kafelek
                        double kf = Math.Max((double)Size / src.PixelWidth, (double)Size / src.PixelHeight);
                        dc.DrawImage(src, new Rect((Size - src.PixelWidth * kf) / 2, (Size - src.PixelHeight * kf) / 2, src.PixelWidth * kf, src.PixelHeight * kf));
                    }
                    else
                    {
                        // tło: kolor brzegu logo (gdy miało swoje tło) albo kontrastowe do przezroczystego logo
                        Color bg = !transparent ? edge : Luminance(src) < 0.45 ? Color.FromRgb(245, 245, 247) : Color.FromRgb(28, 28, 32);
                        dc.DrawRectangle(new SolidColorBrush(bg), null, full);
                        BitmapSource part = new CroppedBitmap(src, crop);
                        part = Smooth(part);
                        double pad = Size * 0.07, box = Size - 2 * pad;
                        double k = Math.Min(box / part.PixelWidth, box / part.PixelHeight);
                        double w = part.PixelWidth * k, h = part.PixelHeight * k;
                        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
                        dc.DrawImage(part, new Rect((Size - w) / 2, (Size - h) / 2, w, h));
                    }
                }
                else
                {
                    double k = Math.Max((double)Size / src.PixelWidth, (double)Size / src.PixelHeight);
                    double w = src.PixelWidth * k, h = src.PixelHeight * k;
                    dc.DrawImage(src, new Rect((Size - w) / 2, (Size - h) / 2, w, h));
                }
            }
            var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var outMs = new MemoryStream();
            enc.Save(outMs);
            return outMs.ToArray();
        }

        /// <summary>
        /// Powiększanie małych ikonek (16-64 px) w kilku krokach po 2× z wygładzaniem -
        /// wychodzi gładko zamiast pikselozy albo mocnego rozmycia przy jednym dużym skoku.
        /// </summary>
        private static BitmapSource Smooth(BitmapSource src)
        {
            try
            {
                BitmapSource cur = src;
                while (Math.Max(cur.PixelWidth, cur.PixelHeight) < 300)
                {
                    int w = cur.PixelWidth * 2, h = cur.PixelHeight * 2;
                    var dv = new DrawingVisual();
                    RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
                    using (var dc = dv.RenderOpen()) dc.DrawImage(cur, new Rect(0, 0, w, h));
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    cur = rtb;
                }
                return cur;
            }
            catch { return src; }
        }

        /// <summary>
        /// Prostokąt z właściwym logo: bez przezroczystych brzegów, a przy logo z jednolitym tłem - bez pasów tego tła.
        /// Zwraca też, czy obraz ma przezroczystość, i kolor brzegu (tła).
        /// </summary>
        private static (Int32Rect crop, bool transparent, Color edge) Trim(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            var full = new Int32Rect(0, 0, w, h);
            try
            {
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int stride = w * 4;
                var px = new byte[stride * h];
                conv.CopyPixels(px, stride, 0);
                byte A(int x, int y) => px[y * stride + x * 4 + 3];
                int corner = 0;
                bool transparent = A(0, 0) < 200 || A(w - 1, 0) < 200 || A(0, h - 1) < 200 || A(w - 1, h - 1) < 200;
                Color edge = Color.FromRgb(px[corner + 2], px[corner + 1], px[corner]);

                bool Content(int x, int y)
                {
                    int i = y * stride + x * 4;
                    if (transparent) return px[i + 3] > 40;
                    // logo z tłem: piksel różni się wyraźnie od koloru narożnika
                    return Math.Abs(px[i] - edge.B) + Math.Abs(px[i + 1] - edge.G) + Math.Abs(px[i + 2] - edge.R) > 60;
                }

                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (Content(x, y))
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                if (maxX < minX || maxY < minY) return (full, transparent, edge);
                // mały oddech wokół logo
                int m = Math.Max(1, (int)(Math.Max(maxX - minX, maxY - minY) * 0.03));
                minX = Math.Max(0, minX - m); minY = Math.Max(0, minY - m);
                maxX = Math.Min(w - 1, maxX + m); maxY = Math.Min(h - 1, maxY + m);
                return (new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1), transparent, edge);
            }
            catch
            {
                return (full, true, Colors.Black);
            }
        }

        /// <summary>Średnia jasność nieprzezroczystych pikseli (0-1).</summary>
        private static double Luminance(BitmapSource src)
        {
            try
            {
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                conv.CopyPixels(px, stride, 0);
                double sum = 0, n = 0;
                bool anyTransparent = false;
                for (int i = 0; i < px.Length; i += 4 * Math.Max(1, (w * h) / 4096))
                {
                    byte a = px[i + 3];
                    if (a < 128) { anyTransparent = true; continue; }
                    sum += (0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2]) / 255.0;
                    n++;
                }
                // logo bez przezroczystości ma własne tło - wtedy tło kafelka dopasowujemy do niego
                double lum = n > 0 ? sum / n : 0.5;
                return anyTransparent ? lum : 1 - lum < 0.5 ? 0.0 : 1.0;
            }
            catch { return 0.5; }
        }
    }
}
