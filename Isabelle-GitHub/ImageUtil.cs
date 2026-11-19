using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>Okładki: wycięcie paska z logo Spotify, przycięcie do kwadratu, kolor akcentu.</summary>
    public static class ImageUtil
    {
        public static ulong Hash(byte[] data)
        {
            ulong h = 14695981039346656037UL; // FNV-1a
            foreach (byte b in data)
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h;
        }

        public static (BitmapSource, Color) ProcessCover(byte[] data, string appId)
        {
            try
            {
                BitmapSource src;
                using (var ms = new MemoryStream(data))
                {
                    var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    src = dec.Frames[0];
                }
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                conv.CopyPixels(px, stride, 0);

                Int32Rect crop = new Int32Rect(0, 0, w, h);
                if ((appId ?? "").IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0)
                    crop = SpotifyCrop(px, w, h);

                int sq = Math.Min(crop.Width, crop.Height);
                crop = new Int32Rect(crop.X + (crop.Width - sq) / 2, crop.Y + (crop.Height - sq) / 2, sq, sq);

                var full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
                BitmapSource cropped = new CroppedBitmap(full, crop);
                double sc = 300.0 / sq;
                BitmapSource scaled = new TransformedBitmap(cropped, new ScaleTransform(sc, sc));
                var final = new WriteableBitmap(scaled);
                final.Freeze();
                return (final, Accent(px, w, crop));
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return (null, Color.FromRgb(30, 215, 96));
            }
        }

        /// <summary>Spotify dokleja tło i pasek z logo - szukamy granic samej okładki.</summary>
        private static Int32Rect SpotifyCrop(byte[] px, int w, int h)
        {
            int Idx(int x, int y) => (y * w + x) * 4;
            int bi = Idx(1, h - 2);
            byte bb = px[bi], bg = px[bi + 1], br = px[bi + 2];
            bool Differs(int x, int y)
            {
                int i = Idx(x, y);
                return Math.Max(Math.Abs(px[i] - bb), Math.Max(Math.Abs(px[i + 1] - bg), Math.Abs(px[i + 2] - br))) > 28;
            }
            int scanH = (int)(h * 0.7);
            int step = Math.Max(1, w / 150);
            bool ColHas(int x)
            {
                for (int y = 0; y < scanH; y += step) if (Differs(x, y)) return true;
                return false;
            }
            int left = 0;
            for (int x = 0; x < w / 2; x++) if (ColHas(x)) { left = x; break; }
            int right = w;
            for (int x = w - 1; x > w / 2; x--) if (ColHas(x)) { right = x + 1; break; }
            int aw = right - left;
            int top = 0;
            for (int y = 0; y < h / 2; y++)
            {
                bool any = false;
                for (int x = left; x < right; x += step) if (Differs(x, y)) { any = true; break; }
                if (any) { top = y; break; }
            }
            bool plausible = aw >= w * 0.55 && aw <= w * 0.95 && top + aw <= h && Math.Abs(left - (w - right)) <= w * 0.06;
            if (!plausible)
            {
                aw = (int)(w * 0.78);
                left = (w - aw) / 2;
                top = 0;
            }
            return new Int32Rect(left, top, Math.Min(aw, w - left), Math.Min(aw, h - top));
        }

        /// <summary>Najbardziej "soczysty" kolor okładki (jak akcent w iOS).</summary>
        private static Color Accent(byte[] px, int w, Int32Rect r)
        {
            double best = -1, bh = 0, bs = 0, bv = 0;
            int step = Math.Max(1, r.Width / 24);
            for (int y = r.Y; y < r.Y + r.Height; y += step)
            {
                for (int x = r.X; x < r.X + r.Width; x += step)
                {
                    int i = (y * w + x) * 4;
                    RgbToHsv(px[i + 2], px[i + 1], px[i], out double hh, out double ss, out double vv);
                    double score = ss * vv + (vv > 0.35 ? 0.15 : 0);
                    if (score > best) { best = score; bh = hh; bs = ss; bv = vv; }
                }
            }
            if (bs < 0.18) return Color.FromRgb(225, 225, 230);
            bs = Math.Min(Math.Max(bs, 0.45), 0.8);
            bv = Math.Max(bv, 0.9);
            return HsvToColor(bh, bs, bv);
        }

        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0;
            else if (max == rf) h = ((gf - bf) / d + 6) % 6;
            else if (max == gf) h = (bf - rf) / d + 2;
            else h = (rf - gf) / d + 4;
            h /= 6;
        }

        private static Color HsvToColor(double h, double s, double v)
        {
            double hh = h * 6;
            int i = (int)Math.Floor(hh) % 6;
            double f = hh - Math.Floor(hh);
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
