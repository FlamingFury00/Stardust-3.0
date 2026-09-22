using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Bot
{
    /// <summary>
    /// Zero-dependency local tactical dashboard. A tiny loopback HTTP server avoids platform-specific
    /// desktop UI packages while still providing a real live window in the user's normal browser.
    /// </summary>
    internal sealed class StardustDebugDashboard
    {
        private readonly bool enabled;
        private readonly bool openBrowser;
        private readonly int requestedPort;
        private readonly StardustScenarioRecorder scenarios;
        private readonly object startGate = new();
        private readonly JsonSerializerOptions json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        private TcpListener listener;
        private Thread serverThread;
        private volatile string latestState = "{}";
        private bool started;
        private int port;

        public bool Enabled => enabled;
        public string Url => started ? $"http://127.0.0.1:{port}/" : null;

        public StardustDebugDashboard(
            bool enabled, int port, bool openBrowser,
            StardustScenarioRecorder scenarios)
        {
            this.enabled = enabled;
            requestedPort = System.Math.Clamp(port, 1024, 65500);
            this.openBrowser = openBrowser;
            this.scenarios = scenarios;
        }

        public void Publish(DebugLiveSnapshot snapshot)
        {
            if (!enabled || snapshot == null)
                return;

            EnsureStarted();
            try
            {
                latestState = JsonSerializer.Serialize(snapshot, json);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"STARDUST_DEBUG_ERROR serialize {error.GetType().Name}: {error.Message}");
            }
        }

        private void EnsureStarted()
        {
            if (!enabled || started)
                return;

            lock (startGate)
            {
                if (started)
                    return;

                for (int candidate = requestedPort;
                    candidate < requestedPort + 10 && candidate <= 65535;
                    candidate++)
                {
                    try
                    {
                        listener = new TcpListener(IPAddress.Loopback, candidate);
                        listener.Start(32);
                        port = candidate;
                        started = true;
                        break;
                    }
                    catch
                    {
                        try { listener?.Stop(); } catch { }
                        listener = null;
                    }
                }

                if (!started)
                {
                    Console.Error.WriteLine(
                        $"STARDUST_DEBUG_ERROR unable to bind loopback ports {requestedPort}-{requestedPort + 9}");
                    return;
                }

                serverThread = new Thread(ServerLoop)
                {
                    IsBackground = true,
                    Name = "StardustDebugDashboard"
                };
                serverThread.Start();

                Console.WriteLine($"STARDUST_DEBUG_READY url={Url}");
                if (openBrowser)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Url,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(
                            $"STARDUST_DEBUG_ERROR browser {error.GetType().Name}: {error.Message}");
                    }
                }
            }
        }

        private void ServerLoop()
        {
            while (started)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch
                {
                    if (!started)
                        return;
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 2500;
                    client.SendTimeout = 2500;
                    NetworkStream stream = client.GetStream();
                    using var reader = new StreamReader(
                        stream, Encoding.UTF8, false, 4096, leaveOpen: true);

                    string requestLine = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(requestLine))
                        return;

                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2)
                        return;

                    string method = parts[0].ToUpperInvariant();
                    string rawTarget = parts[1];
                    string path = rawTarget;
                    string query = "";
                    int question = rawTarget.IndexOf('?');
                    if (question >= 0)
                    {
                        path = rawTarget[..question];
                        query = rawTarget[(question + 1)..];
                    }

                    // Drain request headers. Bodies are intentionally not accepted; dashboard
                    // commands are tiny POSTs encoded in the query string.
                    string header;
                    do { header = reader.ReadLine(); }
                    while (!string.IsNullOrEmpty(header));

                    switch (path)
                    {
                        case "/":
                            Write(stream, 200, "text/html; charset=utf-8", Html);
                            return;
                        case "/api/state":
                            Write(stream, 200, "application/json; charset=utf-8",
                                latestState);
                            return;
                        case "/api/scenarios":
                            Write(stream, 200, "application/json; charset=utf-8",
                                JsonSerializer.Serialize(
                                    scenarios?.ListScenarios() ?? new(), json));
                            return;
                        case "/api/save" when method == "POST":
                            scenarios?.RequestManualSave();
                            Write(stream, 200, "application/json; charset=utf-8",
                                "{\"ok\":true}");
                            return;
                        case "/api/replay" when method == "POST":
                            string file = QueryValue(query, "file");
                            scenarios?.RequestReplay(file);
                            Write(stream, 200, "application/json; charset=utf-8",
                                "{\"ok\":true}");
                            return;
                        default:
                            Write(stream, 404, "text/plain; charset=utf-8",
                                "Not found");
                            return;
                    }
                }
                catch { }
            }
        }

        private static string QueryValue(string query, string name)
        {
            if (string.IsNullOrEmpty(query))
                return null;

            foreach (string pair in query.Split('&',
                StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                string key = equals >= 0 ? pair[..equals] : pair;
                if (!string.Equals(
                    Uri.UnescapeDataString(key), name,
                    StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = equals >= 0 ? pair[(equals + 1)..] : "";
                return Uri.UnescapeDataString(value.Replace("+", " "));
            }
            return null;
        }

        private static void Write(
            NetworkStream stream, int status, string contentType, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
            string statusText = status == 200 ? "OK" :
                status == 404 ? "Not Found" : "Error";
            string headers =
                $"HTTP/1.1 {status} {statusText}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {payload.Length}\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(headers);
            stream.Write(head, 0, head.Length);
            stream.Write(payload, 0, payload.Length);
        }

        private const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Stardust Tactical Debugger</title>
<style>
:root{color-scheme:dark;--bg:#090d13;--card:#111923;--line:#263445;--muted:#8fa1b5;--good:#4ade80;--bad:#fb7185;--warn:#fbbf24;--accent:#67e8f9}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:#eef4fb;font:14px/1.4 ui-monospace,SFMono-Regular,Consolas,monospace}
header{position:sticky;top:0;z-index:2;background:#090d13e8;backdrop-filter:blur(10px);border-bottom:1px solid var(--line);padding:12px 18px;display:flex;gap:18px;align-items:center}
h1{font-size:16px;margin:0;color:var(--accent)}#status{color:var(--muted)}#score{font-size:20px;font-weight:800;margin-left:auto}.wrap{padding:16px;display:grid;grid-template-columns:minmax(420px,1.15fr) minmax(420px,1fr);gap:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:13px;box-shadow:0 8px 30px #0005}.card h2{font-size:12px;text-transform:uppercase;letter-spacing:.12em;color:var(--muted);margin:0 0 10px}
.hero{font-size:22px;font-weight:800;margin:2px 0 6px}.explain{color:#c7d2df}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:8px}.metric{background:#0b121a;border:1px solid #202d3b;border-radius:7px;padding:8px}.metric b{display:block;font-size:16px;margin-top:2px}.label{color:var(--muted);font-size:11px}
.checks{display:grid;grid-template-columns:repeat(2,1fr);gap:6px}.check{padding:7px;border-radius:6px;background:#0b121a;border:1px solid #263445}.true{color:var(--good)}.false{color:var(--bad)}
#field{width:100%;max-height:520px;background:#07130d;border:1px solid #234030;border-radius:8px}pre{white-space:pre-wrap;word-break:break-word;color:#cbd5e1;background:#0b121a;padding:9px;border-radius:7px;border:1px solid #202d3b;max-height:170px;overflow:auto}
button{background:#183445;color:#e7fbff;border:1px solid #2a6178;border-radius:6px;padding:7px 10px;font:inherit;cursor:pointer}button:hover{background:#20516a}.danger{background:#46202b;border-color:#84364b}
table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:5px 6px;border-bottom:1px solid #1f2c39}th{color:var(--muted);font-weight:500}
.scenario{display:flex;gap:8px;align-items:center;padding:7px 0;border-bottom:1px solid #1f2c39}.scenario .name{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.small{font-size:11px;color:var(--muted)}
@media(max-width:980px){.wrap{grid-template-columns:1fr}.grid{grid-template-columns:repeat(2,1fr)}}
</style>
</head>
<body>
<header><h1>✦ STARDUST TACTICAL DEBUGGER</h1><span id="status">connecting…</span><span id="score">0 — 0</span></header>
<div class="wrap">
<section>
  <div class="card"><h2>Current objective</h2><div class="hero" id="objective">—</div><div id="decision" class="small">—</div><p id="explanation" class="explain">—</p></div>
  <div class="card"><h2>World model</h2><canvas id="field" width="700" height="820"></canvas></div>
  <div class="card"><h2>Cars</h2><table><thead><tr><th>#</th><th>team</th><th>speed</th><th>boost</th><th>ball</th><th>goal-side</th></tr></thead><tbody id="cars"></tbody></table></div>
</section>
<section>
  <div class="card"><h2>Tactical state</h2><div class="grid" id="metrics"></div><div class="checks" id="checks" style="margin-top:10px"></div></div>
  <div class="card"><h2>Controller / mechanic</h2><div class="grid" id="controller"></div><pre id="detail">—</pre></div>
  <div class="card"><h2>Failure scenarios</h2>
    <div style="display:flex;gap:8px;margin-bottom:9px"><button id="save">Save current 8s buffer</button><button id="refresh">Refresh list</button></div>
    <div class="small">Conceded goals are captured automatically. Replay requires RLBot state setting to be enabled.</div>
    <div id="scenarios" style="margin-top:8px"></div>
  </div>
</section>
</div>
<script>
const $=id=>document.getElementById(id), fmt=(v,d=2)=>v==null?'—':Number(v).toFixed(d);
function metric(label,value){return '<div class="metric"><span class="label">'+label+'</span><b>'+value+'</b></div>'}
function check(label,v){return '<div class="check '+(v?'true':'false')+'">'+(v?'● ':'○ ')+label+'</div>'}
function draw(s){
 const c=$('field'),x=c.getContext('2d'),W=c.width,H=c.height, px=v=>(v+4096)/8192*W, py=v=>H-(v+5120)/10240*H;
 x.clearRect(0,0,W,H);x.fillStyle='#07130d';x.fillRect(0,0,W,H);x.strokeStyle='#315642';x.lineWidth=2;x.strokeRect(18,18,W-36,H-36);
 x.beginPath();x.moveTo(18,H/2);x.lineTo(W-18,H/2);x.stroke();x.beginPath();x.arc(W/2,H/2,72,0,Math.PI*2);x.stroke();
 function dot(p,r,color){if(!p)return;x.beginPath();x.fillStyle=color;x.arc(px(p[0]),py(p[1]),r,0,Math.PI*2);x.fill()}
 if(s.target?.p){x.strokeStyle='#fbbf24';x.setLineDash([8,7]);x.beginPath();x.moveTo(px(s.me.p[0]),py(s.me.p[1]));x.lineTo(px(s.target.p[0]),py(s.target.p[1]));x.stroke();x.setLineDash([]);dot(s.target.p,6,'#fbbf24')}
 (s.cars||[]).forEach(car=>dot(car.p,car.index===s.car?11:8,car.team===0?'#38bdf8':'#fb7185'));dot(s.ball.p,8,'#f8fafc');
}
function update(s){
 $('status').textContent='live · t='+fmt(s.t,2)+' · build '+(s.build||'').slice(0,8);
 $('score').textContent=(s.score?.[0]??0)+' — '+(s.score?.[1]??0);
 $('objective').textContent=s.objective||'—';$('decision').textContent=(s.decision||'—')+' · '+(s.action||'no action');$('explanation').textContent=s.explanation||'—';
 const t=s.tactics||{},m=s.me||{},b=s.ball||{};
 $('metrics').innerHTML=[
  metric('speed',fmt(m.speed,0)),metric('boost',fmt(m.boost,0)),metric('ball dist',fmt(m.ball_distance,0)),
  metric('my ETA',fmt(t.my_eta)),metric('opp ETA',fmt(t.opponent_eta)),metric('free time',fmt(t.free_time)),
  metric('pressure',fmt(t.pressure_time)),metric('goal threat',fmt(t.goal_threat_time)),metric('counter threat',fmt(t.counter_threat_time)),
  metric('goal-side',fmt(m.goal_side_progress,0)),metric('ball speed',fmt(b.speed,0)),metric('role',s.role||'—')
 ].join('');
 const q=s.checks||{};$('checks').innerHTML=Object.entries(q).map(([k,v])=>check(k.replaceAll('_',' '),v)).join('');
 const u=s.controller||{};$('controller').innerHTML=[
  metric('throttle',fmt(u.throttle)),metric('steer',fmt(u.steer)),metric('boost',String(!!u.boost)),
  metric('jump',String(!!u.jump)),metric('pitch',fmt(u.pitch)),metric('yaw',fmt(u.yaw))
 ].join('');$('detail').textContent=JSON.stringify(s.action_detail,null,2);
 $('cars').innerHTML=(s.cars||[]).map(car=>'<tr><td>'+car.index+'</td><td>'+car.team+'</td><td>'+fmt(car.speed,0)+'</td><td>'+fmt(car.boost,0)+'</td><td>'+fmt(car.ball_distance,0)+'</td><td>'+fmt(car.goal_side_progress,0)+'</td></tr>').join('');
 draw(s);
}
async function poll(){try{const r=await fetch('/api/state',{cache:'no-store'});if(r.ok)update(await r.json())}catch(e){$('status').textContent='disconnected'}setTimeout(poll,100)}
async function scenarios(){try{const r=await fetch('/api/scenarios',{cache:'no-store'}),a=await r.json();$('scenarios').innerHTML=a.map(f=>'<div class="scenario"><span class="name">'+f.name+'</span><span class="small">'+Math.round(f.bytes/1024)+' KB</span><button data-file="'+encodeURIComponent(f.name)+'">Replay</button></div>').join('')||'<div class="small">No captures yet.</div>';document.querySelectorAll('[data-file]').forEach(b=>b.onclick=async()=>{await fetch('/api/replay?file='+b.dataset.file,{method:'POST'});})}catch(e){}}
$('save').onclick=async()=>{await fetch('/api/save',{method:'POST'});setTimeout(scenarios,400)};$('refresh').onclick=scenarios;poll();scenarios();setInterval(scenarios,4000);
</script>
</body>
</html>
""";
    }
}
