using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>Co gra teraz - niezmienny obiekt, bezpieczny dla serwera i Discorda.</summary>
    public sealed class NowPlayingState
    {
        public string Title = "";
        public string Artist = "";
        public string App = "";
        public bool Playing;
        public double Position;
        public double Duration;
        public string Lyric = "";
        public string NextLyric = "";
        public string Accent = "#FF5CA0";
        public string CoverKey = "";
        public BitmapSource Cover;   // zamrożona (Freeze) - można czytać z innych wątków
        public bool HasMedia => Title.Length > 0;
    }

    /// <summary>
    /// Nakładka dla streamerów: mały serwer WWW tylko na tym komputerze (127.0.0.1).
    /// W OBS: Źródło → Przeglądarka → http://localhost:PORT/
    /// </summary>
    public sealed class OverlayServer
    {
        public static volatile NowPlayingState State = new NowPlayingState();

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly object _coverLock = new object();
        private string _coverKey;
        private byte[] _coverPng;

        public int Port { get; private set; }
        public bool Running => _listener != null;
        public string Error { get; private set; }

        public void Start(int port)
        {
            if (Running && port == Port) return;
            Stop();
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                Port = port;
                Error = null;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                Task.Run(() => AcceptLoop(token));
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _listener = null;
                Log.Info("nakładka OBS: " + ex.Message);
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts = null;
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            var listener = _listener;
            while (!token.IsCancellationRequested && listener != null)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(token); }
                catch { break; }
                _ = Task.Run(() => Handle(client));
            }
        }

        private async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 3000;
                    client.SendTimeout = 3000;
                    var stream = client.GetStream();
                    var buf = new byte[8192];
                    int total = 0;
                    while (total < buf.Length)
                    {
                        int n = await stream.ReadAsync(buf, total, buf.Length - total);
                        if (n <= 0) break;
                        total += n;
                        if (Encoding.ASCII.GetString(buf, 0, total).Contains("\r\n\r\n")) break;
                    }
                    string req = Encoding.ASCII.GetString(buf, 0, total);
                    string firstLine = req.Split('\n')[0];
                    string[] parts = firstLine.Split(' ');
                    string path = parts.Length > 1 ? parts[1] : "/";
                    int q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);

                    switch (path)
                    {
                        case "/state":
                            await Send(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(StateJson()));
                            break;
                        case "/cover":
                            byte[] png = CoverPng();
                            if (png != null) await Send(stream, "200 OK", "image/png", png);
                            else await Send(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("no cover"));
                            break;
                        case "/":
                        case "/index.html":
                            await Send(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(OverlayHtml.Page));
                            break;
                        default:
                            await Send(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"));
                            break;
                    }
                }
                catch
                {
                    // klient (OBS) się rozłączył - nic się nie stało
                }
            }
        }

        private static async Task Send(NetworkStream s, string status, string type, byte[] body)
        {
            string head = "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                          "\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            await s.WriteAsync(h, 0, h.Length);
            await s.WriteAsync(body, 0, body.Length);
        }

        private static string StateJson()
        {
            var s = State;
            return JsonSerializer.Serialize(new
            {
                title = s.Title,
                artist = s.Artist,
                app = s.App,
                playing = s.Playing,
                position = s.Position,
                duration = s.Duration,
                lyric = s.Lyric,
                nextLyric = s.NextLyric,
                accent = s.Accent,
                cover = s.CoverKey,
                hasMedia = s.HasMedia,
            });
        }

        private byte[] CoverPng()
        {
            var s = State;
            if (s.Cover == null) return null;
            lock (_coverLock)
            {
                if (_coverKey == s.CoverKey && _coverPng != null) return _coverPng;
                try
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(s.Cover));
                    using (var ms = new MemoryStream())
                    {
                        enc.Save(ms);
                        _coverPng = ms.ToArray();
                        _coverKey = s.CoverKey;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    return null;
                }
                return _coverPng;
            }
        }
    }

    /// <summary>Strona nakładki (przezroczyste tło dla OBS). Opcje w adresie: ?lyrics=0, ?scale=1.5, ?style=glass</summary>
    internal static class OverlayHtml
    {
        public const string Page = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>Isabelle overlay</title>
<style>
html,body{margin:0;background:transparent;overflow:hidden;font-family:'Segoe UI Variable Display','Segoe UI',sans-serif}
#wrap{position:absolute;left:16px;top:16px;transform-origin:0 0;transition:opacity .5s, transform .5s}
#pill{display:flex;align-items:center;gap:14px;background:#000;border-radius:30px;padding:10px 22px 10px 10px;min-width:280px;max-width:560px;
 box-shadow:0 10px 40px rgba(0,0,0,.45), 0 0 34px var(--glow);border:1px solid rgba(255,255,255,.08);transition:box-shadow .8s}
body.glass #pill{background:rgba(12,12,14,.72);backdrop-filter:blur(18px)}
#cover{width:58px;height:58px;border-radius:16px;background:#2a2a30 center/cover no-repeat;flex:none;transition:background-image .4s}
#txt{min-width:0;flex:1}
#title{color:#fff;font-size:17px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
#artist{color:rgba(255,255,255,.7);font-size:13.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:1px}
#lyric{color:#fff;font-size:14px;font-weight:700;margin-top:6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;transition:opacity .35s, transform .35s}
#lyric.out{opacity:0;transform:translateY(6px)}
#bar{height:4px;border-radius:2px;background:rgba(255,255,255,.18);margin-top:8px;overflow:hidden}
#fill{height:100%;width:0;background:var(--accent);border-radius:2px}
#bars{display:flex;gap:3px;align-items:center;height:18px;flex:none}
#bars i{display:block;width:4px;border-radius:2px;background:var(--accent);height:30%;transition:height .18s}
.hidden{opacity:0}
</style></head>
<body>
<div id=""wrap"" class=""hidden""><div id=""pill"">
 <div id=""cover""></div>
 <div id=""txt""><div id=""title""></div><div id=""artist""></div><div id=""lyric""></div><div id=""bar""><div id=""fill""></div></div></div>
 <div id=""bars""><i></i><i></i><i></i><i></i></div>
</div></div>
<script>
const p=new URLSearchParams(location.search);
const showLyrics=p.get('lyrics')!=='0';
const scale=parseFloat(p.get('scale')||'1');
if(p.get('style')==='glass')document.body.classList.add('glass');
const wrap=document.getElementById('wrap');wrap.style.transform='scale('+scale+')';
const $=id=>document.getElementById(id);
if(!showLyrics)$('lyric').style.display='none';
let st=null,at=0,coverKey=null,lastLyric=null;
function fmtAccent(a){document.documentElement.style.setProperty('--accent',a);document.documentElement.style.setProperty('--glow',a+'55');}
async function poll(){
 try{const r=await fetch('/state',{cache:'no-store'});st=await r.json();at=performance.now();
  wrap.classList.toggle('hidden',!st.hasMedia);
  $('title').textContent=st.title;$('artist').textContent=st.artist;fmtAccent(st.accent||'#FF5CA0');
  if(st.cover!==coverKey){coverKey=st.cover;$('cover').style.backgroundImage=st.cover?'url(/cover?k='+encodeURIComponent(st.cover)+')':'none';}
  const l=st.lyric||'';
  if(showLyrics&&l!==lastLyric){lastLyric=l;const el=$('lyric');el.classList.add('out');setTimeout(()=>{el.textContent=l;el.classList.remove('out');},180);}
 }catch(e){wrap.classList.add('hidden');}
}
function frame(t){
 if(st){const pos=st.position+(st.playing?(performance.now()-at)/1000:0);
  $('fill').style.width=(st.duration>0?Math.min(100,pos/st.duration*100):0)+'%';
  const bars=document.querySelectorAll('#bars i');
  bars.forEach((b,i)=>{const h=st.playing?30+70*Math.abs(Math.sin(t/1000*(4.2+i*1.3)+i*1.1)):28;b.style.height=h+'%';});}
 requestAnimationFrame(frame);
}
poll();setInterval(poll,500);requestAnimationFrame(frame);
</script></body></html>";
    }
}
