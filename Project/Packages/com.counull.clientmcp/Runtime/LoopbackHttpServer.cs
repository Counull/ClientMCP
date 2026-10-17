using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClientMcp
{
    /// <summary>
    /// 单用途本机 HTTP 接入。使用 TcpListener 避免 Windows HttpListener 的 URL ACL 安装要求。
    /// 只负责有界报文、令牌和来源检查；不在网络线程接触任何 Unity 对象。
    /// </summary>
    internal sealed class LoopbackHttpServer : IDisposable
    {
        const int MaxHeaderBytes = 16384;
        const int MaxBodyBytes = 1024 * 1024;
        readonly TcpListener listener;
        readonly string token;
        readonly Func<string, string, JObject, HttpReply> handler;
        readonly ConcurrentDictionary<TcpClient, byte> connections = new ConcurrentDictionary<TcpClient, byte>();
        readonly SemaphoreSlim connectionSlots = new SemaphoreSlim(8, 8);
        volatile bool stopped;
        public string Endpoint { get; }

        public LoopbackHttpServer(int port, string token, Func<string, string, JObject, HttpReply> handler)
        {
            this.token = token; this.handler = handler;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(8);
            Endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(AcceptConnections) { IsBackground = true, Name = "ClientMCP loopback" }.Start();
        }

        void AcceptConnections()
        {
            while (!stopped)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { if (stopped) return; continue; }
                catch (ObjectDisposedException) { return; }
                if (!connectionSlots.Wait(0)) { client.Close(); continue; }
                connections.TryAdd(client, 0);
                ThreadPool.QueueUserWorkItem(_ => ServeConnection(client));
            }
        }

        void ServeConnection(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 10000; client.SendTimeout = 10000;
                using (NetworkStream stream = client.GetStream())
                {
                    HttpReply response;
                    try { response = ReadAndHandle(stream); }
                    catch (HttpInputException error) { response = HttpReply.Error(error.Status, error.Message); }
                    catch (JsonException) { response = HttpReply.Error(400, "Invalid JSON object."); }
                    catch (Exception error) { response = HttpReply.Json(500, ClientMcpRuntime.Failure(error)); }
                    WriteResponse(stream, response);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                connections.TryRemove(client, out _); client.Close(); connectionSlots.Release();
            }
        }

        HttpReply ReadAndHandle(NetworkStream stream)
        {
            string header = ReadHeaders(stream);
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || first[2] != "HTTP/1.1") throw new HttpInputException(400, "HTTP/1.1 required.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < lines.Length; index++)
            {
                if (lines[index].Length == 0) continue;
                int colon = lines[index].IndexOf(':');
                if (colon <= 0) throw new HttpInputException(400, "Malformed header.");
                string name = lines[index].Substring(0, colon).Trim();
                if (headers.ContainsKey(name)) throw new HttpInputException(400, "Duplicate header.");
                headers.Add(name, lines[index].Substring(colon + 1).Trim());
            }
            ValidateAccess(headers);
            if (headers.ContainsKey("Transfer-Encoding")) throw new HttpInputException(400, "Use Content-Length; chunked requests are not supported.");
            if (headers.TryGetValue("MCP-Protocol-Version", out string protocol) && !McpEndpoint.SupportsVersion(protocol))
                throw new HttpInputException(400, "Unsupported MCP protocol version.");
            JObject body = null;
            if (first[0] == "POST")
            {
                if (!headers.TryGetValue("Content-Length", out string lengthText) || !int.TryParse(lengthText, out int length) || length < 0)
                    throw new HttpInputException(411, "A valid Content-Length is required.");
                if (length > MaxBodyBytes) throw new HttpInputException(413, "Request body exceeds one MiB.");
                if (!headers.TryGetValue("Content-Type", out string contentType) || !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
                    throw new HttpInputException(415, "Content-Type must be application/json.");
                var bytes = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int count = stream.Read(bytes, read, length - read);
                    if (count == 0) throw new HttpInputException(400, "Incomplete body.");
                    read += count;
                }
                using (var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(bytes))) { MaxDepth = 64, DateParseHandling = DateParseHandling.None })
                {
                    body = JObject.Load(reader);
                    if (reader.Read()) throw new HttpInputException(400, "Only one JSON object is accepted.");
                }
            }
            return handler(first[0], first[1], body);
        }

        void ValidateAccess(Dictionary<string, string> headers)
        {
            string port = new Uri(Endpoint).Port.ToString();
            if (!headers.TryGetValue("Host", out string host) || (host != "127.0.0.1:" + port && host != "localhost:" + port))
                throw new HttpInputException(403, "Invalid Host.");
            if (headers.TryGetValue("Origin", out string origin) && origin != Endpoint && origin != "http://localhost:" + port)
                throw new HttpInputException(403, "Invalid Origin.");
            if (!headers.TryGetValue("Authorization", out string authorization) || !ConstantTimeEquals(authorization, "Bearer " + token))
                throw new HttpInputException(401, "A valid Bearer token is required.");
        }

        static bool ConstantTimeEquals(string left, string right)
        {
            if (left.Length != right.Length) return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        static string ReadHeaders(NetworkStream stream)
        {
            var bytes = new List<byte>();
            while (bytes.Count < MaxHeaderBytes)
            {
                int next = stream.ReadByte();
                if (next < 0) throw new HttpInputException(400, "Incomplete headers.");
                bytes.Add((byte)next);
                int count = bytes.Count;
                if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                    return Encoding.ASCII.GetString(bytes.ToArray());
            }
            throw new HttpInputException(431, "Request headers exceed 16 KiB.");
        }

        static void WriteResponse(NetworkStream stream, HttpReply reply)
        {
            byte[] body = reply.Body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(reply.Body.ToString(Formatting.None));
            string reason = reply.Status == 200 ? "OK" : reply.Status == 202 ? "Accepted" : "Request Failed";
            string headers = "HTTP/1.1 " + reply.Status + " " + reason + "\r\nContent-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + body.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\n" +
                (reply.Status == 405 ? "Allow: POST\r\n" : "") + "\r\n";
            byte[] prefix = Encoding.ASCII.GetBytes(headers);
            stream.Write(prefix, 0, prefix.Length);
            if (body.Length != 0) stream.Write(body, 0, body.Length);
        }

        public void Dispose()
        {
            stopped = true; listener.Stop();
            foreach (TcpClient client in connections.Keys) client.Close();
        }

        sealed class HttpInputException : Exception
        {
            public readonly int Status;
            public HttpInputException(int status, string message) : base(message) { Status = status; }
        }
    }

}
