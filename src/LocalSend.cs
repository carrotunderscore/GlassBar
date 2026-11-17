using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace GlassBar
{
    /// LocalSend protocol v2 (https://github.com/localsend/protocol): the bar shows up as a LocalSend
    /// device on the network, so phones running LocalSend can send to it and it can send to them.
    public sealed class LocalSendService : IDisposable
    {
        public class Device
        {
            public string Alias, Model, Type, Fingerprint, Ip, Protocol;
            public int Port;
            public DateTime Seen;
            public bool IsMobile => Type == "mobile";
            public string BaseUrl => $"{Protocol ?? "https"}://{Ip}:{Port}/api/localsend/v2";
        }

        public class Incoming
        {
            public string SessionId, SenderAlias, SenderIp;
            public List<(string id, string name, long size, string token)> Files = new();
            public long TotalBytes => Files.Sum(f => f.size);
            public readonly Dictionary<string, string> Saved = new();   // id -> final path
            public DateTime LastActivity = DateTime.Now;
        }

        const int DefaultPort = 53317;
        static readonly IPAddress Group = IPAddress.Parse("224.0.0.167");
        static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public string Alias { get; }
        public int Port { get; private set; }
        public string Fingerprint { get; private set; }
        public string SaveDir { get; set; }
        public string Error { get; private set; }

        public readonly ConcurrentDictionary<string, Device> Devices = new();
        public event Action DevicesChanged;
        /// UI: asked when a phone wants to send files. Return true to accept.
        public Func<Incoming, Task<bool>> AskAccept;
        public event Action<Incoming> Progress;
        public event Action<Incoming> Received;
        public event Action<string, string> TextReceived;   // sender, text

        WebApplication web;
        UdpClient udp;
        X509Certificate2 cert;
        Incoming session;
        readonly CancellationTokenSource stop = new();
        HttpClient http;

        /// LocalSend 2.2+ requires a client certificate (mTLS) and checks that it matches our fingerprint.
        HttpClient CreateHttp() => new(new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (a, b, c, d) => true,   // LocalSend uses self-signed certificates
                ClientCertificates = new X509CertificateCollection { cert },
                LocalCertificateSelectionCallback = (sender, host, local, remote, issuers) => cert
            },
            ConnectTimeout = TimeSpan.FromSeconds(4)
        }) { Timeout = Timeout.InfiniteTimeSpan };

        static readonly object logLock = new();
        public static string LogFile => Path.Combine(BarSettings.DataDir, "localsend.log");
        public static void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    var fi = new FileInfo(LogFile);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }

        public LocalSendService(string alias, string saveDir)
        {
            Alias = alias;
            SaveDir = saveDir;
        }

        // ---------------------------------------------------------------
        // start: HTTPS server + multicast listener
        // ---------------------------------------------------------------
        public async Task StartAsync()
        {
            cert = LoadCert();
            Fingerprint = Convert.ToHexString(SHA256.HashData(cert.RawData));   // uppercase hex, like LocalSend
            http = CreateHttp();

            // the official LocalSend app may already hold 53317 – then take the next free port
            Exception last = null;
            foreach (int port in new[] { DefaultPort, DefaultPort + 1, DefaultPort + 2, DefaultPort + 3 })
            {
                try { await StartServer(port); Port = port; last = null; break; }
                catch (Exception ex) { last = ex; }
            }
            if (last != null) { Error = "Couldn't open a network port: " + last.Message; Log(Error); return; }
            Log($"Server on port {Port}, fingerprint {Fingerprint}, addresses {string.Join(", ", LocalAddresses())}");

            try
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DefaultPort));
                foreach (var ip in LocalAddresses())
                    try { udp.JoinMulticastGroup(Group, ip); } catch { }
                _ = Task.Run(ListenUdp);
            }
            catch (Exception ex) { Error = "Multicast unavailable: " + ex.Message; Log(Error); }

            Announce();
            // re-announce now and then so phones that opened LocalSend later still see us
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(30), stop.Token); } catch { return; }
                    Announce();
                }
            });
        }

        static X509Certificate2 LoadCert()
        {
            var file = Path.Combine(BarSettings.DataDir, "localsend.pfx");
            try { if (File.Exists(file)) return new X509Certificate2(file, (string)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable); }
            catch { }
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=GlassBar LocalSend", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var tmp = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(20));
            var pfx = tmp.Export(X509ContentType.Pfx);
            File.WriteAllBytes(file, pfx);
            // SChannel needs a persisted key, so load it back from the PFX
            return new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }

        async Task StartServer(int port)
        {
            var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
            b.Logging.ClearProviders();
            b.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = null;
                k.ListenAnyIP(port, o => { o.Protocols = HttpProtocols.Http1; o.UseHttps(cert); });
            });
            var app = b.Build();
            app.Use(async (ctx, next) =>
            {
                Log($"HTTP {ctx.Request.Method} {ctx.Request.Path} from {RemoteIp(ctx)}");
                await next();
            });
            const string api = "/api/localsend/v2";
            app.MapGet(api + "/info", () => Results.Json(Info(null), Json));
            app.MapPost(api + "/register", async c => await (await Register(c)).ExecuteAsync(c));
            app.MapPost(api + "/prepare-upload", async c => await (await PrepareUpload(c)).ExecuteAsync(c));
            app.MapPost(api + "/upload", async c => await (await Upload(c)).ExecuteAsync(c));
            app.MapPost(api + "/cancel", (HttpContext c) => { if (session?.SessionId == c.Request.Query["sessionId"]) session = null; return Results.Ok(); });
            try { await app.StartAsync(stop.Token); }
            catch { await app.DisposeAsync(); throw; }
            web = app;
        }

        Dictionary<string, object> Info(bool? announce)
        {
            var d = new Dictionary<string, object>
            {
                ["alias"] = Alias, ["version"] = "2.1", ["deviceModel"] = "Windows", ["deviceType"] = "desktop",
                ["fingerprint"] = Fingerprint, ["port"] = Port, ["protocol"] = "https", ["download"] = false
            };
            if (announce != null) d["announce"] = announce.Value;
            return d;
        }

        // ---------------------------------------------------------------
        // discovery
        // ---------------------------------------------------------------
        Device AddDevice(JsonElement info, string ip, int? fallbackPort = null)
        {
            string S(string n) => info.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var fp = S("fingerprint");
            if (fp == Fingerprint || ip == null || ip == "127.0.0.1" || ip == "::1") return null;
            int port = info.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : fallbackPort ?? DefaultPort;
            var d = new Device
            {
                Alias = S("alias") ?? ip, Model = S("deviceModel"), Type = S("deviceType") ?? "desktop",
                Fingerprint = fp ?? ip, Ip = ip, Port = port, Protocol = S("protocol") ?? "https", Seen = DateTime.Now
            };
            // same device on a new address replaces the old entry
            foreach (var old in Devices.Values.Where(x => x.Fingerprint == d.Fingerprint || (x.Ip == d.Ip && x.Port == d.Port)).ToList())
                Devices.TryRemove(old.Fingerprint, out _);
            Devices[d.Fingerprint] = d;
            DevicesChanged?.Invoke();
            return d;
        }

        async Task ListenUdp()
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(stop.Token); }
                catch { if (stop.IsCancellationRequested) return; await Task.Delay(500); continue; }
                try
                {
                    using var doc = JsonDocument.Parse(r.Buffer);
                    var root = doc.RootElement;
                    var d = AddDevice(root, r.RemoteEndPoint.Address.ToString());
                    if (d == null) continue;
                    Log($"UDP from {d.Alias} {d.Ip}:{d.Port} {d.Protocol} announce={(root.TryGetProperty("announce", out var an) ? an.ToString() : "-")}");
                    if (root.TryGetProperty("announce", out var a) && a.ValueKind == JsonValueKind.True)
                        _ = AnswerAnnouncement(d);
                }
                catch { }
            }
        }

        async Task AnswerAnnouncement(Device d)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var res = await http.PostAsync(d.BaseUrl + "/register", JsonBody(Info(null)), cts.Token);
                Log($"register -> {d.Alias}: {(int)res.StatusCode}");
                if (res.IsSuccessStatusCode) return;
            }
            catch (Exception ex) { Log($"register -> {d.Alias} failed: {ex.GetBaseException().Message}"); }
            // fallback: announce ourselves, so the phone registers with us instead
            SendMulticast(Info(true));
        }

        async Task<IResult> Register(HttpContext c)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(c.Request.Body);
                AddDevice(doc.RootElement, RemoteIp(c));
            }
            catch { }
            return Results.Json(Info(null), Json);
        }

        /// Tell everyone on the network we're here; LocalSend devices answer with their info.
        public void Announce() => SendMulticast(Info(true));

        void SendMulticast(object msg)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
            foreach (var ip in LocalAddresses())
            {
                try
                {
                    using var c = new UdpClient(new IPEndPoint(ip, 0));
                    c.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    c.Send(bytes, bytes.Length, new IPEndPoint(Group, DefaultPort));
                }
                catch { }
            }
        }

        /// Fallback when multicast is blocked (some routers): ask every address on the local /24 networks.
        public async Task ScanSubnets()
        {
            var body = JsonSerializer.Serialize(Info(null), Json);
            using var gate = new SemaphoreSlim(48);
            var tasks = new List<Task>();
            foreach (var ip in LocalAddresses())
            {
                var b = ip.GetAddressBytes();
                for (int i = 1; i < 255; i++)
                {
                    if (i == b[3]) continue;
                    var target = $"{b[0]}.{b[1]}.{b[2]}.{i}";
                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync();
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                            var res = await http.PostAsync($"https://{target}:{DefaultPort}/api/localsend/v2/register",
                                new StringContent(body, Encoding.UTF8, "application/json"), cts.Token);
                            if (res.IsSuccessStatusCode)
                            {
                                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                                AddDevice(doc.RootElement, target, DefaultPort);
                            }
                        }
                        catch { }
                        finally { gate.Release(); }
                    }));
                }
            }
            await Task.WhenAll(tasks);
        }

        static List<IPAddress> LocalAddresses()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    if (name.Contains("vethernet") || name.Contains("virtualbox") || name.Contains("vmware") || name.Contains("hyper-v")) continue;
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
                            list.Add(a.Address);
                }
            }
            catch { }
            return list;
        }

        static string RemoteIp(HttpContext c)
        {
            var ip = c.Connection.RemoteIpAddress;
            if (ip == null) return null;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return ip.ToString();
        }

        // ---------------------------------------------------------------
        // receiving (phone -> PC)
        // ---------------------------------------------------------------
        async Task<IResult> PrepareUpload(HttpContext c)
        {
            JsonElement root;
            try { using var doc = await JsonDocument.ParseAsync(c.Request.Body); root = doc.RootElement.Clone(); }
            catch { return Results.StatusCode(400); }
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return Results.StatusCode(400);

            string ip = RemoteIp(c);
            string alias = ip;
            if (root.TryGetProperty("info", out var info))
            {
                var d = AddDevice(info, ip);
                if (info.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String) alias = a.GetString();
            }

            var inc = new Incoming { SessionId = Guid.NewGuid().ToString("N"), SenderAlias = alias, SenderIp = ip };
            string text = null;
            foreach (var f in files.EnumerateObject())
            {
                var v = f.Value;
                string S(string n) => v.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                long size = v.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                inc.Files.Add((S("id") ?? f.Name, S("fileName") ?? "file", size, Guid.NewGuid().ToString("N")));
                if (S("fileType") == "text/plain" && S("preview") is string p) text = p;
            }
            if (inc.Files.Count == 0) return Results.StatusCode(400);

            // a text message: the text is in the preview, nothing to upload
            if (text != null && inc.Files.Count == 1)
            {
                TextReceived?.Invoke(alias, text);
                return Results.StatusCode(204);
            }

            if (session != null && (DateTime.Now - session.LastActivity).TotalMinutes < 2) return Results.StatusCode(409);
            bool ok = false;
            try { ok = AskAccept != null && await AskAccept(inc); } catch { }
            if (!ok) return Results.StatusCode(403);

            session = inc;
            return Results.Json(new { sessionId = inc.SessionId, files = inc.Files.ToDictionary(f => f.id, f => f.token) });
        }

        async Task<IResult> Upload(HttpContext c)
        {
            var s = session;
            var q = c.Request.Query;
            if (s == null || q["sessionId"] != s.SessionId) return Results.StatusCode(403);
            if (RemoteIp(c) != s.SenderIp) return Results.StatusCode(403);
            var file = s.Files.FirstOrDefault(f => f.id == q["fileId"]);
            if (file.id == null || file.token != q["token"]) return Results.StatusCode(403);

            var path = UniquePath(SaveDir, file.name);
            var tmp = path + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                await using (var fs = File.Create(tmp))
                {
                    var buf = new byte[1 << 16];
                    int n;
                    long lastReport = 0, done = 0;
                    while ((n = await c.Request.Body.ReadAsync(buf, c.RequestAborted)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        s.LastActivity = DateTime.Now;
                        if (done - lastReport > 1 << 20) { lastReport = done; Progress?.Invoke(s); }
                    }
                }
                File.Move(tmp, path, true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                return Results.StatusCode(500);
            }

            lock (s.Saved) s.Saved[file.id] = path;
            Progress?.Invoke(s);
            if (s.Saved.Count == s.Files.Count)
            {
                session = null;
                Received?.Invoke(s);
            }
            return Results.Ok();
        }

        /// Keeps sub-folders the sender used ("photos/a.jpg"), strips anything unsafe and never overwrites.
        static string UniquePath(string dir, string name)
        {
            var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => string.Concat(p.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch))).Trim())
                .Where(p => p.Length > 0 && p != "." && p != "..").ToList();
            if (parts.Count == 0) parts.Add("file");
            var path = Path.Combine(new[] { dir }.Concat(parts).ToArray());
            var stem = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
            var ext = Path.GetExtension(path);
            for (int i = 1; File.Exists(path); i++) path = $"{stem} ({i}){ext}";
            return path;
        }

        // ---------------------------------------------------------------
        // sending (PC -> phone)
        // ---------------------------------------------------------------
        public enum SendResult { Done, Rejected, Busy, NeedsPin, Failed }

        /// Sends files; the phone has to accept first (waits up to 5 minutes). progress = bytes sent, total.
        public async Task<SendResult> SendFiles(Device d, IReadOnlyList<string> paths, Action<long, long> progress, CancellationToken ct)
        {
            var files = paths.Where(File.Exists).Select(p => (id: Guid.NewGuid().ToString("N"), path: p, info: new FileInfo(p))).ToList();
            if (files.Count == 0) return SendResult.Failed;
            var meta = files.ToDictionary(f => f.id, f => (object)new Dictionary<string, object>
            {
                ["id"] = f.id, ["fileName"] = f.info.Name, ["size"] = f.info.Length, ["fileType"] = Mime(f.info.Extension),
                ["metadata"] = new Dictionary<string, object> { ["modified"] = f.info.LastWriteTimeUtc.ToString("o") }
            });
            var res = await Prepare(d, meta, ct);
            if (res.result != SendResult.Done || res.tokens == null) return res.result;

            long total = files.Sum(f => f.info.Length), sent = 0;
            foreach (var f in files)
            {
                if (!res.tokens.TryGetValue(f.id, out var token)) continue;   // phone skipped this one
                await using var fs = File.OpenRead(f.path);
                var body = new ProgressContent(fs, f.info.Length, n => progress?.Invoke(sent + n, total));
                var url = $"{d.BaseUrl}/upload?sessionId={Uri.EscapeDataString(res.session)}&fileId={f.id}&token={Uri.EscapeDataString(token)}";
                var r = await http.PostAsync(url, body, ct);
                if (!r.IsSuccessStatusCode) return SendResult.Failed;
                sent += f.info.Length;
            }
            progress?.Invoke(total, total);
            return SendResult.Done;
        }

        /// Sends a text message (shows up as a message on the phone).
        public async Task<SendResult> SendText(Device d, string text, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("N");
            var bytes = Encoding.UTF8.GetBytes(text);
            var meta = new Dictionary<string, object>
            {
                [id] = new Dictionary<string, object> { ["id"] = id, ["fileName"] = id + ".txt", ["size"] = bytes.Length, ["fileType"] = "text/plain", ["preview"] = text }
            };
            var res = await Prepare(d, meta, ct);
            if (res.result != SendResult.Done || res.tokens == null || !res.tokens.TryGetValue(id, out var token)) return res.result;
            var r = await http.PostAsync($"{d.BaseUrl}/upload?sessionId={Uri.EscapeDataString(res.session)}&fileId={id}&token={Uri.EscapeDataString(token)}", new ByteArrayContent(bytes), ct);
            return r.IsSuccessStatusCode ? SendResult.Done : SendResult.Failed;
        }

        async Task<(SendResult result, string session, Dictionary<string, string> tokens)> Prepare(Device d, Dictionary<string, object> files, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            HttpResponseMessage r;
            try { r = await http.PostAsync(d.BaseUrl + "/prepare-upload", JsonBody(new Dictionary<string, object> { ["info"] = Info(null), ["files"] = files }), cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (SendResult.Rejected, null, null); }
            catch { return (SendResult.Failed, null, null); }
            switch ((int)r.StatusCode)
            {
                case 204: return (SendResult.Done, null, null);
                case 401: return (SendResult.NeedsPin, null, null);
                case 403: return (SendResult.Rejected, null, null);
                case 409: case 429: return (SendResult.Busy, null, null);
            }
            if (!r.IsSuccessStatusCode) return (SendResult.Failed, null, null);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tokens = new Dictionary<string, string>();
            if (root.TryGetProperty("files", out var fl))
                foreach (var p in fl.EnumerateObject()) tokens[p.Name] = p.Value.GetString();
            return (SendResult.Done, root.GetProperty("sessionId").GetString(), tokens);
        }

        static StringContent JsonBody(object o) => new(JsonSerializer.Serialize(o, Json), Encoding.UTF8, "application/json");

        static string Mime(string ext) => ext.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".heic" => "image/heic",
            ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", ".webm" => "video/webm",
            ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".wav" => "audio/wav", ".flac" => "audio/flac",
            ".pdf" => "application/pdf", ".txt" => "text/plain", ".zip" => "application/zip", ".apk" => "application/vnd.android.package-archive",
            _ => "application/octet-stream"
        };

        sealed class ProgressContent : HttpContent
        {
            readonly Stream src; readonly long length; readonly Action<long> report;
            public ProgressContent(Stream s, long len, Action<long> r)
            {
                src = s; length = len; report = r;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            }
            protected override async Task SerializeToStreamAsync(Stream target, TransportContext ctx)
            {
                var buf = new byte[1 << 16];
                long done = 0, last = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await target.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (done - last > 256 * 1024) { last = done; report(done); }
                }
                report(done);
            }
            protected override bool TryComputeLength(out long l) { l = length; return true; }
        }

        public void Dispose()
        {
            stop.Cancel();
            try { udp?.Dispose(); } catch { }
            try { web?.StopAsync().Wait(1000); } catch { }
        }
    }
}
