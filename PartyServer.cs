using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>Piosenka dodana przez gościa imprezy.</summary>
    public sealed class PartyRequest
    {
        public string Title = "", Artist = "", By = "", Source = "";
        public DateTime At = DateTime.Now;
    }

    /// <summary>
    /// Tryb imprezy: mała strona WWW w domowej sieci. Goście skanują kod QR, szukają piosenek
    /// (Moja muzyka i Spotify) i dodają je do kolejki. Działa tylko z tajnym kluczem z kodu QR.
    /// </summary>
    public static class PartyServer
    {
        public const int Port = 43822;
        private static TcpListener _listener;
        private static CancellationTokenSource _cts;
        private static readonly Dictionary<string, DateTime> LastAdd = new Dictionary<string, DateTime>();
        private static readonly object Lock = new object();
        private static string _coverKey;
        private static byte[] _coverPng;

        public static bool Running => _listener != null;
        public static string Key { get; private set; } = "";
        public static string Error { get; private set; }
        private static readonly List<PartyRequest> Requests = new List<PartyRequest>();

        /// <summary>Ostatnio dodane (najnowsze pierwsze) - kopia, bezpieczna dla okna.</summary>
        public static List<PartyRequest> Recent(int count)
        {
            lock (Lock) return Requests.AsEnumerable().Reverse().Take(count).ToList();
        }
        public static event Action<PartyRequest> Added;
        public static event Action StateChanged;

        /// <summary>Adres strony dla telefonów (z kluczem), np. http://192.168.1.20:43822/?k=abc</summary>
        public static string Url => "http://" + LocalIp() + ":" + Port + "/?k=" + Key;

        public static bool Start()
        {
            if (Running) return true;
            try
            {
                Key = RandomKey();
                // nasłuch w domowej sieci - Windows może zapytać o zgodę zapory (trzeba zezwolić dla sieci prywatnej)
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _cts = new CancellationTokenSource();
                var tok = _cts.Token;
                Task.Run(() => AcceptLoop(tok));
                Error = null;
                lock (Lock) Requests.Clear();
                StateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Log.Info("impreza: " + ex.Message);
                _listener = null;
                return false;
            }
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts = null;
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

        private static async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = client.SendTimeout = 5000;
                    var stream = client.GetStream();
                    var buf = new byte[8192];
                    int n = await stream.ReadAsync(buf, 0, buf.Length);
                    if (n <= 0) return;
                    string req = Encoding.UTF8.GetString(buf, 0, n);
                    string target = req.Split(' ').Skip(1).FirstOrDefault() ?? "/";
                    string path = target.Split('?')[0];
                    var q = Query(target);
                    string ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";

                    // bez klucza z kodu QR - nic nie pokazujemy
                    if (!q.TryGetValue("k", out var k) || k != Key) { await Send(stream, "403 Forbidden", "text/plain", Encoding.UTF8.GetBytes("Isabelle")); return; }

                    switch (path)
                    {
                        case "/":
                            await Send(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page()));
                            break;
                        case "/api/now":
                            await Json(stream, NowJson());
                            break;
                        case "/api/cover":
                            var png = CoverPng();
                            if (png == null) await Send(stream, "404 Not Found", "text/plain", Array.Empty<byte>());
                            else await Send(stream, "200 OK", "image/png", png);
                            break;
                        case "/api/search":
                            await Json(stream, await SearchJson(q.TryGetValue("q", out var term) ? term : ""));
                            break;
                        case "/api/add":
                            await Json(stream, await AddJson(q, ip));
                            break;
                        case "/api/queue":
                            List<PartyRequest> list;
                            lock (Lock) list = Requests.AsEnumerable().Reverse().Take(15).ToList();
                            await Json(stream, JsonSerializer.Serialize(list.Select(r => new { title = r.Title, artist = r.Artist, by = r.By })));
                            break;
                        default:
                            await Send(stream, "404 Not Found", "text/plain", Array.Empty<byte>());
                            break;
                    }
                }
                catch (Exception ex) { Log.Info("impreza: " + ex.Message); }
            }
        }

        // ================= API =================
        private static string NowJson()
        {
            var s = OverlayServer.State;
            return JsonSerializer.Serialize(new { title = s.Title, artist = s.Artist, playing = s.Playing, cover = s.CoverKey, accent = s.Accent });
        }

        private static byte[] CoverPng()
        {
            var s = OverlayServer.State;
            if (s.Cover == null) return null;
            lock (Lock)
            {
                if (_coverKey == s.CoverKey && _coverPng != null) return _coverPng;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(s.Cover));
                using var ms = new MemoryStream();
                enc.Save(ms);
                _coverPng = ms.ToArray();
                _coverKey = s.CoverKey;
                return _coverPng;
            }
        }

        private static async Task<string> SearchJson(string term)
        {
            term = (term ?? "").Trim();
            var results = new List<object>();
            if (term.Length < 2) return "[]";
            string[] words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Match(string hay) => words.All(w => hay.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0);
            foreach (var t in LibraryService.Tracks.ToList().Where(t => Match(t.Title + " " + t.Artist + " " + t.Album)).Take(6))
                results.Add(new { src = "lib", id = ToId(t.Path), title = t.Title, artist = t.Artist, art = "" });
            if (SpotifyService.CanPlay)
            {
                try
                {
                    var found = await SpotifyService.SearchAsync(term, 6);
                    if (found != null)
                        foreach (var t in found) results.Add(new { src = "spotify", id = t.Uri, title = t.Title, artist = t.Artist, art = t.Art ?? "" });
                }
                catch (Exception ex) { Log.Info("impreza szukaj: " + ex.Message); }
            }
            return JsonSerializer.Serialize(results);
        }

        private static async Task<string> AddJson(Dictionary<string, string> q, string ip)
        {
            string src = q.TryGetValue("src", out var s) ? s : "", id = q.TryGetValue("id", out var i) ? i : "";
            string by = q.TryGetValue("by", out var b) ? b.Trim() : "";
            if (by.Length > 20) by = by.Substring(0, 20);
            if (by.Length == 0) by = L.T("Gość", "Guest");
            lock (Lock)
            {
                // jedna piosenka na 5 s z jednego telefonu
                if (LastAdd.TryGetValue(ip, out var last) && (DateTime.UtcNow - last).TotalSeconds < 5)
                    return JsonSerializer.Serialize(new { ok = false, msg = L.T("Chwilka – za szybko", "Hold on – too fast") });
                LastAdd[ip] = DateTime.UtcNow;
            }
            var req = new PartyRequest { By = by, Source = src };
            var item = new PendingItem { Req = req };
            if (src == "lib")
            {
                string path = FromId(id);
                var t = LibraryService.Tracks.ToList().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                if (t == null) return JsonSerializer.Serialize(new { ok = false, msg = L.T("Nie ma takiej piosenki", "No such song") });
                req.Title = t.Title;
                req.Artist = t.Artist;
                item.Track = t;
            }
            else if (src == "spotify" && id.StartsWith("spotify:track:") && SpotifyService.CanPlay)
            {
                req.Title = q.TryGetValue("title", out var tt) ? tt : "";
                req.Artist = q.TryGetValue("artist", out var aa) ? aa : "";
                item.Uri = id;
            }
            else return JsonSerializer.Serialize(new { ok = false, msg = "?" });

            // do kolejki imprezy - zagra po obecnej piosence (nie przerywa jej)
            var ui = System.Windows.Application.Current?.Dispatcher;
            if (ui != null) await ui.InvokeAsync(() => { lock (Lock) Pending.Add(item); EnsurePump(); Pump(); });
            lock (Lock)
            {
                Requests.Add(req);
                if (Requests.Count > 200) Requests.RemoveAt(0);
            }
            try { Added?.Invoke(req); } catch (Exception ex) { Log.Error(ex); }
            return JsonSerializer.Serialize(new { ok = true, msg = L.T("Dodano do kolejki 🎉", "Added to the queue 🎉") });
        }

        // ================= kolejka imprezy =================
        // Piosenki gości grają PO obecnej, w kolejności dodania. Gdy się da - trafiają do natywnej kolejki
        // (Moja muzyka: „zagraj następne”, Spotify: kolejka Spotify). Gdy gra coś innego - czekamy na koniec
        // obecnej piosenki i wtedy przełączamy. Gdy nic nie gra - impreza startuje od razu.
        private sealed class PendingItem
        {
            public LibraryTrack Track;
            public string Uri;
            public PartyRequest Req;
        }

        private static readonly List<PendingItem> Pending = new List<PendingItem>();
        private static System.Windows.Threading.DispatcherTimer _pump;
        private static string _waitKey;
        private static DateTime _cooldownUntil = DateTime.MinValue;

        /// <summary>Ile piosenek gości czeka na swoją kolej (poza natywnymi kolejkami).</summary>
        public static int PendingCount { get { lock (Lock) return Pending.Count; } }

        private static void EnsurePump()
        {
            if (_pump != null) return;
            _pump = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _pump.Tick += (s, e) => Pump();
            _pump.Start();
        }

        private static void Pump()
        {
            try
            {
                if (DateTime.UtcNow < _cooldownUntil) return;
                while (true)
                {
                    PendingItem head;
                    lock (Lock) head = Pending.Count > 0 ? Pending[0] : null;
                    if (head == null) { _waitKey = null; return; }

                    var island = App.Island;
                    var snap = island?.Snapshot;
                    bool playing = island?.Playing == true && snap != null && !string.IsNullOrEmpty(snap.Title);
                    string app = (snap?.AppId ?? "").ToLowerInvariant();
                    bool ownList = playing && app.Contains("isabelle") && LocalPlayer.CurrentTrack != null && LocalPlayer.CurrentStation == null && !LocalPlayer.PlayingPreview;
                    bool spotifyOn = playing && app.Contains("spotify");

                    // 1) natywna kolejka obecnego odtwarzacza - bez żadnej przerwy
                    if (head.Track != null && ownList) { Remove(head); LocalPlayer.Enqueue(head.Track); continue; }
                    if (head.Uri != null && spotifyOn) { Remove(head); _ = SpotifyService.QueueAsync(head.Uri, playIfIdle: false); continue; }

                    // 2) nic nie gra - startujemy od razu
                    if (!playing) { Remove(head); StartNow(head, snap); return; }

                    // 3) gra coś innego - czekamy na koniec obecnej piosenki
                    _waitKey ??= snap.Key;
                    bool ending = snap.Duration > 0 && snap.Duration - snap.PositionNow(DateTime.UtcNow) <= 1.2;
                    bool changed = snap.Key != _waitKey;
                    if (ending || changed) { Remove(head); StartNow(head, snap); }
                    return;
                }
            }
            catch (Exception ex) { Log.Info("impreza kolejka: " + ex.Message); }
        }

        private static void Remove(PendingItem it)
        {
            lock (Lock) Pending.Remove(it);
            _waitKey = null;
        }

        /// <summary>Włącza piosenkę gościa (najpierw zatrzymuje to, co grało).</summary>
        private static void StartNow(PendingItem it, MediaSnapshot current)
        {
            _cooldownUntil = DateTime.UtcNow.AddSeconds(4);       // wyspa musi zauważyć nową piosenkę
            if (current != null)
            {
                string app = current.AppId ?? "";
                bool ownIsPlaying = app.IndexOf("Isabelle", StringComparison.OrdinalIgnoreCase) >= 0;
                // ten sam odtwarzacz i tak przełączy utwór - pauzujemy tylko „obcy”
                if (it.Track != null && !ownIsPlaying) App.Island?.PauseApp(app);
                if (it.Uri != null && app.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    if (ownIsPlaying) { if (LocalPlayer.IsPlaying) LocalPlayer.TogglePause(); }
                    else App.Island?.PauseApp(app);
                }
            }
            if (it.Track != null) LocalPlayer.PlayTracks(new[] { it.Track }, 0, false);
            else if (it.Uri != null) _ = SpotifyService.PlayAsync(it.Uri);
        }

        // ================= pomocnicze =================
        private static Task Json(NetworkStream s, string json) => Send(s, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

        private static async Task Send(NetworkStream s, string status, string type, byte[] body)
        {
            string head = "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                          "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
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
                string b = id.Replace('-', '+').Replace('_', '/');
                b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
                return Encoding.UTF8.GetString(Convert.FromBase64String(b));
            }
            catch { return ""; }
        }

        private static string RandomKey()
        {
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
            return new string(bytes.Select(x => chars[x % chars.Length]).ToArray());
        }

        /// <summary>Adres tego komputera w domowej sieci (karta z bramą, nie wirtualna).</summary>
        public static string LocalIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vpn") || desc.Contains("tap")) continue;
                    var props = ni.GetIPProperties();
                    if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                    var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                    if (ip != null) return ip.ToString();
                }
            }
            catch { }
            return "localhost";
        }

        // ================= strona dla telefonu =================
        private static string Page() => @"<!DOCTYPE html><html lang='pl'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1'>
<title>Isabelle – impreza</title>
<style>
*{box-sizing:border-box}body{margin:0;font-family:-apple-system,Segoe UI,Roboto,sans-serif;background:#0b0b0d;color:#fff}
.wrap{max-width:520px;margin:0 auto;padding:18px 16px 40px}
.now{display:flex;gap:14px;align-items:center;background:#16161a;border-radius:22px;padding:14px}
.now img{width:64px;height:64px;border-radius:12px;object-fit:cover;background:#222}
.t{font-weight:700;font-size:17px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.a{color:#ffffff90;font-size:14px;margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
h2{font-size:15px;color:#ffffffb0;margin:22px 4px 8px;font-weight:600}
input{width:100%;background:#16161a;border:1px solid #ffffff20;border-radius:16px;color:#fff;font-size:16px;padding:13px 16px;outline:none}
input:focus{border-color:var(--ac,#ff5ca0)}
.row{display:flex;gap:12px;align-items:center;padding:9px 6px;border-radius:14px}
.row img,.ph{width:44px;height:44px;border-radius:9px;object-fit:cover;background:#222;flex:none;display:flex;align-items:center;justify-content:center;color:#fff8;font-size:18px}
.row .x{flex:1;min-width:0}
.add{flex:none;background:var(--ac,#ff5ca0);color:#000;border:0;border-radius:20px;font-weight:700;font-size:14px;padding:9px 15px}
.add:disabled{opacity:.5}
.src{font-size:11px;color:#ffffff70;margin-left:4px}
#toast{position:fixed;left:50%;bottom:24px;transform:translateX(-50%);background:#fff;color:#000;font-weight:700;border-radius:20px;padding:11px 18px;opacity:0;transition:opacity .25s;pointer-events:none}
.name{margin-top:10px}
</style></head><body><div class='wrap'>
<div class='now'><img id='cv' alt=''><div style='min-width:0'><div class='a' id='lbl'>Teraz gra</div><div class='t' id='nt'>–</div><div class='a' id='na'></div></div></div>
<h2 id='h1'>Dodaj piosenkę</h2>
<input id='q' placeholder='Szukaj utworu lub wykonawcy' autocomplete='off'>
<input id='nick' class='name' placeholder='Twoje imię (widać przy piosence)' maxlength='20'>
<div id='res'></div>
<h2 id='h2'>Ostatnio dodane</h2><div id='qu'></div>
</div><div id='toast'></div>
<script>
const k=new URLSearchParams(location.search).get('k');const en=!(navigator.language||'').startsWith('pl');
const T=(p,e)=>en?e:p;
document.getElementById('lbl').textContent=T('Teraz gra','Now playing');document.getElementById('h1').textContent=T('Dodaj piosenkę','Add a song');
document.getElementById('h2').textContent=T('Ostatnio dodane','Recently added');
q.placeholder=T('Szukaj utworu lub wykonawcy','Search for a song or artist');nick.placeholder=T('Twoje imię (widać przy piosence)','Your name (shown with the song)');
nick.value=localStorage.getItem('nick')||'';nick.oninput=()=>localStorage.setItem('nick',nick.value);
const api=(p,o)=>fetch(p+(p.includes('?')?'&':'?')+'k='+k+(o||'')).then(r=>r.json());
let lastCover='';
function esc(s){return (s||'').replace(/[&<>""']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c]))}
async function now(){try{const n=await api('/api/now');nt.textContent=n.title||'–';na.textContent=n.artist||'';
document.documentElement.style.setProperty('--ac',n.accent||'#ff5ca0');
if(n.cover&&n.cover!==lastCover){lastCover=n.cover;cv.src='/api/cover?k='+k+'&v='+encodeURIComponent(n.cover);}}catch(e){}}
async function queue(){try{const l=await api('/api/queue');qu.innerHTML=l.length?l.map(r=>`<div class='row'><div class='ph'>♪</div><div class='x'><div class='t' style='font-size:15px'>${esc(r.title)}</div><div class='a'>${esc(r.artist)} · ${esc(r.by)}</div></div></div>`).join(''):`<div class='a' style='padding:6px'>${T('Na razie nic – bądź pierwszy!','Nothing yet – be the first!')}</div>`;}catch(e){}}
let timer;q.oninput=()=>{clearTimeout(timer);timer=setTimeout(search,250)};
async function search(){const term=q.value.trim();if(term.length<2){res.innerHTML='';return;}
const l=await api('/api/search?q='+encodeURIComponent(term));
res.innerHTML=l.length?l.map((r,i)=>`<div class='row'>${r.art?`<img src='${esc(r.art)}'>`:`<div class='ph'>♪</div>`}<div class='x'><div class='t' style='font-size:15px'>${esc(r.title)}</div><div class='a'>${esc(r.artist)}<span class='src'>${r.src==='spotify'?'Spotify':T('z biblioteki','library')}</span></div></div><button class='add' data-i='${i}'>${T('Dodaj','Add')}</button></div>`).join(''):`<div class='a' style='padding:8px'>${T('Nic nie znaleziono','Nothing found')}</div>`;
res.querySelectorAll('.add').forEach(b=>b.onclick=async()=>{const r=l[b.dataset.i];b.disabled=true;
const o=await api('/api/add?src='+r.src+'&id='+encodeURIComponent(r.id)+'&title='+encodeURIComponent(r.title)+'&artist='+encodeURIComponent(r.artist)+'&by='+encodeURIComponent(nick.value));
toast(o.msg);if(o.ok){b.textContent='✓';queue();}else b.disabled=false;});}
function toast(m){const t=document.getElementById('toast');t.textContent=m;t.style.opacity=1;setTimeout(()=>t.style.opacity=0,2200)}
now();queue();setInterval(now,3000);setInterval(queue,6000);
</script></body></html>";
    }
}
