using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IslandPlayer
{
    /// <summary>
    /// własny, duży bufor radia (zamiast małego bufora odtwarzacza Windows).
    ///  • jedno połączenie na wszystko - dźwięk i tytuły piosenek (ICY) z tego samego strumienia,
    ///  • start po 1,5 s, a zapas rośnie w tle do 10 s (chroni przed zacięciami sieci),
    ///  • gdy zapas mimo to się skończy (dłuższa awaria), zbieramy 5 s, zanim znowu zagramy - bez „rwania”,
    ///  • zerwane połączenie odnawiamy w tle - odtwarzacz dostaje ciągły strumień i nie musi startować od nowa.
    /// Dla odtwarzacza wygląda to jak zwykły plik, który ciągle „rośnie”.
    /// </summary>
    public sealed class LiveRadioStream : Stream
    {
        private static readonly HttpClient Http = CreateClient();

        private readonly string _url;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _lock = new object();
        private readonly List<byte[]> _blocks = new List<byte[]>();   // dane od _base
        private long _base;          // pozycja pierwszego trzymanego bajtu
        private long _written;       // ile bajtów dźwięku przyszło łącznie
        private long _pos;           // pozycja odczytu odtwarzacza
        private TaskCompletionSource<bool> _dataSignal = NewSignal();
        private byte[] _head;        // pierwsze 256 KB zawsze w pamięci (odtwarzacz czasem do nich wraca)

        private double _bytesPerSecond;
        private double _targetSeconds = StartSeconds;
        /// <summary>
        /// Start już po 1,5 s (szybko), docelowy zapas 10 s budowany potem niezauważalnie
        /// (odtwarzanie o 3% wolniej, aż zapas urośnie), po zerwaniu połączenia - 2,5 s przed wznowieniem.
        /// </summary>
        public const double StartSeconds = 1.5, GoalSeconds = 10, RefillSeconds = 2.5;
        private bool _refilling = true;    // zbieramy zapas - odtwarzacz czeka

        public string ContentType { get; private set; } = "audio/mpeg";
        public int Reconnects { get; private set; }
        public int Underruns { get; private set; }
        /// <summary>Ile sekund dźwięku czeka w zapasie.</summary>
        public double BufferedSeconds { get { lock (_lock) return _bytesPerSecond > 0 ? (_written - _pos) / _bytesPerSecond : 0; } }
        public double TargetSeconds => _targetSeconds;

        public event Action<string> TitleChanged;
        /// <summary>Połączenie się nie udaje (np. stacja nie działa) - do przełączenia na inny adres.</summary>
        public event Action Failed;

        private static TaskCompletionSource<bool> NewSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(8),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.None,
            };
            var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Isabelle/1.5");
            return c;
        }

        public LiveRadioStream(string url, int bitrateKbps)
        {
            _url = url;
            _bytesPerSecond = Math.Max(4, bitrateKbps > 0 ? bitrateKbps : 128) * 1000 / 8.0;
        }

        /// <summary>Łączy się i czeka na pierwszy zapas. False = stacja nie odpowiada.</summary>
        public async Task<bool> StartAsync()
        {
            var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => Pump(first));
            var done = await Task.WhenAny(first.Task, Task.Delay(10000));
            return done == first.Task && first.Task.Result;
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch { }
            lock (_lock) _dataSignal.TrySetResult(false);
        }

        // ================= pobieranie =================
        private async Task Pump(TaskCompletionSource<bool> first)
        {
            int failures = 0;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, _url);
                    req.Headers.Add("Icy-MetaData", "1");
                    using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                    res.EnsureSuccessStatusCode();
                    string ct = res.Content.Headers.ContentType?.MediaType ?? "";
                    if (ct.Contains("aac") || ct.Contains("mp4")) ContentType = "audio/aac";
                    else if (ct.Contains("mpeg") || ct.Contains("mp3")) ContentType = "audio/mpeg";
                    if (Header(res, "icy-br") is string br && int.TryParse(br.Split(',')[0], out int kbps) && kbps >= 16 && kbps <= 1000)
                        _bytesPerSecond = kbps * 1000 / 8.0;
                    int metaint = int.TryParse(Header(res, "icy-metaint"), out int mi) && mi > 0 && mi <= 512 * 1024 ? mi : 0;

                    using var s = await res.Content.ReadAsStreamAsync(_cts.Token);
                    failures = 0;
                    var buf = new byte[16 * 1024];
                    int untilMeta = metaint;
                    while (!_cts.IsCancellationRequested)
                    {
                        int want = metaint > 0 ? Math.Min(buf.Length, untilMeta) : buf.Length;
                        // brak danych przez 15 s = martwe połączenie - zrywamy i łączymy od nowa
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        stall.CancelAfter(TimeSpan.FromSeconds(15));
                        int n = await s.ReadAsync(buf.AsMemory(0, want), stall.Token);
                        if (n <= 0) throw new EndOfStreamException();
                        Append(buf, n);
                        // wiele serwerów na starcie wysyła od razu kilka sekund - wtedy ruszamy praktycznie natychmiast
                        if (first != null && BufferedSeconds >= _targetSeconds) { first.TrySetResult(true); first = null; }
                        if (metaint > 0 && (untilMeta -= n) == 0)
                        {
                            await ReadMeta(s);
                            untilMeta = metaint;
                        }
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (_cts.IsCancellationRequested) break;
                    failures++;
                    Reconnects++;
                    Log.Info("radio stream: " + ex.Message + " (próba " + failures + ")");
                    if (first != null && failures >= 3) { first.TrySetResult(false); first = null; }
                    if (failures >= 6) { Failed?.Invoke(); }
                    // szybko za pierwszym razem, potem coraz wolniej (max 8 s)
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 0.3 * Math.Pow(2, failures - 1))), _cts.Token); }
                    catch { break; }
                }
            }
            first?.TrySetResult(false);
            lock (_lock) _dataSignal.TrySetResult(false);
        }

        private static string Header(HttpResponseMessage res, string name) =>
            res.Headers.TryGetValues(name, out var v) ? string.Join(",", v) :
            res.Content.Headers.TryGetValues(name, out var v2) ? string.Join(",", v2) : null;

        private async Task ReadMeta(Stream s)
        {
            var one = new byte[1];
            if (await s.ReadAsync(one.AsMemory(0, 1), _cts.Token) <= 0) throw new EndOfStreamException();
            int len = one[0] * 16;
            if (len == 0) return;
            var meta = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = await s.ReadAsync(meta.AsMemory(got, len - got), _cts.Token);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
            string text = Encoding.UTF8.GetString(meta).TrimEnd('\0');
            if (text.Contains('�')) text = Encoding.Latin1.GetString(meta).TrimEnd('\0');
            const string key = "StreamTitle='";
            int i = text.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return;
            i += key.Length;
            int j = text.IndexOf("';", i, StringComparison.Ordinal);
            if (j < 0) j = text.LastIndexOf('\'');
            if (j <= i) return;
            string title = text.Substring(i, j - i).Trim();
            if (title.Length > 0) TitleChanged?.Invoke(title);
        }

        private void Append(byte[] buf, int n)
        {
            var copy = new byte[n];
            Buffer.BlockCopy(buf, 0, copy, 0, n);
            TaskCompletionSource<bool> signal;
            lock (_lock)
            {
                if (_head == null || _head.Length < 256 * 1024)
                {
                    int take = (int)Math.Min(n, 256 * 1024 - (_head?.Length ?? 0));
                    var h = new byte[(_head?.Length ?? 0) + take];
                    if (_head != null) Buffer.BlockCopy(_head, 0, h, 0, _head.Length);
                    Buffer.BlockCopy(copy, 0, h, h.Length - take, take);
                    _head = h;
                }
                _blocks.Add(copy);
                _written += n;
                // trzymamy maks. ~16 MB za pozycją odczytu (godziny słuchania nie zapchają pamięci)
                while (_blocks.Count > 1 && _base + _blocks[0].Length < _pos - 16L * 1024 * 1024)
                {
                    _base += _blocks[0].Length;
                    _blocks.RemoveAt(0);
                }
                signal = _dataSignal;
                _dataSignal = NewSignal();
            }
            signal.TrySetResult(true);
        }

        // ================= odczyt (odtwarzacz) =================
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            await ReadAsync(buffer.AsMemory(offset, count), ct);

        public override async ValueTask<int> ReadAsync(Memory<byte> dest, CancellationToken ct = default)
        {
            if (dest.Length == 0) return 0;
            DateTime? waitingSince = null;
            while (true)
            {
                Task<bool> wait;
                lock (_lock)
                {
                    // odtwarzacz zagląda daleko w przód (np. po znaczniki na końcu pliku) - dostaje ciszę
                    if (_pos > _written + 2L * 1024 * 1024)
                    {
                        dest.Span.Clear();
                        _pos += dest.Length;
                        return dest.Length;
                    }
                    double lead = _bytesPerSecond > 0 ? (_written - _pos) / _bytesPerSecond : 0;
                    if (_refilling && lead >= _targetSeconds) _refilling = false;
                    // dane są - oddajemy od razu (bez sztucznego czekania)
                    if (!_refilling && _written > _pos) return CopyOut(dest.Span);
                    // brak danych dłużej niż pół sekundy = prawdziwe zacięcie: skoro i tak jest cisza,
                    // zbieramy kilka sekund zapasu, żeby dalej nie „rwało” co chwilę
                    if (!_refilling && waitingSince.HasValue && (DateTime.UtcNow - waitingSince.Value).TotalSeconds >= 0.5)
                    {
                        _refilling = true;
                        Underruns++;
                        _targetSeconds = RefillSeconds;
                    }
                    if (_cts.IsCancellationRequested) return 0;
                    wait = _dataSignal.Task;
                }
                waitingSince ??= DateTime.UtcNow;
                // ciągle łączymy się od nowa w tle - czekamy cierpliwie (do 60 s), potem „koniec strumienia”
                var done = await Task.WhenAny(wait, Task.Delay(waitingSince.Value.AddSeconds(60) - DateTime.UtcNow > TimeSpan.Zero
                    ? waitingSince.Value.AddSeconds(60) - DateTime.UtcNow : TimeSpan.Zero, ct));
                if (done != wait)
                {
                    lock (_lock) if (_written > _pos) { _refilling = false; continue; }
                    return 0;
                }
                if (!wait.Result) { lock (_lock) if (_written <= _pos) return 0; }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        private int CopyOut(Span<byte> dest)
        {
            long pos = _pos;
            int total = 0;
            // z „głowy” strumienia (pierwsze 256 KB), gdy odtwarzacz do niej wrócił
            if (pos < _base && _head != null && pos < _head.Length)
            {
                int n = (int)Math.Min(dest.Length, _head.Length - pos);
                _head.AsSpan((int)pos, n).CopyTo(dest);
                _pos += n;
                return n;
            }
            if (pos < _base) { _pos = _base; pos = _base; }
            long blockStart = _base;
            foreach (var b in _blocks)
            {
                long blockEnd = blockStart + b.Length;
                if (pos < blockEnd && total < dest.Length)
                {
                    int from = (int)(pos - blockStart);
                    int n = Math.Min(b.Length - from, dest.Length - total);
                    b.AsSpan(from, n).CopyTo(dest.Slice(total));
                    total += n;
                    pos += n;
                }
                blockStart = blockEnd;
                if (total >= dest.Length) break;
            }
            _pos = pos;
            return total;
        }

        // ================= reszta interfejsu Stream =================
        public override bool CanRead => true;
        public override bool CanSeek => true;    // wymagane przez odtwarzacz Windows; przewijamy tylko w obrębie bufora
        public override bool CanWrite => false;
        public override long Length => long.MaxValue / 4;     // „nieskończony” plik
        public override long Position
        {
            get { lock (_lock) return _pos; }
            set { lock (_lock) _pos = Math.Max(0, value); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_lock)
            {
                _pos = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _pos + offset,
                    _ => Length + offset,
                };
                if (_pos < 0) _pos = 0;
                return _pos;
            }
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Stop();
            base.Dispose(disposing);
        }
    }
}
