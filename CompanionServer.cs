using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>
    /// Isabelle w telefonie: aplikacja webowa podawana w domowym Wi-Fi (dodaje się ją do ekranu głównego).
    /// Pilot do komputera, tekst piosenki na żywo, biblioteka z komputera grana w telefonie, wyszukiwanie
    /// (muzyka, radio, podcasty, Spotify), Isabelle Wrapped i tryb imprezy. Działa tylko ze stałym kluczem z kodu QR.
    /// </summary>
    public static class CompanionServer
    {
        public const int Port = 43823;
        private static TcpListener _listener;
        private static CancellationTokenSource _cts;
        private static string _page;
        private static byte[] _icon;
        private static readonly object Lock = new object();
        // wyniki wyszukiwania (stacje, podcasty, odcinki, Spotify) - telefon odwołuje się do nich krótkim kluczem
        private static readonly Dictionary<string, object> Tokens = new Dictionary<string, object>();

        public static bool Running => _listener != null;
        public static string Error { get; private set; }
        public static event Action StateChanged;

        /// <summary>Stały klucz (zapamiętany) - aplikacja dodana do ekranu telefonu działa po restarcie komputera.</summary>
        public static string Key
        {
            get
            {
                if (string.IsNullOrEmpty(App.Config.CompanionKey)) { App.Config.CompanionKey = RandomKey(); App.Config.Save(); }
                return App.Config.CompanionKey;
            }
        }

        public static string Url => "http://" + PartyServer.LocalIp() + ":" + Port + "/?k=" + Key;

        /// <summary>Nowy klucz = stare telefony tracą dostęp (trzeba zeskanować kod jeszcze raz).</summary>
        public static void ResetKey()
        {
            App.Config.CompanionKey = RandomKey();
            App.Config.Save();
            StateChanged?.Invoke();
        }

        public static bool Start()
        {
            if (Running) return true;
            try
            {
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _cts = new CancellationTokenSource();
                var tok = _cts.Token;
                Task.Run(() => AcceptLoop(tok));
                Error = null;
                StateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _listener = null;
                Log.Info("telefon: " + ex.Message);
                return false;
            }
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            StateChanged?.Invoke();
        }

        private static async Task AcceptLoop(CancellationToken tok)
        {
            while (!tok.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(tok); }
                catch { break; }
                _ = Task.Run(() => Handle(client));
            }
        }

        // ================= HTTP =================
        private static async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.SendTimeout = 15000;
                    var stream = client.GetStream();
                    var buf = new byte[16384];
                    int n = await stream.ReadAsync(buf, 0, buf.Length);
                    if (n <= 0) return;
                    string req = Encoding.UTF8.GetString(buf, 0, n);
                    string first = req.Split('\n')[0];
                    string target = first.Split(' ').Skip(1).FirstOrDefault() ?? "/";
                    string path = target.Split('?')[0];
                    var q = Query(target);
                    string range = req.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));

                    // pliki aplikacji bez klucza (ikona i manifest muszą się wczytać przy dodawaniu do ekranu)
                    if (path == "/icon.png") { await Send(stream, "200 OK", "image/png", Icon(), "public, max-age=86400"); return; }
                    if (path == "/manifest.json") { await Send(stream, "200 OK", "application/manifest+json", Encoding.UTF8.GetBytes(Manifest(q.TryGetValue("k", out var mk) ? mk : ""))); return; }

                    if (!q.TryGetValue("k", out var k) || k != Key) { await Send(stream, "403 Forbidden", "text/plain", Encoding.UTF8.GetBytes("Isabelle: zeskanuj kod QR jeszcze raz")); return; }

                    switch (path)
                    {
                        case "/": await Send(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page())); break;
                        case "/api/now": await Json(stream, await Ui(NowJson)); break;
                        case "/api/cover":
                            var png = PartyServer.CoverPngPublic();
                            if (png == null) await Send(stream, "404 Not Found", "text/plain", Array.Empty<byte>());
                            else await Send(stream, "200 OK", "image/png", png, "public, max-age=3600");
                            break;
                        case "/api/lyrics": await Json(stream, await Ui(LyricsJson)); break;
                        case "/api/cmd": await Json(stream, await Ui(() => Command(q))); break;
                        case "/api/library": await Json(stream, LibraryJson()); break;
                        case "/api/art": await Art(stream, q); break;
                        case "/api/stream": await StreamFile(stream, q, range); break;
                        case "/api/search": await Json(stream, await SearchJson(q.TryGetValue("q", out var term) ? term : "")); break;
                        case "/api/play": await Json(stream, await Ui(() => Play(q))); break;
                        case "/api/episodes": await Json(stream, await EpisodesJson(q)); break;
                        case "/api/podcasts": await Json(stream, Serialize(PodcastService.Subscriptions.Select(s => new { t = Token(s), title = s.Title, author = s.Author, art = s.Art }))); break;
                        case "/api/wrapped": await Json(stream, WrappedJson(q.TryGetValue("p", out var p) ? p : "week")); break;
                        case "/api/party": await Json(stream, await Ui(() => PartyJson(q))); break;
                        case "/api/partyqr":
                            var qr = PartyServer.Running ? QrPng(PartyServer.Url) : null;
                            if (qr == null) await Send(stream, "404 Not Found", "text/plain", Array.Empty<byte>());
                            else await Send(stream, "200 OK", "image/png", qr);
                            break;
                        default: await Send(stream, "404 Not Found", "text/plain", Array.Empty<byte>()); break;
                    }
                }
                catch (Exception ex) { Log.Info("telefon: " + ex.Message); }
            }
        }

        /// <summary>Wywołanie na wątku interfejsu (wyspa, odtwarzacz, głośność).</summary>
        private static Task<string> Ui(Func<string> f)
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null) return Task.FromResult("{}");
            return d.InvokeAsync(f).Task;
        }

        // ================= teraz gra =================
        private static string NowJson()
        {
            var isl = App.Island;
            var snap = isl?.Snapshot;
            var st = OverlayServer.State;
            var c = isl?.AccentColor ?? System.Windows.Media.Color.FromRgb(255, 92, 160);
            return Serialize(new
            {
                title = snap?.Title ?? "",
                artist = (snap?.Artist ?? "").Split(" · ")[0],
                app = ListeningHistory.Source(snap?.AppId),
                playing = isl?.Playing == true,
                pos = isl?.Position ?? 0,
                dur = snap?.Duration ?? 0,
                cover = st.CoverKey ?? "",
                accent = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B),
                vol = Math.Round((isl?.Volume ?? 0) * 100),
                lyricsKey = isl?.LyricsKey ?? "",
                spotify = SpotifyService.CanPlay,
                party = PartyServer.Running,
            });
        }

        private static string LyricsJson()
        {
            var isl = App.Island;
            isl?.EnsureLyrics();
            var lines = isl?.Lyrics;
            return Serialize(new
            {
                key = isl?.LyricsKey ?? "",
                lines = lines == null ? new object[0] : lines.Select(l => (object)new { t = l.Time, text = l.Text ?? "" }).ToArray(),
            });
        }

        private static string Command(Dictionary<string, string> q)
        {
            var isl = App.Island;
            if (isl == null) return "{}";
            string c = q.TryGetValue("c", out var cc) ? cc : "";
            double.TryParse(q.TryGetValue("v", out var vv) ? vv : "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v);
            switch (c)
            {
                case "toggle": isl.TogglePlay(); break;
                case "next": isl.Next(); break;
                case "prev": isl.Previous(); break;
                case "seek": isl.SeekTo(v); break;
                case "vol": isl.Volume = (float)Math.Clamp(v / 100.0, 0, 1); break;
            }
            return "{\"ok\":true}";
        }

        // ================= biblioteka =================
        private static string LibraryJson()
        {
            if (LibraryService.Tracks.Count == 0) LibraryService.LoadCache();
            return Serialize(LibraryService.Tracks.ToList().Select(t => new
            {
                id = ToId(t.Path), title = t.Title, artist = t.Artist, album = t.Album ?? "", dur = Math.Round(t.Duration), art = !string.IsNullOrEmpty(t.CoverPath),
            }));
        }

        private static LibraryTrack TrackById(string id)
        {
            string path = FromId(id);
            return LibraryService.Tracks.ToList().FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task Art(NetworkStream s, Dictionary<string, string> q)
        {
            var t = TrackById(q.TryGetValue("id", out var id) ? id : "");
            if (t == null || string.IsNullOrEmpty(t.CoverPath) || !File.Exists(t.CoverPath)) { await Send(s, "404 Not Found", "text/plain", Array.Empty<byte>()); return; }
            string type = t.CoverPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            await Send(s, "200 OK", type, await File.ReadAllBytesAsync(t.CoverPath), "public, max-age=86400");
        }

        /// <summary>Plik z biblioteki dla odtwarzacza w telefonie (z obsługą przewijania - Range).</summary>
        private static async Task StreamFile(NetworkStream s, Dictionary<string, string> q, string rangeHeader)
        {
            var t = TrackById(q.TryGetValue("id", out var id) ? id : "");
            if (t == null || !File.Exists(t.Path)) { await Send(s, "404 Not Found", "text/plain", Array.Empty<byte>()); return; }
            string ext = Path.GetExtension(t.Path).ToLowerInvariant();
            string type = ext switch { ".mp3" => "audio/mpeg", ".m4a" or ".aac" or ".alac" => "audio/mp4", ".flac" => "audio/flac", ".wav" => "audio/wav", _ => "application/octet-stream" };
            using var fs = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long len = fs.Length, from = 0, to = len - 1;
            bool partial = false;
            if (rangeHeader != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(rangeHeader, @"bytes=(\d*)-(\d*)");
                if (m.Success)
                {
                    partial = true;
                    if (m.Groups[1].Value.Length > 0) from = long.Parse(m.Groups[1].Value);
                    if (m.Groups[2].Value.Length > 0) to = Math.Min(len - 1, long.Parse(m.Groups[2].Value));
                    if (m.Groups[1].Value.Length == 0 && m.Groups[2].Value.Length > 0) { from = Math.Max(0, len - long.Parse(m.Groups[2].Value)); to = len - 1; }
                }
            }
            long count = to - from + 1;
            string head = "HTTP/1.1 " + (partial ? "206 Partial Content" : "200 OK") + "\r\nContent-Type: " + type + "\r\nAccept-Ranges: bytes\r\nContent-Length: " + count +
                          (partial ? "\r\nContent-Range: bytes " + from + "-" + to + "/" + len : "") + "\r\nConnection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            await s.WriteAsync(h, 0, h.Length);
            fs.Position = from;
            var buf = new byte[64 * 1024];
            while (count > 0)
            {
                int r = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, count));
                if (r <= 0) break;
                await s.WriteAsync(buf, 0, r);
                count -= r;
            }
        }

        // ================= wyszukiwanie i granie =================
        private static async Task<string> SearchJson(string term)
        {
            term = (term ?? "").Trim();
            if (term.Length < 2) return "{}";
            string[] words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Match(string hay) => words.All(w => hay.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0);
            var lib = LibraryService.Tracks.ToList().Where(t => Match(t.Title + " " + t.Artist + " " + t.Album)).Take(8)
                .Select(t => new { id = ToId(t.Path), title = t.Title, artist = t.Artist, art = !string.IsNullOrEmpty(t.CoverPath) }).ToList();

            var radioTask = SafeTask(RadioService.Search(term));
            var podTask = SafeTask(PodcastService.SearchAsync(term));
            var spotTask = SpotifyService.CanPlay ? SafeTask(SpotifyService.SearchAsync(term, 6)) : Task.FromResult<List<SpotifyService.SpotifyTrack>>(null);
            await Task.WhenAll(radioTask, podTask, spotTask);

            var radio = (radioTask.Result ?? new List<RadioStation>()).Where(s => s.LastCheckOk).Take(6)
                .Select(s => new { t = Token(s), title = s.Name, sub = s.Country ?? "", url = s.Url }).ToList();
            var pods = (podTask.Result ?? new List<PodcastShow>()).Take(6).Select(s => new { t = Token(s), title = s.Title, sub = s.Author, art = s.Art }).ToList();
            var spot = (spotTask.Result ?? new List<SpotifyService.SpotifyTrack>()).Select(s => new { t = Token(s), title = s.Title, sub = s.Artist, art = s.Art }).ToList();
            return Serialize(new { lib, radio, pods, spot });
        }

        private static async Task<T> SafeTask<T>(Task<T> t) where T : class
        {
            try { return await t; } catch { return null; }
        }

        private static async Task<string> EpisodesJson(Dictionary<string, string> q)
        {
            if (!(Resolve(q.TryGetValue("t", out var t) ? t : "") is PodcastShow show)) return "[]";
            List<PodcastEpisode> eps;
            try { eps = PodcastService.CachedEpisodes(show) ?? await PodcastService.EpisodesAsync(show); }
            catch { return "[]"; }
            return Serialize(new
            {
                show = new { title = show.Title, author = show.Author, art = show.Art, t = Token(show) },
                eps = eps.Take(60).Select(e => new
                {
                    t = Token(e), title = e.Title, date = e.Date == default ? "" : e.Date.ToString("yyyy-MM-dd"), dur = e.Duration, url = e.Url,
                    pos = PodcastService.Position(e.Key), done = PodcastService.IsDone(e.Key),
                }),
            });
        }

        /// <summary>Granie na komputerze (z telefonu jako pilot).</summary>
        private static string Play(Dictionary<string, string> q)
        {
            if (q.TryGetValue("id", out var id))
            {
                var all = LibraryService.Tracks.ToList();
                var t = TrackById(id);
                if (t == null) return "{\"ok\":false}";
                LocalPlayer.PlayTracks(all, Math.Max(0, all.IndexOf(t)), q.TryGetValue("shuffle", out var sh) && sh == "1");
                return "{\"ok\":true}";
            }
            var o = Resolve(q.TryGetValue("t", out var tok) ? tok : "");
            switch (o)
            {
                case RadioStation st: LocalPlayer.PlayStation(st); break;
                case PodcastEpisode ep: LocalPlayer.PlayPodcast(ep); break;
                case SpotifyService.SpotifyTrack sp: _ = SpotifyService.PlayAsync(sp.Uri); break;
                default: return "{\"ok\":false}";
            }
            return "{\"ok\":true}";
        }

        // ================= Wrapped i impreza =================
        private static string WrappedJson(string period)
        {
            DateTime since = period switch { "week" => DateTime.UtcNow.AddDays(-7), "month" => DateTime.UtcNow.AddDays(-30), "year" => DateTime.UtcNow.AddDays(-365), _ => DateTime.MinValue };
            var items = ListeningHistory.Since(since);
            var music = items.Where(x => x.S != "Radio" && x.S != "Podcast").ToList();
            var tracks = music.GroupBy(x => (x.T.ToLowerInvariant(), ListeningHistory.MainArtist(x.A).ToLowerInvariant()))
                .Select(g => new { title = g.First().T, artist = g.First().A, count = g.Count() }).OrderByDescending(x => x.count).Take(5);
            double total = Math.Max(1, items.Sum(x => x.Sec));
            return Serialize(new
            {
                hours = Math.Round(items.Sum(x => x.Sec) / 3600.0, 1),
                plays = items.Count,
                artists = music.Select(x => ListeningHistory.MainArtist(x.A).ToLowerInvariant()).Where(a => a.Length > 0).Distinct().Count(),
                topArtists = ListeningHistory.TopArtists(since, 5).Select(a => new { name = a.artist, count = a.count }),
                topTracks = tracks,
                sources = items.GroupBy(x => x.S).Select(g => new { name = g.Key, share = Math.Round(g.Sum(x => x.Sec) / total * 100) }).OrderByDescending(x => x.share).Take(5),
            });
        }

        private static string PartyJson(Dictionary<string, string> q)
        {
            string a = q.TryGetValue("a", out var aa) ? aa : "";
            if (a == "start") PartyServer.Start();
            if (a == "stop") PartyServer.Stop();
            return Serialize(new
            {
                running = PartyServer.Running,
                url = PartyServer.Running ? PartyServer.Url : "",
                guests = PartyServer.Recent(10).Select(r => new { title = r.Title, artist = r.Artist, by = r.By }),
            });
        }

        // ================= pomocnicze =================
        private static string Token(object o)
        {
            lock (Lock)
            {
                foreach (var kv in Tokens) if (ReferenceEquals(kv.Value, o)) return kv.Key;
                if (Tokens.Count > 2000) Tokens.Clear();
                string t = Guid.NewGuid().ToString("N").Substring(0, 10);
                Tokens[t] = o;
                return t;
            }
        }

        private static object Resolve(string t)
        {
            lock (Lock) return t != null && Tokens.TryGetValue(t, out var o) ? o : null;
        }

        private static string Serialize(object o) => JsonSerializer.Serialize(o);

        private static Task Json(NetworkStream s, string json) => Send(s, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

        private static async Task Send(NetworkStream s, string status, string type, byte[] body, string cache = "no-store")
        {
            string head = "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                          "\r\nCache-Control: " + cache + "\r\nConnection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            await s.WriteAsync(h, 0, h.Length);
            if (body.Length > 0) await s.WriteAsync(body, 0, body.Length);
        }

        private static Dictionary<string, string> Query(string target)
        {
            var d = new Dictionary<string, string>();
            int qi = target.IndexOf('?');
            if (qi < 0) return d;
            foreach (var part in target.Substring(qi + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string key = eq < 0 ? part : part.Substring(0, eq), val = eq < 0 ? "" : part.Substring(eq + 1);
                try { d[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(val.Replace('+', ' ')); } catch { }
            }
            return d;
        }

        private static string ToId(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        private static string FromId(string id)
        {
            try
            {
                string b = (id ?? "").Replace('-', '+').Replace('_', '/');
                b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
                return Encoding.UTF8.GetString(Convert.FromBase64String(b));
            }
            catch { return ""; }
        }

        private static string RandomKey()
        {
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            return new string(bytes.Select(x => chars[x % chars.Length]).ToArray());
        }

        public static byte[] QrPng(string text)
        {
            try
            {
                using var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
                return new QRCoder.PngByteQRCode(data).GetGraphic(12);
            }
            catch { return null; }
        }

        private static string Page()
        {
            if (_page != null) return _page;
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("companion.html");
            using var r = new StreamReader(s);
            _page = r.ReadToEnd();
            return _page;
        }

        private static byte[] Icon()
        {
            if (_icon != null) return _icon;
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("applogo.png");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                _icon = ms.ToArray();
            }
            catch { _icon = Array.Empty<byte>(); }
            return _icon;
        }

        private static string Manifest(string key) => JsonSerializer.Serialize(new
        {
            name = "Isabelle",
            short_name = "Isabelle",
            start_url = "/?k=" + key,
            display = "standalone",
            background_color = "#0b0b0d",
            theme_color = "#0b0b0d",
            icons = new[] { new { src = "/icon.png", sizes = "512x512", type = "image/png", purpose = "any maskable" } },
        });
    }
}
