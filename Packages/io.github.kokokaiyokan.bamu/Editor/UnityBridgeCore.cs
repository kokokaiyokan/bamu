using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using static bamu.BridgeJson;

namespace bamu
{
    internal sealed class UnityBridgeCore
    {
        internal const string Version = "0.1.0";
        private readonly object gate = new object();
        private readonly string home;
        private readonly Func<BlmLibrary> library;
        private readonly Func<double> clock;
        private readonly CancellationToken cancellation;
        internal Dictionary<string, object> State { get; private set; }
        private static readonly string[] Pending = { "Queued", "Retry", "Preparing", "ReadyForBLM", "AwaitingBLM", "Verifying", "NeedsAttention" };
        private static readonly string[] Active = { "Preparing", "ReadyForBLM", "AwaitingBLM", "Verifying", "NeedsAttention" };
        private double Now => clock();
        private static string Utc => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        private Dictionary<string, object> Files => Map(State["files"]);
        private Dictionary<string, object> Watches => Map(State["watches"]);
        private Dictionary<string, object> Metadata => Map(State["metadata"]);
        private List<object> Jobs => List(State["jobs"]);
        private List<object> Revisions => List(State["revisions"]);
        private static IEnumerable<Dictionary<string, object>> Records(IEnumerable<object> values) => values.Select(Map);
        public UnityBridgeCore(string home, Func<BlmLibrary> library, CancellationToken cancellation, Func<double> clock = null)
        {
            this.home = Path.GetFullPath(home); this.library = library; this.cancellation = cancellation;
            this.clock = clock ?? (() => (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
            Directory.CreateDirectory(home);
            var path = Path.Combine(home, "state.json");
            State = File.Exists(path) ? Map(Parse(File.ReadAllText(path))) : Obj("schema_version", 1);
            if (Num(State, "schema_version") != 1) throw new InvalidDataException("未対応の連携データです。元のファイルを保持しました。");
            foreach (var key in new[] { "files", "watches", "metadata" }) { if (!State.ContainsKey(key)) State[key] = Obj(); Map(State[key]); }
            foreach (var key in new[] { "jobs", "revisions" }) { if (!State.ContainsKey(key)) State[key] = new List<object>(); List(State[key]); }
            foreach (var pair in Files)
                if (Str(FileRecord(Map(pair.Value)), "key") != pair.Key) throw new InvalidDataException("配布ファイルの保存キーが不正です。");
            foreach (var job in Records(Jobs))
                if (!Regex.IsMatch(Str(job, "id"), "^[a-zA-Z0-9_-]{1,128}$")) throw new InvalidDataException("取得記録の識別子が不正です。");
            if (File.Exists(path) && !File.Exists(path + ".pre-unity.bak")) File.Copy(path, path + ".pre-unity.bak");
            foreach (var job in Records(Jobs))
            {
                var state = Str(job, "state");
                if (state == "Preparing") job["state"] = "Queued";
                if (state == "ReadyForBLM") job.Remove("preflight_checked_at");
                if (Str(job, "source") != "manual_missing" && new[] { "Downloading", "Processing", "Queued", "Retry", "ReadyForBLM" }.Contains(Str(job, "state")))
                { job["state"] = "Cancelled"; job["message"] = "旧自動取得を停止しました。"; }
            }
            Default("last_catalog", 0); Default("requested", true); Default("interval_hours", 6); Default("browser_status", "Chrome拡張の接続待ち");
            State["transport"] = "blm"; State["auto_enabled"] = false; State["verify_hours"] = 0;
            Save();
        }
        private void Default(string key, object value) { if (!State.ContainsKey(key)) State[key] = value; }
        public void WaitForWrites() { lock (gate) { } }
        private void Save() { cancellation.ThrowIfCancellationRequested(); BridgeJson.Save(Path.Combine(home, "state.json"), State); }
        private Dictionary<string, object> Watch(string item) => Watches.TryGetValue(item, out var watch) ? Map(watch) : Obj();
        private Dictionary<string, object> Meta(string item) => Metadata.TryGetValue(item, out var value) ? Map(value) : Obj();
        private bool Target(string item) { var mode = Str(Watch(item), "target_mode", "auto"); return !Bool(Watch(item), "hidden") && (mode == "include" || mode == "auto" && Str(Meta(item), "vrchat_state") == "yes"); }
        private List<string> Recommended(string item)
        {
            var selected = new Dictionary<string, Dictionary<string, object>>();
            foreach (var record in Records(Files.Values).Where(f => Str(f, "item_id") == item && Bool(f, "available")))
            {
                var name = Str(record, "name"); var extension = Path.GetExtension(name).ToLowerInvariant();
                if (!new[] { ".zip", ".unitypackage", ".rar", ".7z", ".fbx", ".png", ".jpg", ".jpeg", ".tga" }.Contains(extension) ||
                    Regex.IsMatch(name, @"psd|(?:^|[ _.-])(source|sources|readme)(?:[ _.-]|$)", RegexOptions.IgnoreCase)) continue;
                var version = NativeBlmFiles.Version(name);
                var group = Str(record, "variation_id") + ":" + (version?.Item1 ?? name.ToLowerInvariant());
                if (selected.TryGetValue(group, out var old))
                {
                    var previous = NativeBlmFiles.Version(Str(old, "name"));
                    var comparison = NativeBlmFiles.Compare(version?.Item2 ?? Array.Empty<string>(), previous?.Item2 ?? Array.Empty<string>());
                    if (comparison < 0 || comparison == 0 && long.Parse(Str(record, "file_id")) < long.Parse(Str(old, "file_id"))) continue;
                }
                selected[group] = record;
            }
            return selected.Values.Select(f => Str(f, "key")).OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
        public Dictionary<string, object> Status()
        {
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                var local = library(); var products = new Dictionary<string, Dictionary<string, object>>();
                foreach (var product in local.products.Where(p => !string.IsNullOrEmpty(p.boothId)))
                    products[product.boothId] = Obj("item_id", product.boothId, "name", product.name, "directory", product.directory, "registered", true);
                foreach (var file in Records(Files.Values))
                {
                    var item = Str(file, "item_id");
                    if (!products.ContainsKey(item)) products[item] = Obj("item_id", item, "name", Str(file, "item_name", "BOOTH #" + item), "directory", "", "registered", false);
                }
                foreach (var pair in products)
                {
                    var item = pair.Key; var product = pair.Value; var metadata = Meta(item);
                    if (!Bool(product, "registered") && Str(metadata, "name").Length > 0) product["name"] = metadata["name"];
                    foreach (var key in new[] { "shop", "thumbnail_url", "category", "vrchat_state" }) product[key] = Str(metadata, key);
                    product["tags"] = Value(metadata, "tags", new List<object>());
                    product["target_mode"] = Str(Watch(item), "target_mode", "auto"); product["is_target"] = Target(item);
                    product["recommended_file_keys"] = Recommended(item);
                    product["remote_available"] = Records(Files.Values).Any(f => Str(f, "item_id") == item && Bool(f, "available"));
                    var states = Records(Jobs).Where(j => Str(j, "key").StartsWith(item + ":", StringComparison.Ordinal)).Select(j => Str(j, "state")).ToList();
                    product["sync_state"] = states.Contains("NeedsAttention") ? "NeedsAttention" : states.Any(s => Active.Contains(s)) ? "Processing" :
                        states.Any(s => s == "Queued" || s == "Retry") ? "Queued" : Bool(product, "registered") ? "Saved" : "RemoteOnly";
                }
                var requests = Records(Files.Values).Where(f => Bool(f, "available")).Select(f => Str(f, "item_id")).Distinct().OrderBy(i => i, StringComparer.Ordinal)
                    .Where(i => Num(Meta(i), "retry_after") <= Now && Now - Num(Meta(i), "fetched_at") > (Num(Meta(i), "tag_source_version") == 1 ? 7 * 86400 : 3600)).Take(3).ToList();
                return Copy(Obj("version", Version, "runtime", "unity", "transport", "blm", "products", products.Values.ToList(), "files", Files.Values.ToList(),
                    "watches", Watches.Values.ToList(), "jobs", Jobs.Skip(Math.Max(0, Jobs.Count - 100)).ToList(), "revisions", Revisions,
                    "auto_enabled", false, "verify_hours", 0, "requested", Bool(State, "requested"), "last_catalog", Num(State, "last_catalog"),
                    "interval_hours", Num(State, "interval_hours", 6), "browser_status", Str(State, "browser_status"), "metadata_requests", requests, "library_root", local.root));
            }
        }
        private static string Id(Dictionary<string, object> value, string field)
        {
            var id = Str(value, field); if (!Regex.IsMatch(id, "^[1-9][0-9]{0,17}$")) throw new InvalidDataException("BOOTH識別子が不正です。"); return id;
        }
        private static Dictionary<string, object> FileRecord(Dictionary<string, object> value)
        {
            var item = Id(value, "item_id"); var variation = Id(value, "variation_id"); var file = Id(value, "file_id"); var name = Str(value, "name");
            if (name.Length == 0 || name.IndexOfAny(new[] { '<', '>', '"', '/', '\\', ':', '|', '?', '*' }) >= 0 || name.Any(char.IsControl) || name.TrimEnd(' ', '.') != name ||
                Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) throw new InvalidDataException("配布ファイル名が不正です。");
            return Obj("key", item + ":" + variation + ":" + file, "item_id", item, "variation_id", variation, "file_id", file, "name", name,
                "item_name", Str(value, "item_name").Substring(0, Math.Min(300, Str(value, "item_name").Length)));
        }
        public Dictionary<string, object> Command(string path, Dictionary<string, object> body)
        {
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                switch (path)
                {
                    case "/v1/catalog":
                        var incoming = List(Value(body, "files", new List<object>())).Select(v => FileRecord(Map(v))).ToList();
                        if (incoming.Select(f => Str(f, "key")).Distinct().Count() != incoming.Count) incoming = incoming.GroupBy(f => Str(f, "key")).Select(g => g.Last()).ToList();
                        library(); // Fail before mutating when the BLM database is unavailable.
                        if (Bool(body, "complete")) foreach (var old in Records(Files.Values)) old["available"] = false;
                        foreach (var file in incoming)
                        {
                            var key = Str(file, "key"); var merged = Files.TryGetValue(key, out var existing) ? Map(existing) : Obj();
                            foreach (var pair in file) merged[pair.Key] = pair.Value;
                            merged["available"] = true; merged["last_seen"] = Utc; Files[key] = merged;
                        }
                        State["last_catalog"] = Now; State["requested"] = false; State["browser_status"] = "BOOTH商品一覧を確認済み"; break;
                    case "/v1/metadata":
                        var prepared = Obj();
                        foreach (var entry in List(Value(body, "products", new List<object>())))
                        {
                            var metadata = Map(entry); var item = Id(metadata, "item_id");
                            if (!Records(Files.Values).Any(f => Str(f, "item_id") == item)) throw new InvalidDataException("利用可能なBOOTH商品ではありません。");
                            if (Bool(metadata, "failed")) { var failed = Copy(Meta(item)); failed["retry_after"] = Now + 3600; prepared[item] = failed; continue; }
                            var classification = Str(metadata, "vrchat_state", "unknown");
                            if (!new[] { "yes", "no", "unknown" }.Contains(classification)) throw new InvalidDataException("VRChat分類が不正です。");
                            var image = Str(metadata, "thumbnail_url");
                            if (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Authority != "booth.pximg.net") image = "";
                            prepared[item] = Obj("item_id", item, "name", Trim(metadata, "name", 300), "shop", Trim(metadata, "shop", 200), "thumbnail_url", image,
                                "tags", List(Value(metadata, "tags", new List<object>())).Take(1000).Select(t => Convert.ToString(t)).Where(t => t != null).Select(t => t.Substring(0, Math.Min(100, t.Length))).ToList(),
                                "category", Trim(metadata, "category", 200), "vrchat_state", classification, "fetched_at", Now, "checked_at", Utc, "tag_source_version", Num(metadata, "tag_source_version") == 1 ? 1 : 0);
                        }
                        foreach (var pair in prepared) Metadata[pair.Key] = pair.Value; break;
                    case "/v1/watch":
                        var watchItem = Id(body, "item_id");
                        if (!Watches.ContainsKey(watchItem) && !Records(Files.Values).Any(f => Str(f, "item_id") == watchItem)) throw new InvalidDataException("商品一覧を確認してください。");
                        var keys = List(Value(body, "file_keys", new List<object>())).Select(Convert.ToString).Distinct().ToList();
                        if (keys.Any(k => !Files.ContainsKey(k) || Str(Map(Files[k]), "item_id") != watchItem)) throw new InvalidDataException("他の商品・不明な配布ファイルは指定できません。");
                        var mode = Str(body, "target_mode", "auto"); var selection = Str(body, "selection_mode", "auto");
                        if (!new[] { "auto", "include", "exclude" }.Contains(mode) || !new[] { "auto", "manual" }.Contains(selection)) throw new InvalidDataException("取得対象の設定が不正です。");
                        Watches[watchItem] = Obj("item_id", watchItem, "enabled", Bool(body, "enabled"), "target_mode", mode, "selection_mode", selection, "file_keys", keys, "hidden", Bool(body, "hidden"));
                        if (Bool(body, "hidden"))
                            foreach (var job in Records(Jobs).Where(j => Str(j, "key").Split(':')[0] == watchItem && new[] { "Queued", "Retry", "Preparing", "ReadyForBLM" }.Contains(Str(j, "state"))))
                            { job["state"] = "Cancelled"; job["message"] = "商品を一覧・取得対象から除外しました。"; }
                        break;
                    case "/v1/bulk-missing": return Bulk();
                    case "/v1/next": return Next();
                    case "/v1/check": State["requested"] = true; break;
                    case "/v1/settings": State["interval_hours"] = Math.Max(1, Math.Min(168, Num(body, "interval_hours", 6))); break;
                    case "/v1/browser-status":
                        var code = Str(body, "code"); State["browser_status"] = code == "connected" ? "Chrome拡張に接続しました" : code == "login" ? "ChromeでBOOTHへログインしてください" : code == "rate_limit" ? "BOOTHの制限により待機中" : "Chrome側の確認に失敗しました"; break;
                    case "/v1/failed":
                        var failure = Job(Str(body, "job_id"));
                        if (!new[] { "Saved", "Unchanged", "Existing", "Cancelled" }.Contains(Str(failure, "state"))) Attention(failure, "BLMへの引き渡しを確認できません。ChromeとBLMを確認してください。");
                        break;
                    case "/v1/retry":
                        var retry = Job(Str(body, "job_id"));
                        if (Str(retry, "state") != "NeedsAttention" || Str(retry, "source") != "manual_missing" || !ValidJob(retry)) throw new InvalidDataException("再試行できる手動取得ではありません。");
                        retry["state"] = "Cancelled"; var replacement = NewJob(Map(Files[Str(retry, "key")]), Str(retry, "batch_id")); Jobs.Add(replacement); break;
                    case "/v1/dismiss":
                        var dismissed = Job(Str(body, "job_id")); if (Str(dismissed, "state") != "NeedsAttention") throw new InvalidDataException("確認待ちの取得ではありません。");
                        foreach (var job in Records(Jobs).Where(j => Str(j, "batch_id") == Str(dismissed, "batch_id") && Str(j, "key").Split(':')[0] == Str(dismissed, "key").Split(':')[0] && new[] { "Queued", "Retry", "NeedsAttention" }.Contains(Str(j, "state")))) job["state"] = "Cancelled";
                        break;
                    default: throw new KeyNotFoundException("Not found");
                }
                Save(); return Obj("ok", true);
            }
        }
        private static string Trim(Dictionary<string, object> value, string key, int length) { var text = Str(value, key); return text.Substring(0, Math.Min(length, text.Length)); }
        private Dictionary<string, object> Job(string id) => Records(Jobs).FirstOrDefault(j => Str(j, "id") == id) ?? throw new InvalidDataException("取得記録がありません。");
        private static Dictionary<string, object> NewJob(Dictionary<string, object> file, string batch) => Obj("id", Guid.NewGuid().ToString("N"), "key", Str(file, "key"), "state", "Queued", "created_at", Utc,
            "attempts", 0, "transport", "blm", "source", "manual_missing", "batch_id", batch, "requested_name", Str(file, "name"));
        private bool ValidJob(Dictionary<string, object> job) => !Bool(Watch(Str(job, "key").Split(':')[0]), "hidden") && Str(job, "source") == "manual_missing" && Files.TryGetValue(Str(job, "key"), out var file) && Bool(Map(file), "available") && Str(Map(file), "name") == Str(job, "requested_name");
        private Dictionary<string, object> Bulk()
        {
            var registered = new HashSet<string>(library().products.Select(p => p.boothId)); var batch = Guid.NewGuid().ToString("N"); var items = new HashSet<string>(); var count = 0;
            foreach (var file in Records(Files.Values))
            {
                var item = Str(file, "item_id"); var watch = Watch(item); var key = Str(file, "key");
                if (registered.Contains(item) || !Bool(file, "available") || !Target(item) || !Bool(watch, "enabled", true)) continue;
                var choices = Str(watch, "selection_mode", "auto") == "manual" ? List(Value(watch, "file_keys", new List<object>())).Select(Convert.ToString).ToList() : Recommended(item);
                if (!choices.Contains(key) || Records(Jobs).Any(j => Str(j, "key") == key && Pending.Contains(Str(j, "state")))) continue;
                Jobs.Add(NewJob(file, batch)); items.Add(item); count++;
            }
            Save(); return Obj("ok", true, "queued_products", items.Count, "queued_files", count);
        }
        private Dictionary<string, object> Next()
        {
            foreach (var job in Records(Jobs).Where(j => Str(j, "state") == "ReadyForBLM"))
            {
                if (!ValidJob(job)) { job["state"] = "Cancelled"; Save(); continue; }
                if (Now - Num(job, "preflight_checked_at") > 10) continue;
                var result = Copy(Map(job["file_record"])); result["id"] = Str(job, "id");
                job["state"] = "AwaitingBLM"; job["dispatched_at"] = Now; job["message"] = "BLMの登録・保存を待っています。";
                Save(); return Obj("job", result);
            }
            if (!Records(Jobs).Any(j => Active.Contains(Str(j, "state"))))
                foreach (var job in Records(Jobs).Where(j => new[] { "Queued", "Retry" }.Contains(Str(j, "state")) && Num(j, "retry_at") <= Now))
                {
                    if (!ValidJob(job)) { job["state"] = "Cancelled"; continue; }
                    job["state"] = "Preparing"; job["file_record"] = Copy(Map(Files[Str(job, "key")])); job["attempts"] = Num(job, "attempts") + 1; break;
                }
            Save(); return Obj("job", null);
        }
        public void Pump()
        {
            List<Dictionary<string, object>> work;
            lock (gate) work = Records(Jobs).Where(j => new[] { "Preparing", "ReadyForBLM", "AwaitingBLM", "Verifying", "NeedsAttention" }.Contains(Str(j, "state"))).Select(Copy).ToList();
            foreach (var snapshot in work)
            {
                cancellation.ThrowIfCancellationRequested();
                try { if (new[] { "Preparing", "ReadyForBLM" }.Contains(Str(snapshot, "state"))) Prepare(snapshot); else Observe(snapshot); }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    lock (gate)
                    {
                        var job = Job(Str(snapshot, "id"));
                        if (new[] { "Preparing", "ReadyForBLM" }.Contains(Str(snapshot, "state"))) { job["state"] = "NeedsAttention"; job["message"] = "取得前の保存物を確認できません。BLM保存先と空き容量を確認してください。"; }
                        else { job.Remove("observed_signature"); Wait(job); }
                        Save();
                    }
                }
            }
        }
        private Product ProductFor(string item)
        {
            var local = library(); var product = local.products.FirstOrDefault(p => p.boothId == item);
            if (product != null && !NativeBlmFiles.Inside(product.directory, local.root)) throw new IOException("BLMライブラリ外を参照しています。");
            return product;
        }
        private void Prepare(Dictionary<string, object> snapshot)
        {
            var record = Map(snapshot["file_record"]); var product = ProductFor(Str(record, "item_id"));
            var found = product == null ? Obj() : NativeBlmFiles.Existing(product.directory, Str(record, "name"), cancellation);
            if (found.Count > 0)
            {
                foreach (var file in found.Keys) NativeBlmFiles.Validate(Path.Combine(product.directory, file), cancellation);
                Finish(snapshot, record, product.directory, found, true); return;
            }
            lock (gate)
            {
                var job = Job(Str(snapshot, "id")); if (!new[] { "Preparing", "ReadyForBLM" }.Contains(Str(job, "state"))) return;
                job["baseline"] = found; job["state"] = "ReadyForBLM"; job["preflight_checked_at"] = Now; job["message"] = "BLMへ引き渡せます。"; Save();
            }
        }
        private void Observe(Dictionary<string, object> snapshot)
        {
            var record = Map(snapshot["file_record"]); var product = ProductFor(Str(record, "item_id"));
            var current = product == null ? Obj() : NativeBlmFiles.Scan(product.directory, Str(record, "name"), cancellation);
            var baseline = Map(Value(snapshot, "baseline", Obj()));
            var changed = current.Where(p => !baseline.TryGetValue(p.Key, out var old) || Stringify(old) != Stringify(p.Value)).Select(p => p.Key.Split('/')[0]).Distinct().ToList();
            if (changed.Count == 0) { lock (gate) { Wait(Job(Str(snapshot, "id"))); Save(); } return; }
            if (changed.Count > 1) { lock (gate) { Attention(Job(Str(snapshot, "id")), "複数の保存先が同時に変化しました。BLMで確認してください。"); Save(); } return; }
            var files = Obj(); foreach (var pair in current.Where(p => p.Key.Split('/')[0] == changed[0])) files[pair.Key] = pair.Value;
            var signature = NativeBlmFiles.TextHash(Stringify(files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new object[] { p.Key, p.Value }).ToList()));
            lock (gate)
            {
                var job = Job(Str(snapshot, "id")); if (!new[] { "AwaitingBLM", "Verifying", "NeedsAttention" }.Contains(Str(job, "state"))) return;
                if (Str(job, "observed_signature") != signature)
                { job["observed_signature"] = signature; job["stable_since"] = Now; job["state"] = "Verifying"; job["message"] = "保存内容が安定するまで確認中。"; Save(); return; }
                if (Now - Num(job, "stable_since") < 60) return;
            }
            foreach (var file in files.Keys) NativeBlmFiles.Validate(Path.Combine(product.directory, file), cancellation);
            NativeBlmFiles.Backup(home, Str(record, "item_id"), product.directory, files, cancellation);
            var recheck = NativeBlmFiles.Scan(product.directory, Str(record, "name"), cancellation);
            if (Stringify(current) != Stringify(recheck)) throw new IOException("保存内容が変化しました。");
            Finish(snapshot, record, product.directory, files, false);
        }
        private void Finish(Dictionary<string, object> snapshot, Dictionary<string, object> record, string product, Dictionary<string, object> files, bool existing)
        {
            var logical = files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new object[] { p.Key.Contains("/") ? p.Key.Substring(p.Key.IndexOf('/') + 1) : Str(record, "name"), Num(Map(p.Value), "size"), Str(Map(p.Value), "sha256") }).ToList();
            var digest = NativeBlmFiles.TextHash(Stringify(logical)); var key = Str(record, "key");
            var receipt = Obj("key", key, "item_id", Str(record, "item_id"), "name", Str(record, "name"), "sha256", digest, "saved_at", Utc,
                "target_path", Path.Combine(product, files.Keys.First().Split('/')[0]), "transport", "blm", "digest_kind", "unity-file-tree", "baseline", existing, "files", files,
                "package_hashes", files.Where(p => p.Key.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)).Select(p => Str(Map(p.Value), "sha256")).ToList());
            lock (gate)
            {
                var job = Job(Str(snapshot, "id"));
                if (!new[] { "Preparing", "ReadyForBLM", "AwaitingBLM", "Verifying", "NeedsAttention" }.Contains(Str(job, "state"))) return;
                var unchanged = Records(Revisions).Any(r => Str(r, "key") == key && Str(r, "sha256") == digest);
                cancellation.ThrowIfCancellationRequested();
                BridgeJson.Save(Path.Combine(home, "blm-receipts", Str(record, "item_id"), Str(job, "id") + ".json"), receipt);
                if (!unchanged) Revisions.Add(receipt);
                var file = Map(Files[key]); file["saved_name"] = Str(record, "name"); file["latest_sha256"] = digest; file["saved_at"] = receipt["saved_at"]; file["remote_added"] = false; file["verified_at"] = Now;
                job["state"] = existing ? "Existing" : unchanged ? "Unchanged" : "Saved"; job["message"] = existing ? "BLM保存済み。再取得を省略しました。" : "BLM登録・保存を確認しました。"; job["ended_at"] = Utc;
                Save();
            }
        }
        private void Wait(Dictionary<string, object> job) { if (Now - Num(job, "dispatched_at", Now) >= 1800) Attention(job, "BLMの保存を確認できません。Chrome・BLMを確認し、再試行かスキップを選んでください。"); }
        private static void Attention(Dictionary<string, object> job, string message) { job["state"] = "NeedsAttention"; job["message"] = message; }
    }
}
