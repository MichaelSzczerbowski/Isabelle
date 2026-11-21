using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace IslandPlayer
{
    /// <summary>
    /// Korektor dźwięku (10 pasm) dla Mojej muzyki. Ustawienia w Settings (EqEnabled, EqPreset, EqCustom).
    /// Zmiana gotowca / suwaka działa od razu w grającym utworze.
    /// </summary>
    public static class Equalizer
    {
        public static readonly double[] Bands = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        public static readonly string[] BandLabels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
        public const double MaxDb = 12;

        public static readonly (string key, string pl, string en, double[] gains)[] Presets =
        {
            ("flat", "Bez zmian", "Flat", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            ("bass", "Więcej basu", "Bass boost", new double[] { 6, 5.5, 4.5, 2.5, 0.5, 0, 0, 0, 0, 0 }),
            ("vshape", "Bas i soprany", "Bass & treble", new double[] { 5, 4, 2.5, 0.5, -1, -1, 0, 2, 3.5, 4.5 }),
            ("vocal", "Wokal", "Vocal", new double[] { -2, -2, -1, 0.5, 2.5, 3.5, 3, 2, 0.5, -1 }),
            ("rock", "Rock", "Rock", new double[] { 4, 3.5, 2, 0, -1, -0.5, 1.5, 3, 3.5, 4 }),
            ("pop", "Pop", "Pop", new double[] { -1, 0, 1.5, 3, 3, 1.5, 0, -0.5, 0.5, 1 }),
            ("electronic", "Elektroniczna", "Electronic", new double[] { 5, 4.5, 1.5, 0, -1.5, 1, 0.5, 1.5, 4, 5 }),
            ("acoustic", "Akustyczna", "Acoustic", new double[] { 3, 3, 2, 1, 1.5, 1.5, 2.5, 3, 2.5, 1.5 }),
            ("night", "Nocna (cichsze basy)", "Night (softer bass)", new double[] { -5, -4, -2.5, -1, 0.5, 1, 1, 0, -1, -2 }),
        };

        private static double[] _gains = new double[10];
        private static int _version = 1;
        public static event Action Changed;

        /// <summary>Numer zmiany - strumień przelicza filtry, gdy się zmieni.</summary>
        public static int Version => System.Threading.Volatile.Read(ref _version);

        public static bool Enabled => App.Config?.EqEnabled == true;

        /// <summary>Korektor coś zmienia (włączony i nie wszystkie suwaki na zerze).</summary>
        public static bool Active
        {
            get
            {
                if (!Enabled) return false;
                foreach (var g in _gains) if (Math.Abs(g) > 0.05) return true;
                return false;
            }
        }

        public static double[] Gains => (double[])_gains.Clone();

        public static string PresetKey => string.IsNullOrEmpty(App.Config?.EqPreset) ? "flat" : App.Config.EqPreset;

        /// <summary>Wczytanie z ustawień (przy starcie).</summary>
        public static void Load()
        {
            var c = App.Config;
            if (c == null) return;
            double[] g = null;
            if (c.EqPreset == "custom") g = c.EqCustom;
            else if (PresetKey.StartsWith(UserPrefix)) g = FindUser(PresetKey.Substring(UserPrefix.Length))?.Gains ?? c.EqCustom;
            else foreach (var p in Presets) if (p.key == PresetKey) g = p.gains;
            Apply(g);
        }

        public static void SetEnabled(bool on)
        {
            App.Config.EqEnabled = on;
            App.Config.Save();
            Bump();
            LocalPlayer.RefreshEqualizer();
        }

        // ================= własne ustawienia =================
        public const string UserPrefix = "user:";

        public static List<EqUserPreset> UserPresets => App.Config.EqUserPresets ??= new List<EqUserPreset>();

        private static EqUserPreset FindUser(string name) =>
            UserPresets.Find(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

        /// <summary>Zapisuje obecne suwaki pod nazwą (ta sama nazwa = nadpisanie).</summary>
        public static void SaveUserPreset(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return;
            if (name.Length > 30) name = name.Substring(0, 30);
            var p = FindUser(name);
            if (p == null) UserPresets.Add(p = new EqUserPreset { Name = name });
            p.Gains = Gains;
            App.Config.EqPreset = UserPrefix + p.Name;
            App.Config.EqCustom = Gains;
            App.Config.Save();
            Bump();
        }

        public static void RenameUserPreset(string oldName, string newName)
        {
            newName = (newName ?? "").Trim();
            var p = FindUser(oldName);
            if (p == null || newName.Length == 0) return;
            if (newName.Length > 30) newName = newName.Substring(0, 30);
            var clash = FindUser(newName);
            if (clash != null && !ReferenceEquals(clash, p)) UserPresets.Remove(clash);
            p.Name = newName;
            if (PresetKey == UserPrefix + oldName) App.Config.EqPreset = UserPrefix + newName;
            App.Config.Save();
            Bump();
        }

        public static void DeleteUserPreset(string name)
        {
            var p = FindUser(name);
            if (p == null) return;
            UserPresets.Remove(p);
            // usunięte ustawienie, które było włączone - dźwięk zostaje taki sam, tylko jako „własne”
            if (string.Equals(PresetKey, UserPrefix + name, StringComparison.CurrentCultureIgnoreCase)) App.Config.EqPreset = "custom";
            App.Config.Save();
            Bump();
        }

        public static void SetPreset(string key)
        {
            if (key != null && key.StartsWith(UserPrefix))
            {
                var u = FindUser(key.Substring(UserPrefix.Length));
                if (u == null) return;
                App.Config.EqPreset = UserPrefix + u.Name;
                App.Config.EqCustom = (double[])(u.Gains ?? new double[10]).Clone();
                App.Config.Save();
                Apply(u.Gains);
                return;
            }
            foreach (var p in Presets)
            {
                if (p.key != key) continue;
                App.Config.EqPreset = key;
                App.Config.EqCustom = (double[])p.gains.Clone();
                App.Config.Save();
                Apply(p.gains);
                return;
            }
        }

        /// <summary>Ruch suwaka: własne ustawienie („custom”). Zapis do pliku robi SaveCustom po puszczeniu suwaka.</summary>
        public static void SetBand(int band, double db)
        {
            if (band < 0 || band >= _gains.Length) return;
            var g = Gains;
            g[band] = Math.Round(Math.Clamp(db, -MaxDb, MaxDb) * 2) / 2;
            App.Config.EqPreset = "custom";
            App.Config.EqCustom = g;
            Apply(g);
        }

        public static void SaveCustom() => App.Config.Save();

        private static void Apply(double[] g)
        {
            var n = new double[10];
            if (g != null) for (int i = 0; i < Math.Min(10, g.Length); i++) n[i] = Math.Clamp(g[i], -MaxDb, MaxDb);
            _gains = n;
            Bump();
        }

        private static void Bump()
        {
            System.Threading.Interlocked.Increment(ref _version);
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Error(ex); }
        }
    }

    /// <summary>Filtr dwubiegunowy (wzory RBJ „Audio EQ Cookbook”).</summary>
    internal struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        public void Set(int kind, double rate, double freq, double q, double db)
        {
            freq = Math.Min(freq, rate * 0.45);
            double a = Math.Pow(10, db / 40), w = 2 * Math.PI * freq / rate, cos = Math.Cos(w), sin = Math.Sin(w);
            double b0, b1, b2, a0, a1, a2;
            if (kind == 0)
            {
                // pasmo środkowe (peaking)
                double alpha = sin / (2 * q);
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
            }
            else
            {
                // półka niska (kind 1) albo wysoka (kind 2), nachylenie 1
                double alpha = sin / 2 * Math.Sqrt(2), sa = 2 * Math.Sqrt(a) * alpha;
                if (kind == 1)
                {
                    b0 = a * ((a + 1) - (a - 1) * cos + sa); b1 = 2 * a * ((a - 1) - (a + 1) * cos); b2 = a * ((a + 1) - (a - 1) * cos - sa);
                    a0 = (a + 1) + (a - 1) * cos + sa; a1 = -2 * ((a - 1) + (a + 1) * cos); a2 = (a + 1) + (a - 1) * cos - sa;
                }
                else
                {
                    b0 = a * ((a + 1) + (a - 1) * cos + sa); b1 = -2 * a * ((a - 1) + (a + 1) * cos); b2 = a * ((a + 1) + (a - 1) * cos - sa);
                    a0 = (a + 1) - (a - 1) * cos + sa; a1 = 2 * ((a - 1) - (a + 1) * cos); a2 = (a + 1) - (a - 1) * cos - sa;
                }
            }
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public void Reset() { _z1 = _z2 = 0; }

        public double Process(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }

    /// <summary>
    /// Plik muzyczny jako strumień WAV (16 bit) przepuszczony przez korektor. Odtwarzacz Windows czyta go jak zwykły
    /// plik (przewijanie działa), więc wyspa, klawisze multimedialne i okładki działają bez zmian.
    /// </summary>
    internal sealed class EqStream : Stream
    {
        private const int HeaderSize = 44;
        private const int ChunkFrames = 8192;

        private readonly MediaFoundationReader _reader;
        private readonly int _ch, _rate;
        private readonly long _frames;
        private readonly byte[] _header;
        private readonly object _lock = new object();
        private long _pos, _nextFrame;
        private byte[] _chunk = Array.Empty<byte>(), _in = Array.Empty<byte>();
        private long _chunkStart = -1;
        private int _chunkLen;
        private Biquad[] _filters;
        private int _version = -1;
        private double _pre = 1;
        private bool _bypass = true, _disposed;

        // ostatnio otwarte strumienie - stare zamykamy (odtwarzacz trzyma otwarte najwyżej kilka utworów)
        private static readonly List<EqStream> Open = new List<EqStream>();

        private EqStream(MediaFoundationReader reader)
        {
            _reader = reader;
            var f = reader.WaveFormat;
            _ch = f.Channels;
            _rate = f.SampleRate;
            _frames = reader.Length / (_ch * 4);
            _filters = new Biquad[_ch * Equalizer.Bands.Length];
            long data = _frames * _ch * 2;
            _header = new byte[HeaderSize];
            using (var w = new BinaryWriter(new MemoryStream(_header)))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write((uint)Math.Min(uint.MaxValue, data + 36));
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);                 // PCM
                w.Write((short)_ch);
                w.Write(_rate);
                w.Write(_rate * _ch * 2);
                w.Write((short)(_ch * 2));
                w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write((uint)Math.Min(uint.MaxValue, data));
            }
        }

        /// <summary>Otwiera plik przez dekoder Windows (MP3, FLAC, M4A, WAV, WMA…). Null = nie da się (wtedy gramy bez korektora).</summary>
        public static EqStream TryOpen(string path)
        {
            try
            {
                var settings = new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true, RepositionInRead = true };
                var reader = new MediaFoundationReader(path, settings);
                var f = reader.WaveFormat;
                if (f.Encoding != WaveFormatEncoding.IeeeFloat || f.BitsPerSample != 32 || f.Channels < 1 || f.Channels > 8 || reader.Length <= 0)
                {
                    reader.Dispose();
                    return null;
                }
                var s = new EqStream(reader);
                lock (Open)
                {
                    Open.Add(s);
                    while (Open.Count > 6) { var old = Open[0]; Open.RemoveAt(0); old.Dispose(); }
                }
                return s;
            }
            catch (Exception ex)
            {
                Log.Info("korektor: nie można otworzyć " + path + ": " + ex.Message);
                return null;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => HeaderSize + _frames * _ch * 2;

        public override long Position
        {
            get { lock (_lock) return _pos; }
            set { lock (_lock) _pos = Math.Clamp(value, 0, Length); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_lock)
            {
                long p = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : Length + offset;
                _pos = Math.Clamp(p, 0, Length);
                return _pos;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (_disposed) return 0;
                int total = 0;
                long len = Length;
                while (count > 0 && _pos < len)
                {
                    int n;
                    if (_pos < HeaderSize)
                    {
                        n = (int)Math.Min(count, HeaderSize - _pos);
                        Buffer.BlockCopy(_header, (int)_pos, buffer, offset, n);
                    }
                    else
                    {
                        long d = _pos - HeaderSize;
                        if (_chunkStart < 0 || d < _chunkStart || d >= _chunkStart + _chunkLen) Fill(d);
                        if (_chunkLen <= 0) break;
                        int off = (int)(d - _chunkStart);
                        n = Math.Min(count, _chunkLen - off);
                        Buffer.BlockCopy(_chunk, off, buffer, offset, n);
                    }
                    _pos += n;
                    offset += n;
                    count -= n;
                    total += n;
                }
                return total;
            }
        }

        /// <summary>Dekoduje kawałek od ramki zawierającej bajt d, przepuszcza przez korektor i zamienia na 16 bit.</summary>
        private void Fill(long d)
        {
            int outBlock = _ch * 2, inBlock = _ch * 4;
            long frame = d / outBlock;
            int frames = (int)Math.Min(ChunkFrames, _frames - frame);
            if (frames <= 0) { _chunkLen = 0; return; }
            if (frame != _nextFrame)
            {
                // przewinięcie - dekoder skacze w nowe miejsce, filtry startują od zera (bez „ogona” z poprzedniego miejsca)
                try { _reader.Position = frame * inBlock; } catch (Exception ex) { Log.Info("korektor: przewijanie " + ex.Message); }
                for (int i = 0; i < _filters.Length; i++) _filters[i].Reset();
            }
            int need = frames * inBlock;
            if (_in.Length < need) _in = new byte[need];
            int got = 0;
            try
            {
                while (got < need)
                {
                    int r = _reader.Read(_in, got, need - got);
                    if (r <= 0) break;
                    got += r;
                }
            }
            catch (Exception ex) { Log.Info("korektor: odczyt " + ex.Message); }
            int gotFrames = got / inBlock;
            if (gotFrames < frames) Array.Clear(_in, gotFrames * inBlock, (frames - gotFrames) * inBlock);   // koniec pliku - cisza do końca

            UpdateFilters();
            int outLen = frames * outBlock;
            if (_chunk.Length < outLen) _chunk = new byte[outLen];
            var src = MemoryMarshal.Cast<byte, float>(_in.AsSpan(0, frames * inBlock));
            var dst = MemoryMarshal.Cast<byte, short>(_chunk.AsSpan(0, outLen));
            int bands = Equalizer.Bands.Length;
            for (int i = 0; i < src.Length; i++)
            {
                double x = src[i];
                if (!_bypass)
                {
                    int c = i % _ch, b0 = c * bands;
                    for (int b = 0; b < bands; b++) x = _filters[b0 + b].Process(x);
                    x *= _pre;
                    // łagodne obcięcie zamiast twardego trzasku przy bardzo głośnych fragmentach
                    if (x > 0.95) x = 0.95 + 0.05 * Math.Tanh((x - 0.95) / 0.05);
                    else if (x < -0.95) x = -0.95 + 0.05 * Math.Tanh((x + 0.95) / 0.05);
                }
                dst[i] = (short)Math.Clamp(Math.Round(x * 32767), -32768, 32767);
            }
            _chunkStart = frame * outBlock;
            _chunkLen = outLen;
            _nextFrame = frame + frames;
        }

        private void UpdateFilters()
        {
            int v = Equalizer.Version;
            if (v == _version) return;
            _version = v;
            var g = Equalizer.Gains;
            _bypass = !Equalizer.Active;
            if (_bypass) return;
            double maxBoost = 0;
            int bands = Equalizer.Bands.Length;
            for (int b = 0; b < bands; b++)
            {
                maxBoost = Math.Max(maxBoost, g[b]);
                int kind = b == 0 ? 1 : b == bands - 1 ? 2 : 0;
                for (int c = 0; c < _ch; c++) _filters[c * bands + b].Set(kind, _rate, Equalizer.Bands[b], 1.1, g[b]);
            }
            // ściszenie o część podbicia - podbite pasma nie przesterowują
            _pre = Math.Pow(10, -maxBoost * 0.5 / 20);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _reader.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
