using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using bamu;
using static bamu.BridgeJson;

internal static class NativeReceiverTests
{
    private sealed class Harness : IDisposable
    {
        public readonly string Home, Product, Fixture;
        public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        public bool Registered;
        public double Time = 1800000000;
        public UnityBridgeCore Core;
        public readonly BlmLibrary Library;
        public Harness(string root)
        {
            Home = Path.Combine(TestEnvironment.RunRoot, "native-tests", Guid.NewGuid().ToString("N"));
            Product = Path.Combine(Home, "BLM", "b123"); Directory.CreateDirectory(Product);
            Fixture = Path.Combine(TestEnvironment.RunRoot, "fixtures", "two-assets.unitypackage");
            Library = new BlmLibrary { root = Path.GetDirectoryName(Product), schema = "20261005" };
            Restart();
        }
        public void Restart() { Core = new UnityBridgeCore(Home, () => { Library.products.Clear(); if (Registered) Library.products.Add(new Product { boothId = "123", key = "booth:123", name = "Local", directory = Product }); return Library; }, Cancellation.Token, () => Time); }
        public Dictionary<string, object> File(string id = "789", string name = "asset.zip") => Obj("item_id", "123", "variation_id", "456", "file_id", id, "name", name, "item_name", "Remote");
        public void Catalog(params Dictionary<string, object>[] files) => Core.Command("/v1/catalog", Obj("files", files.ToList(), "complete", true));
        public void Classify() => Core.Command("/v1/metadata", Obj("products", new List<object> { Obj("item_id", "123", "vrchat_state", "yes", "name", "Remote", "tags", new List<object> { "VRChat", "髪型" }, "tag_source_version", 1) }));
        public Dictionary<string, object> Dispatch()
        {
            Core.Command("/v1/next", Obj()); Core.Pump();
            var result = Core.Command("/v1/next", Obj()); return Value(result, "job") == null ? null : Map(result["job"]);
        }
        public string Save(string folder = "asset", string value = null)
        {
            Registered = true;
            var path = Path.Combine(Product, folder, "sample.unitypackage"); Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (value == null) System.IO.File.Copy(Fixture, path, true); else System.IO.File.WriteAllText(path, value);
            Core.Pump(); Time += 61; Core.Pump(); return path;
        }
        public Dictionary<string, object> Job => Map(List(Core.State["jobs"]).Last());
        public void Dispose() { Cancellation.Cancel(); Cancellation.Dispose(); }
    }
    public static void Run(Action<bool, string> check, string root)
    {
        foreach (var stage in new[] { "Queued", "Preparing", "ReadyForBLM", "AwaitingBLM" })
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify(); h.Core.Command("/v1/bulk-missing", Obj());
            if (stage != "Queued") h.Core.Command("/v1/next", Obj());
            if (stage == "ReadyForBLM" || stage == "AwaitingBLM") h.Core.Pump();
            if (stage == "AwaitingBLM") h.Core.Command("/v1/next", Obj());
            h.Core.Command("/v1/watch", Obj("item_id", "123", "enabled", true, "target_mode", "include", "hidden", true));
            check(Str(h.Job, "state") == (stage == "AwaitingBLM" ? "AwaitingBLM" : "Cancelled"), "product exclusion cancels undispatched work at " + stage);
            check(h.Dispatch() == null, "excluded product never dispatches at " + stage);
            if (stage == "AwaitingBLM")
            {
                h.Save();
                check(Str(h.Job, "state") == "Saved", "exclusion keeps observing files already handed to BLM");
                continue;
            }
            h.Restart(); h.Catalog(h.File("790", "new.zip")); h.Classify();
            check(Bool(Map(List(h.Core.Status()["watches"]).Single()), "hidden") && !Bool(Map(List(h.Core.Status()["products"]).Single()), "is_target"), "exclusion survives restart and catalog metadata refresh");
            check(Num(h.Core.Command("/v1/bulk-missing", Obj()), "queued_files") == 0, "exclusion blocks future files even with include and enabled settings");
            h.Core.Command("/v1/watch", Obj("item_id", "123", "enabled", true, "target_mode", "include", "hidden", false));
            check(Num(h.Core.Command("/v1/bulk-missing", Obj()), "queued_files") == 1, "restoring excluded product permits explicit bulk download again");
        }
        var unicode = Map(Parse("{\"text\":\"髪\\n\\u30bf\\u30b0\",\"mtime_ns\":1789999999999999900,\"v\":[true,null,1.25]}"));
        check(Str(unicode, "text") == "髪\nタグ" && Stringify(Parse(Stringify(unicode))) == Stringify(unicode), "native JSON preserves Unicode, escapes and nanosecond integers");
        foreach (var invalid in new[] { "{\"x\":1,\"x\":2}", "[1,]", "01", "true false", "\"bad\\x\"", "1e999" })
        { var rejected = false; try { Parse(invalid); } catch (InvalidDataException) { rejected = true; } check(rejected, "native JSON rejects malformed input " + invalid); }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify();
            check(List(h.Core.State["jobs"]).Count == 0, "native catalog and metadata updates do not schedule downloads");
            check(Num(h.Core.Command("/v1/bulk-missing", Obj()), "queued_products") == 1, "native bulk queues unregistered VRChat products");
            check(Num(h.Core.Command("/v1/bulk-missing", Obj()), "queued_files") == 0, "native repeated bulk clicks do not duplicate pending downloads");
            var job = h.Dispatch();
            check(Str(job, "file_id") == "789" && Str(h.Job, "state") == "AwaitingBLM", "native handoff persists dispatch before returning the official IDs");
            check(Value(h.Core.Command("/v1/next", Obj()), "job") == null, "native handoff never automatically redispatches an awaiting download");
            h.Core.Command("/v1/failed", Obj("job_id", Str(job, "id")));
            check(Str(h.Job, "state") == "NeedsAttention", "native browser dispatch failure requires review");
            h.Core.Command("/v1/retry", Obj("job_id", Str(job, "id")));
            check(h.Dispatch() != null, "native explicit retry creates a new manual request");
            var saved = h.Save();
            check(Str(h.Job, "state") == "Saved" && List(h.Core.State["revisions"]).Count == 1, "native stable registered BLM files create a saved revision");
            var hash = NativeBlmFiles.Hash(saved, CancellationToken.None);
            check(System.IO.File.Exists(Path.Combine(h.Home, "blm-objects", "123", hash)), "native saved files are archived outside BLM directories");
            var receipt = Map(List(h.Core.State["revisions"])[0]);
            check(List(receipt["package_hashes"]).Contains(hash), "native receipts keep Unity package hashes for import-version comparison");
            h.Catalog(h.File("790", "asset_v2.zip"));
            check(Num(h.Core.Command("/v1/bulk-missing", Obj()), "queued_files") == 0, "native bulk excludes registered products even when new files appear");
            h.Core.Command("/v1/failed", Obj("job_id", Str(h.Job, "id")));
            check(Str(h.Job, "state") == "Saved", "late browser errors cannot demote a completed native download");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File(), h.File("790", "extra.zip"));
            check(Num(h.Core.Command("/v1/bulk-missing", Obj()), "queued_files") == 0, "native bulk excludes unclassified products");
            h.Core.Command("/v1/watch", Obj("item_id", "123", "enabled", true, "target_mode", "include", "selection_mode", "manual", "file_keys", new[] { "123:456:790" }));
            check(Num(h.Core.Command("/v1/bulk-missing", Obj()), "queued_files") == 1 && Str(h.Dispatch(), "file_id") == "790", "native manual product and file choices work without requiring classification");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File(), h.File("790", "extra.zip")); h.Classify();
            check(Num(h.Core.Command("/v1/bulk-missing", Obj()), "queued_files") == 2, "native multi-file product is captured as one explicit batch");
            h.Dispatch(); h.Save();
            check(Str(h.Dispatch(), "file_id") == "790", "native batch continues remaining files after BLM registers the first file");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify(); h.Core.Command("/v1/bulk-missing", Obj());
            h.Catalog(h.File("789", "replacement.zip"));
            check(h.Dispatch() == null && Str(h.Job, "state") == "Cancelled", "native queue does not follow files renamed after the explicit request");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify(); h.Core.Command("/v1/bulk-missing", Obj());
            h.Registered = true; var folder = Path.Combine(h.Product, "asset"); Directory.CreateDirectory(folder);
            System.IO.File.Copy(h.Fixture, Path.Combine(folder, "test.unitypackage"));
            check(h.Dispatch() == null && Str(h.Job, "state") == "Existing", "native preflight adopts an already completed BLM save instead of downloading twice");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify(); h.Core.Command("/v1/bulk-missing", Obj()); h.Core.Command("/v1/next", Obj()); h.Core.Pump();
            h.Restart(); h.Registered = true; var folder = Path.Combine(h.Product, "asset"); Directory.CreateDirectory(folder);
            System.IO.File.Copy(h.Fixture, Path.Combine(folder, "test.unitypackage"));
            check(Value(h.Core.Command("/v1/next", Obj()), "job") == null, "resumed ready handoffs wait for a fresh BLM preflight check");
            h.Core.Pump();
            check(Str(h.Job, "state") == "Existing" && Value(h.Core.Command("/v1/next", Obj()), "job") == null, "resumed ready handoffs do not redownload files saved while Unity was closed");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify(); h.Core.Command("/v1/bulk-missing", Obj()); h.Dispatch(); h.Registered = true;
            foreach (var name in new[] { "asset", "asset (1)" })
            {
                var folder = Path.Combine(h.Product, name); Directory.CreateDirectory(folder); System.IO.File.Copy(h.Fixture, Path.Combine(folder, "test.unitypackage"));
            }
            h.Core.Pump();
            check(Str(h.Job, "state") == "NeedsAttention" && List(h.Core.State["revisions"]).Count == 0, "native observer stops on ambiguous simultaneous BLM save folders");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify(); h.Core.Command("/v1/bulk-missing", Obj());
            h.Core.Command("/v1/next", Obj()); h.Restart();
            check(Str(h.Job, "state") == "Queued" && h.Dispatch() != null, "native restart safely resumes pre-dispatch preparation");
            var before = Str(h.Job, "id"); h.Restart();
            check(Value(h.Core.Command("/v1/next", Obj()), "job") == null && Str(h.Job, "id") == before, "native restart observes an existing dispatch without sending it twice");
            h.Save(); check(Str(h.Job, "state") == "Saved", "native restarted receiver completes interrupted BLM observation");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify(); h.Core.Command("/v1/bulk-missing", Obj()); h.Dispatch();
            h.Save(value: "partial package");
            check(Str(h.Job, "state") != "Saved" && List(h.Core.State["revisions"]).Count == 0, "native corrupt partial packages never count as saved");
            h.Time += 1801; h.Core.Pump(); h.Time += 61; h.Core.Pump();
            check(Str(h.Job, "state") == "NeedsAttention", "native invalid downloads eventually require attention instead of being resent");
            h.Core.Command("/v1/dismiss", Obj("job_id", Str(h.Job, "id")));
            check(Str(h.Job, "state") == "Cancelled", "native skip unblocks the manual queue");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify(); h.Core.Command("/v1/bulk-missing", Obj()); h.Dispatch();
            h.Registered = true; var unrelated = Path.Combine(h.Product, "unrelated"); Directory.CreateDirectory(unrelated);
            System.IO.File.Copy(h.Fixture, Path.Combine(unrelated, "test.unitypackage")); h.Core.Pump();
            check(Str(h.Job, "state") == "AwaitingBLM", "native observer ignores unrelated BLM folders");
            h.Save("asset");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File()); h.Classify(); h.Core.Command("/v1/bulk-missing", Obj());
            h.Core.State["future_field"] = Obj("Japanese", "引継ぎ", "integer", 1789999999999999900L);
            var old = Obj("id", "old", "key", "123:456:789", "state", "Queued", "transport", "blm");
            List(h.Core.State["jobs"]).Add(old); h.Core.State["auto_enabled"] = true;
            BridgeJson.Save(Path.Combine(h.Home, "state.json"), h.Core.State); h.Restart();
            check(!Bool(h.Core.State, "auto_enabled") && Str(Map(h.Core.State["future_field"]), "Japanese") == "引継ぎ", "native migration preserves unknown Python state fields and disables automatic downloads");
            check(Str(Map(List(h.Core.State["jobs"]).Last()), "state") == "Cancelled" && System.IO.File.Exists(Path.Combine(h.Home, "state.json.pre-unity.bak")), "native migration backs up legacy state and cancels only old automatic work");
        }
        using (var h = new Harness(root))
        {
            h.Catalog(h.File("789", "Asset_v1.0.zip"), h.File("790", "Asset_v2.0.zip"), h.File("791", "Asset_PSD.zip")); h.Classify();
            var product = Map(List(h.Core.Status()["products"]).Single());
            check(List(product["recommended_file_keys"]).SequenceEqual(new object[] { "123:456:790" }), "native asset recommendations choose the latest family and omit source PSD files");
            var snapshot = h.Core.Status();
            h.Catalog(h.File("800", "new.zip"));
            check(List(snapshot["files"]).Count == 3, "native HTTP status snapshots are detached from concurrent catalog changes");
            var before = Stringify(h.Core.State); var rejected = false;
            try { h.Catalog(h.File("801", "valid.zip"), h.File("802", "../bad.zip")); } catch (InvalidDataException) { rejected = true; }
            check(rejected && Stringify(h.Core.State) == before, "native malformed catalogs are rejected atomically without changing state");
        }
        using (var h = new Harness(root))
        {
            var zip = Path.Combine(h.Home, "test.zip");
            using (var stream = System.IO.File.Create(zip)) using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("test.txt").Open())) writer.Write("sample");
            NativeBlmFiles.Validate(zip, CancellationToken.None);
            var bytes = System.IO.File.ReadAllBytes(zip);
            for (var i = 0; i < bytes.Length - 20; i++) if (BitConverter.ToUInt32(bytes, i) == 0x02014b50) { bytes[i + 16] ^= 1; break; }
            System.IO.File.WriteAllBytes(zip, bytes); var rejected = false;
            try { NativeBlmFiles.Validate(zip, CancellationToken.None); } catch (IOException) { rejected = true; }
            check(rejected, "native ZIP verification checks CRC instead of trusting the central directory");
            var zip64 = Path.Combine(h.Home, "zip64.zip");
            using (var stream = System.IO.File.Create(zip64)) using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                for (var i = 0; i < 65536; i++) archive.CreateEntry("entry" + i);
            NativeBlmFiles.Validate(zip64, CancellationToken.None);
            check(true, "native ZIP64 central directories are verified without requiring Python");
        }
        var legacyPath = Path.Combine(BridgeStorage.LegacySharedHome, "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath));
        using (var seed = new Harness(root))
        {
            seed.Catalog(seed.File()); seed.Classify();
            seed.Core.Command("/v1/bulk-missing", Obj()); seed.Dispatch(); seed.Save();
            System.IO.File.WriteAllText(legacyPath, Stringify(seed.Core.State));
        }
        {
            var clone = Path.Combine(TestEnvironment.RunRoot, "native-synthetic-migration", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(clone);
            var text = System.IO.File.ReadAllText(legacyPath); var before = Map(Parse(text));
            System.IO.File.WriteAllText(Path.Combine(clone, "state.json"), text);
            using (var cancelled = new CancellationTokenSource())
            {
                var native = new UnityBridgeCore(clone, () => BlmLibrary.Load(BridgeStorage.DefaultDatabase), cancelled.Token);
                check(Map(native.State["files"]).Count == Map(before["files"]).Count && List(native.State["revisions"]).Count == List(before["revisions"]).Count,
                    "native migration preserves synthetic catalog and revision counts in an isolated clone");
                check(Stringify(native.State["metadata"]) == Stringify(before["metadata"]) && Stringify(native.State["watches"]) == Stringify(before["watches"]),
                    "native migration preserves synthetic product metadata and file selection settings");
                check(Str(native.Status(), "runtime") == "unity" && Num(native.Status(), "interval_hours") == Num(before, "interval_hours"),
                    "native migrated API reads the synthetic BLM database without changing polling preferences");
            }
        }
        using (var h = new Harness(root))
        using (var host = new UnityBridgeHost(h.Core, "test-token", h.Cancellation, 0))
        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) })
        {
            HttpResponseMessage Request(string method, string path, string token = "test-token", string origin = "", string hostName = null, string body = "{}")
            {
                using (var request = new HttpRequestMessage(new HttpMethod(method), "http://127.0.0.1:" + host.Port + path))
                {
                    if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (origin.Length > 0) request.Headers.TryAddWithoutValidation("Origin", origin);
                    if (hostName != null) request.Headers.Host = hostName;
                    if (method == "POST") request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    return client.SendAsync(request).GetAwaiter().GetResult();
                }
            }
            using (var response = Request("GET", "/v1/status"))
            {
                var status = Map(Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
                check((int)response.StatusCode == 200 && Str(status, "runtime") == "unity", "native receiver serves real authenticated loopback HTTP without Python");
            }
            using (var r = Request("GET", "/v1/status", null)) check((int)r.StatusCode == 403, "native HTTP rejects missing credentials");
            using (var r = Request("GET", "/v1/status", "incorrect")) check((int)r.StatusCode == 403, "native HTTP rejects wrong credentials");
            using (var r = Request("GET", "/v1/status", origin: "https://evil.example")) check((int)r.StatusCode == 403, "native HTTP rejects web-page origins");
            using (var r = Request("GET", "/v1/status", hostName: "evil.example")) check((int)r.StatusCode == 403, "native HTTP rejects DNS-rebinding hostnames");
            var extension = "chrome-extension://" + new string('a', 32);
            using (var r = Request("OPTIONS", "/v1/metadata", null, extension))
                check((int)r.StatusCode == 204 && r.Headers.GetValues("Access-Control-Allow-Origin").Single() == extension, "native receiver supports Chrome extension CORS preflight");
            using (var r = Request("GET", "/v1/next")) check((int)r.StatusCode == 404, "native handoff endpoint requires POST");
            using (var r = Request("POST", "/v1/check")) check((int)r.StatusCode == 200 && Bool(h.Core.State, "requested"), "native HTTP accepts explicit catalog refresh commands");
            using (var r = Request("POST", "/v1/check", body: "{broken")) check((int)r.StatusCode == 400, "native HTTP rejects invalid JSON bodies");
            using (var r = Request("POST", "/v1/check", body: "{}" + new string(' ', 4 * 1024 * 1024 - 2)))
                check((int)r.StatusCode == 200, "native HTTP accepts a complete maximum-size body within the receive deadline");
            using (var oversized = new TcpClient("127.0.0.1", host.Port))
            using (var stream = oversized.GetStream())
            {
                var header = System.Text.Encoding.ASCII.GetBytes("POST /v1/check HTTP/1.1\r\nHost: 127.0.0.1:" + host.Port + "\r\nAuthorization: Bearer test-token\r\nContent-Length: 4194305\r\n\r\n");
                stream.Write(header, 0, header.Length);
                using (var reader = new StreamReader(stream)) check(reader.ReadLine().StartsWith("HTTP/1.1 400"), "native HTTP rejects oversized bodies before reading or allocating them");
            }
            using (var r = Request("POST", "/v1/downloaded")) check((int)r.StatusCode == 404, "native receiver exposes no normal-download upload endpoint");
            using (var r = Request("POST", "/v1/settings", body: "{\"auto_enabled\":true}")) check((int)r.StatusCode == 200 && !Bool(h.Core.State, "auto_enabled"), "native API cannot re-enable the removed automatic-download feature");
        }
        CheckReceiveDeadline(false, root, check);
        CheckReceiveDeadline(true, root, check);
        using (var h = new Harness(root))
        {
            var legacyHome = Path.Combine(h.Home, "legacy");
            var oldKey = ReceiverCredentials.LoadOrCreate(h.Home, legacyHome);
            check(System.Text.RegularExpressions.Regex.IsMatch(oldKey, "^[A-Za-z0-9_-]{43}$"), "receiver creates a random 256-bit connection key");
            check(ReceiverCredentials.LoadOrCreate(h.Home, legacyHome) == oldKey, "receiver startup preserves an existing connection key");
            var stateBefore = System.IO.File.ReadAllBytes(Path.Combine(h.Home, "state.json"));
            using (var host = new UnityBridgeHost(h.Core, () => ReceiverCredentials.ReadToken(h.Home), h.Cancellation, 0))
            using (var other = new UnityBridgeHost(h.Core, () => ReceiverCredentials.ReadToken(h.Home), h.Cancellation, 0))
            using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) })
            {
                int Status(UnityBridgeHost receiver, string key)
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + receiver.Port + "/v1/status"))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                        using (var response = client.SendAsync(request).GetAwaiter().GetResult()) return (int)response.StatusCode;
                    }
                }
                check(Status(host, oldKey) == 200, "receiver accepts original key before rotation");
                var newKey = ReceiverCredentials.Regenerate(h.Home);
                check(newKey != oldKey && ReceiverCredentials.ReadToken(h.Home) == newKey, "key regeneration persists a different connection key");
                check(Status(host, oldKey) == 403 && Status(host, newKey) == 200, "running receiver immediately rejects old key and accepts regenerated key");
                check(Status(other, oldKey) == 403 && Status(other, newKey) == 200, "independent receiver instance observes shared key rotation without restart");
                check(ReceiverCredentials.LoadOrCreate(h.Home, legacyHome) == newKey, "receiver restart cannot restore a revoked key");
                check(System.IO.File.ReadAllBytes(Path.Combine(h.Home, "state.json")).SequenceEqual(stateBefore), "key regeneration preserves catalog and download state");
                var latestKey = ReceiverCredentials.Regenerate(h.Home);
                check(Status(host, oldKey) == 403 && Status(host, newKey) == 403 && Status(host, latestKey) == 200,
                    "successive rotations revoke all earlier connection keys");
            }
        }
    }

    private static void CheckReceiveDeadline(bool body, string root, Action<bool, string> check)
    {
        using (var h = new Harness(root))
        using (var host = new UnityBridgeHost(h.Core, "test-token", h.Cancellation, 0, headerTimeoutMs: 600, bodyTimeoutMs: 800))
        using (var sending = new CancellationTokenSource())
        {
            h.Catalog(); // Clear requested; an incomplete POST must not execute /v1/check.
            var clients = new List<TcpClient>();
            Task writer = null;
            var elapsed = Stopwatch.StartNew();
            try
            {
                for (var i = 0; i < (body ? 1 : 8); i++)
                {
                    var client = new TcpClient("127.0.0.1", host.Port); clients.Add(client);
                    client.SendTimeout = 1000; client.ReceiveTimeout = 4000;
                    var initial = body ? "POST /v1/check HTTP/1.1\r\nHost: 127.0.0.1:" + host.Port +
                        "\r\nAuthorization: Bearer test-token\r\nContent-Length: 100000\r\n\r\n" : "G";
                    var bytes = System.Text.Encoding.ASCII.GetBytes(initial);
                    client.GetStream().Write(bytes, 0, bytes.Length);
                }
                writer = Task.Run(async () =>
                {
                    while (!sending.IsCancellationRequested)
                    {
                        foreach (var client in clients)
                        {
                            try { client.GetStream().WriteByte((byte)' '); }
                            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException) { }
                        }
                        try { await Task.Delay(50, sending.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                });
                foreach (var client in clients)
                {
                    try { check(client.GetStream().ReadByte() == -1, "incomplete " + (body ? "body" : "header") + " is closed without a response"); }
                    catch (IOException ex) when (ex.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionReset)
                    { check(true, "expired connection is reset"); }
                }
                check(elapsed.ElapsedMilliseconds < 3000, "continuous bytes cannot extend the " + (body ? "body" : "header") + " receive deadline");
                check(!Bool(h.Core.State, "requested"), "expired request never executes a command");
                using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) })
                {
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
                    using (var response = http.GetAsync("http://127.0.0.1:" + host.Port + "/v1/status").GetAwaiter().GetResult())
                        check((int)response.StatusCode == 200, "receiver serves authenticated requests after expired slots are released");
                }
            }
            finally
            {
                sending.Cancel(); writer?.GetAwaiter().GetResult();
                foreach (var client in clients) client.Dispose();
            }
        }
    }
}
