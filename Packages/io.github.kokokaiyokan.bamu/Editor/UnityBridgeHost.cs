using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static bamu.BridgeJson;

namespace bamu
{
    // Explicit IPv4 loopback binding avoids HTTP.sys URL ACL/admin requirements.
    internal sealed class UnityBridgeHost : IDisposable
    {
        private readonly TcpListener listener;
        private readonly UnityBridgeCore core;
        private readonly string token;
        private readonly CancellationTokenSource stopping;
        private readonly ConcurrentDictionary<TcpClient, byte> clients = new ConcurrentDictionary<TcpClient, byte>();
        private readonly Task accept, observer;
        public int Port { get; }
        public UnityBridgeHost(UnityBridgeCore core, string token, CancellationTokenSource stopping, int port = 48763)
        {
            this.core = core; this.token = token; this.stopping = stopping;
            listener = new TcpListener(IPAddress.Loopback, port); listener.Start(8); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            accept = Task.Run(Accept);
            observer = Task.Run(async () =>
            {
                while (!stopping.IsCancellationRequested)
                {
                    try { core.Pump(); await Task.Delay(3000, stopping.Token); }
                    catch (OperationCanceledException) { break; }
                    catch { try { await Task.Delay(3000, stopping.Token); } catch (OperationCanceledException) { break; } }
                }
            });
        }
        private async Task Accept()
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception) when (stopping.IsCancellationRequested) { break; }
                if (clients.Count >= 8) { client.Close(); continue; }
                clients.TryAdd(client, 0);
                _ = Task.Run(() => Serve(client));
            }
        }
        private static bool Origin(string origin) => Regex.IsMatch(origin, "^chrome-extension://[a-p]{32}$");
        private bool Token(string value)
        {
            var expected = "Bearer " + token; var difference = expected.Length ^ value.Length;
            for (var i = 0; i < expected.Length; i++) difference |= expected[i] ^ (i < value.Length ? value[i] : 0);
            return difference == 0;
        }
        private void Serve(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 10000; client.SendTimeout = 10000;
                using (var stream = client.GetStream())
                {
                    var bytes = new List<byte>();
                    while (bytes.Count < 16384)
                    {
                        var next = stream.ReadByte(); if (next < 0) return; bytes.Add((byte)next);
                        var n = bytes.Count;
                        if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
                    }
                    var text = Encoding.ASCII.GetString(bytes.ToArray());
                    var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None); var request = lines[0].Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
                    {
                        var colon = line.IndexOf(':'); if (colon < 1) { Reply(stream, 400, Obj("error", "Bad request"), ""); return; }
                        var key = line.Substring(0, colon); if (headers.ContainsKey(key)) { Reply(stream, 400, Obj("error", "Duplicate header"), ""); return; }
                        headers[key] = line.Substring(colon + 1).Trim();
                    }
                    string Header(string key) => headers.TryGetValue(key, out var value) ? value : "";
                    var origin = Header("Origin");
                    if (request.Length != 3 || bytes.Count >= 16384 || Header("Host") != "127.0.0.1:" + Port || origin.Length > 0 && !Origin(origin))
                    { Reply(stream, 403, Obj("error", "接続元を確認してください。"), ""); return; }
                    if (request[0] == "OPTIONS")
                    { Reply(stream, Origin(origin) ? 204 : 403, Obj(), origin); return; }
                    if (!Token(Header("Authorization"))) { Reply(stream, 403, Obj("error", "接続キーを確認してください。"), origin); return; }
                    if (Header("Transfer-Encoding").Length > 0 || !int.TryParse(Header("Content-Length").Length == 0 ? "0" : Header("Content-Length"), out var length) || length < 0 || length > 4 * 1024 * 1024)
                    { Reply(stream, 400, Obj("error", "リクエストサイズが不正です。"), origin); return; }
                    try
                    {
                        object response;
                        if (request[0] == "GET" && request[1] == "/v1/status") response = core.Status();
                        else if (request[0] == "POST")
                        {
                            var body = new byte[length]; var offset = 0;
                            while (offset < length) { var n = stream.Read(body, offset, length - offset); if (n == 0) throw new EndOfStreamException(); offset += n; }
                            response = core.Command(request[1], Map(Parse(new UTF8Encoding(false, true).GetString(body))));
                        }
                        else throw new KeyNotFoundException();
                        Reply(stream, 200, response, origin);
                    }
                    catch (KeyNotFoundException) { Reply(stream, 404, Obj("error", "Not found"), origin); }
                    catch (Exception ex) when (ex is InvalidDataException || ex is FormatException || ex is ArgumentException || ex is OverflowException)
                    { Reply(stream, 400, Obj("error", ex.Message), origin); }
                    catch (OperationCanceledException) { Reply(stream, 503, Obj("error", "Unityの再読み込み中です。少し待って再確認してください。"), origin); }
                    catch { Reply(stream, 500, Obj("error", "BLM保存先・連携データを確認してください。"), origin); }
                }
            }
            catch (Exception) { /* No request bodies, paths or credentials in logs. */ }
            finally { clients.TryRemove(client, out _); client.Dispose(); }
        }
        private static void Reply(Stream stream, int status, object value, string origin)
        {
            var body = status == 204 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(Stringify(value));
            var headers = "HTTP/1.1 " + status + " Response\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n";
            if (Origin(origin)) headers += "Access-Control-Allow-Origin: " + origin + "\r\nAccess-Control-Allow-Headers: Authorization, Content-Type\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\n";
            var head = Encoding.ASCII.GetBytes(headers + "\r\n"); stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length); stream.Flush();
        }
        public void Dispose()
        {
            stopping.Cancel(); listener.Stop(); foreach (var client in clients.Keys) client.Close();
            core.WaitForWrites();
            try { Task.WaitAll(new[] { accept, observer }, 5000); } catch (AggregateException) { }
        }
    }
}
