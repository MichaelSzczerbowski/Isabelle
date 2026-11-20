using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandPlayer
{
    /// <summary>
    /// Status na Discordzie ("Słucha ...") z aktualnie śpiewaną linijką tekstu.
    /// Łączy się z aplikacją Discord na tym komputerze przez lokalny potok (bez internetu, bez logowania).
    /// </summary>
    public sealed class DiscordPresence : IDisposable
    {
        /// <summary>Application ID z https://discord.com/developers/applications (aplikacja "Isabelle").</summary>
        public const string ClientId = "1573360659687678213";
        private const string ServerUrl = "https://discord.gg/ueUYTw8ry6";
        private const string ServerText = "discord.gg/ueUYTw8ry6";
        public static bool Configured => !ClientId.StartsWith("DISCORD_", StringComparison.Ordinal);

        private NamedPipeClientStream _pipe;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private DateTime _nextConnect = DateTime.MinValue;
        private DateTime _lastSend = DateTime.MinValue;
        private string _lastKey;
        private bool _cleared = true;

        public bool Connected => _pipe != null && _pipe.IsConnected;

        /// <summary>Wołane co sekundę z wątku interfejsu; samo pilnuje limitu Discorda (ok. 1 zmiana na 4 s).</summary>
        public void Update(NowPlayingState s, bool lyrics)
        {
            if (!Configured) return;
            bool show = s != null && s.HasMedia && s.Playing;
            string line = lyrics && show ? (s.Lyric ?? "") : "";
            string key = show ? s.Title + "|" + s.Artist + "|" + line + "|" + s.CoverUrl : "";
            if (key == _lastKey) return;
            if ((DateTime.UtcNow - _lastSend).TotalSeconds < 4) return;
            if (!show && _cleared) { _lastKey = key; return; }

            _lastKey = key;
            _lastSend = DateTime.UtcNow;
            string payload = show ? ActivityJson(s, line) : ClearJson();
            _cleared = !show;
            _ = Task.Run(() => SendAsync(payload));
        }

        public void Clear()
        {
            if (!Configured || _cleared) return;
            _cleared = true;
            _lastKey = "";
            string payload = ClearJson();
            _ = Task.Run(() => SendAsync(payload));
        }

        private static string Fit(string text, int max)
        {
            text = (text ?? "").Trim();
            if (text.Length < 2) text = (text + "  ").Substring(0, 2); // Discord wymaga min. 2 znaków
            return text.Length > max ? text.Substring(0, max - 1) + "…" : text;
        }

        private static string ActivityJson(NowPlayingState s, string line)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            object timestamps = null;
            if (s.Duration > 0)
            {
                long start = now - (long)(s.Position * 1000);
                timestamps = new { start, end = start + (long)(s.Duration * 1000) };
            }
            var activity = new
            {
                type = 2, // "Słucha"
                details = Fit(s.Title, 128),
                state = Fit(string.IsNullOrWhiteSpace(line) ? s.Artist : "🎤 " + line, 128),
                timestamps,
                // trzecia linijka: wykonawca, gdy w statusie jest tekst piosenki - inaczej link do naszego serwera
                // duża grafika: okładka piosenki, mała w rogu: nasze logo (bez okładki - samo logo)
                assets = string.IsNullOrEmpty(s.CoverUrl)
                    ? (object)new { large_image = "logo", large_text = Fit(string.IsNullOrWhiteSpace(line) ? ServerText : s.Artist, 128) }
                    : new
                    {
                        large_image = s.CoverUrl, large_text = Fit(string.IsNullOrWhiteSpace(line) ? ServerText : s.Artist, 128),
                        small_image = "logo", small_text = "Isabelle",
                    },
                // klikalny przycisk (widzą go inni - na własnym profilu Discord go ukrywa)
                buttons = new[] { new { label = "Serwer Isabelle", url = ServerUrl } },
            };
            return JsonSerializer.Serialize(new
            {
                cmd = "SET_ACTIVITY",
                args = new { pid = Environment.ProcessId, activity },
                nonce = Guid.NewGuid().ToString(),
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        }

        private static string ClearJson() => JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId },
            nonce = Guid.NewGuid().ToString(),
        });

        private async Task SendAsync(string json)
        {
            await _gate.WaitAsync();
            try
            {
                if (!Connected && !TryConnect())
                {
                    _lastKey = null;      // Discord wyłączony - spróbujemy znowu przy następnej aktualizacji
                    return;
                }
                WriteFrame(1, json);
            }
            catch (Exception ex)
            {
                Log.Info("discord: " + ex.Message);
                Drop();
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool TryConnect()
        {
            if (DateTime.UtcNow < _nextConnect) return false;
            _nextConnect = DateTime.UtcNow.AddSeconds(15);
            for (int i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    pipe.Connect(150);
                    _pipe = pipe;
                    WriteFrame(0, JsonSerializer.Serialize(new { v = 1, client_id = ClientId }));
                    // czekamy na READY od Discorda - wcześniej wysłany status bywa ignorowany
                    var head = new byte[8];
                    var ready = ReadExact(pipe, head, 8);
                    if (!ready.Wait(3000) || !ready.Result) throw new IOException("no READY");
                    int len = BitConverter.ToInt32(head, 4);
                    if (len < 0 || len > 1 << 20) throw new IOException("bad frame");
                    var body = new byte[len];
                    var rb = ReadExact(pipe, body, len);
                    if (!rb.Wait(3000) || !rb.Result) throw new IOException("no READY");
                    if (BitConverter.ToInt32(head, 0) == 2) throw new IOException("closed: " + Encoding.UTF8.GetString(body));
                    _ = Task.Run(ReadLoop);  // odpowiedzi Discorda trzeba odbierać, inaczej potok się zapcha
                    return true;
                }
                catch (Exception ex)
                {
                    if (ReferenceEquals(_pipe, pipe)) { _pipe = null; Log.Info("discord: " + ex.Message); }
                    pipe.Dispose();
                }
            }
            return false; // Discord nie jest uruchomiony
        }

        private void WriteFrame(int op, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            _pipe.Write(frame, 0, frame.Length);
            _pipe.Flush();
        }

        private async Task ReadLoop()
        {
            var pipe = _pipe;
            var head = new byte[8];
            try
            {
                while (pipe != null && pipe.IsConnected)
                {
                    if (!await ReadExact(pipe, head, 8)) break;
                    int len = BitConverter.ToInt32(head, 4);
                    if (len < 0 || len > 1 << 20) break;
                    var body = new byte[len];
                    if (!await ReadExact(pipe, body, len)) break;
                }
            }
            catch { }
            if (ReferenceEquals(pipe, _pipe)) Drop();
        }

        private static async Task<bool> ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = await s.ReadAsync(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private void Drop()
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
            _lastKey = null;
        }

        public void Dispose()
        {
            try
            {
                if (Connected) WriteFrame(1, ClearJson());
            }
            catch { }
            Drop();
        }
    }
}
