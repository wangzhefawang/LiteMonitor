using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Buffers;
using System.Diagnostics;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.WebServer
{
    public class WebSessionManager
    {
        // 同时处理的连接上限；WebSocket 客户端在整个 ReceiveLoop 期间都占一个名额
        private const int MaxConnections = 32;
        // 初始请求头的读取超时（毫秒）
        private const int HeaderReadTimeoutMs = 5000;

        private readonly ConcurrentDictionary<TcpClient, bool> _wsClients = new();
        private readonly Action<Utf8JsonWriter> _dataProvider;
        private readonly SemaphoreSlim _connSlots = new(MaxConnections, MaxConnections);

        public WebSessionManager(Action<Utf8JsonWriter> dataProvider)
        {
            _dataProvider = dataProvider;
        }

        /// <summary>
        /// 启动广播循环。循环的生命周期完全由 token 决定，Stop 之后旧循环会自行退出。
        /// </summary>
        public void Start(CancellationToken token)
        {
            Task.Run(() => BroadcastLoop(token));
        }

        public void Stop()
        {
            foreach (var client in _wsClients.Keys)
            {
                try { client.Close(); } catch { }
            }
            _wsClients.Clear();
        }

        /// <summary>
        /// 统一处理客户端连接（自动识别 WebSocket 或 HTTP）
        /// </summary>
        public async Task HandleClientAsync(TcpClient client, CancellationToken serviceToken)
        {
            // 超过连接上限直接关闭，不排队
            if (!_connSlots.Wait(0))
            {
                try { client.Close(); } catch { }
                return;
            }

            byte[] buffer = null;
            try
            {
                var stream = client.GetStream();
                stream.WriteTimeout = 5000;

                // 1. 读取请求头 (最多 8KB，5 秒内没读完就放弃，防恶意连接)
                buffer = ArrayPool<byte>.Shared.Rent(8192);
                int bytesRead = await ReadHeaderAsync(stream, buffer, serviceToken);

                if (bytesRead == 0) { client.Close(); return; }

                string requestStr = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // 2. 协议分流
                if (IsWebSocketUpgrade(requestStr))
                {
                    if (Handshake(stream, requestStr))
                    {
                        _wsClients.TryAdd(client, true);
                        await ReceiveLoop(client, serviceToken); // 进入 WS 循环
                    }
                }
                else
                {
                    HandleHttpRequest(stream, requestStr);
                    client.Close(); // HTTP 是短连接
                }
            }
            catch (OperationCanceledException)
            {
                // 请求头读取超时或服务停止，属于预期情况，直接关闭连接，不记日志
                try { client.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Client Error: {ex.Message}");
                try { client.Close(); } catch { }
            }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                // 接收循环结束或握手失败时，都释放此连接。
                try { client.Close(); } catch { }
                _connSlots.Release(); // 与方法开头的 Wait(0) 成对，只释放一次
            }
        }

        // --- 内部网络辅助方法 ---

        private async Task<int> ReadHeaderAsync(NetworkStream stream, byte[] buffer, CancellationToken serviceToken)
        {
            // NetworkStream.ReadTimeout 对 ReadAsync 无效，用链接 token 实现整个请求头的 5 秒总超时
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
            readCts.CancelAfter(HeaderReadTimeoutMs);
            int maxHeaderBytes = Math.Min(8192, buffer.Length);

            int totalBytes = 0;
            while (totalBytes < maxHeaderBytes)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalBytes, maxHeaderBytes - totalBytes), readCts.Token);
                if (read == 0) return 0;
                
                totalBytes += read;
                if (FindHeaderEnd(buffer, totalBytes) >= 0) return totalBytes;
            }
            // 超出上限或未收到完整结束符时，不处理任何 HTTP 或 WebSocket 请求。
            return 0;
        }

        private int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i < length - 3; i++)
            {
                if (buffer[i] == 13 && buffer[i+1] == 10 && buffer[i+2] == 13 && buffer[i+3] == 10)
                    return i + 4; // 返回包含结束符的长度
            }
            return -1;
        }

        private bool IsWebSocketUpgrade(string req)
        {
            return req.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) &&
                   TryGetHeaderValue(req, "Upgrade", out var upgrade) &&
                   upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase);
        }

        // 只读取完整请求头中的独立字段，忽略头名大小写及值两侧的空格、制表符。
        private static bool TryGetHeaderValue(string request, string name, out string value)
        {
            value = "";
            int headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            int lineStart = request.IndexOf("\r\n", StringComparison.Ordinal);
            if (headerEnd < 0 || lineStart < 0) return false;

            bool found = false;
            lineStart += 2;
            while (lineStart < headerEnd)
            {
                int lineEnd = request.IndexOf("\r\n", lineStart, StringComparison.Ordinal);
                string line = request.Substring(lineStart, lineEnd - lineStart);
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    // 拒绝重复字段，避免握手时产生歧义。
                    if (found) return false;
                    value = line.Substring(colon + 1).Trim(' ', '\t');
                    found = true;
                }
                lineStart = lineEnd + 2;
            }
            return found;
        }

        private void HandleHttpRequest(NetworkStream stream, string request)
        {
            string[] lines = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            
            string path = lines[0].Split(' ').Length > 1 ? lines[0].Split(' ')[1] : "/";
            
            if (path.StartsWith("/api/snapshot"))
            {
                if (!CheckAuth(request))
                {
                    SendHttpResponse(stream, 401, "text/plain", Encoding.UTF8.GetBytes("Unauthorized"), 12);
                    return;
                }

                using var ms = new MemoryStream();
                using var writer = new Utf8JsonWriter(ms);
                _dataProvider(writer);
                writer.Flush();
                SendHttpResponse(stream, 200, "application/json", ms.GetBuffer(), (int)ms.Length);
            }
            else if (path == "/favicon.ico")
            {
                SendHttpResponse(stream, 404, "text/html", Array.Empty<byte>(), 0);
            }
            else
            {
                // 默认返回首页
                string iconBase64 = WebPageContent.GetAppIconBase64();
                bool authRequired = !string.IsNullOrEmpty(Settings.Load().WebServerPassword);
                
                string html = WebPageContent.IndexHtml
                    .Replace("{{FAVICON}}", !string.IsNullOrEmpty(iconBase64) ? $"<link rel='icon' type='image/png' href='data:image/png;base64,{iconBase64}'>" : "")
                    .Replace("{{AUTH_REQUIRED}}", authRequired.ToString().ToLower());
                
                byte[] data = Encoding.UTF8.GetBytes(html);
                SendHttpResponse(stream, 200, "text/html; charset=utf-8", data, data.Length);
            }
        }

        private bool CheckAuth(string requestHeader)
        {
            var pwd = Settings.Load().WebServerPassword;
            if (string.IsNullOrEmpty(pwd)) return true;

            string firstLine = requestHeader.Split('\n')[0];
            string[] parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            int queryStart = parts[1].IndexOf('?');
            if (queryStart < 0) return false;

            string? suppliedPassword = null;
            foreach (string parameter in parts[1].Substring(queryStart + 1).Split('&'))
            {
                int equals = parameter.IndexOf('=');
                if (equals < 0 || parameter.Substring(0, equals) != "pwd") continue;
                // 浏览器使用 encodeURIComponent 编码密码；拒绝重复参数，避免认证歧义。
                if (suppliedPassword != null) return false;
                suppliedPassword = System.Net.WebUtility.UrlDecode(parameter.Substring(equals + 1));
            }
            return suppliedPassword == pwd;
        }

        private void SendHttpResponse(NetworkStream stream, int code, string type, byte[] body, int len)
        {
            string headers = $"HTTP/1.1 {code} OK\r\nContent-Type: {type}\r\nContent-Length: {len}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            byte[] hBytes = Encoding.UTF8.GetBytes(headers);
            stream.Write(hBytes, 0, hBytes.Length);
            if (len > 0) stream.Write(body, 0, len);
        }

        // --- WebSocket 核心逻辑 ---

        private async Task BroadcastLoop(CancellationToken token)
        {
            // 本循环独有的快照缓冲，容量不够时自动扩容；每个循环实例各持一份，旧循环退出时不会和新循环共用
            var json = new ArrayBufferWriter<byte>(32 * 1024);

            while (!token.IsCancellationRequested)
            {
                // [Optimization] 如果没有任何客户端连接，降低循环频率以节省资源
                if (_wsClients.IsEmpty)
                {
                    try { await Task.Delay(2000, token); } catch (OperationCanceledException) { break; }
                    continue;
                }

                try
                {
                    json.ResetWrittenCount();
                    using (var writer = new Utf8JsonWriter(json))
                    {
                        _dataProvider(writer);
                        writer.Flush();
                    }
                    int payloadLen = json.WrittenCount;

                    int headerLen = 2;
                    if (payloadLen >= 65536) headerLen += 8;
                    else if (payloadLen >= 126) headerLen += 2;
                    int totalLen = headerLen + payloadLen;

                    // Rent a target buffer for the frame
                    byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(totalLen);
                    try
                    {
                        int payloadOffset = EncodeFrameHeader(payloadLen, frameBuffer);
                        json.WrittenSpan.CopyTo(frameBuffer.AsSpan(payloadOffset));

                        // Broadcast using frameBuffer
                        var tasks = new List<Task>();
                        // Capture the buffer and length for the closure
                        var bufToSend = frameBuffer; 
                        var lenToSend = totalLen;

                        foreach (var client in _wsClients.Keys)
                        {
                            if (!client.Connected)
                            {
                                _wsClients.TryRemove(client, out _);
                                continue;
                            }
                            // We must NOT return frameBuffer until ALL sends are done.
                            // But since we await Task.WhenAll, it's fine.
                            tasks.Add(SendFrameSafeAsync(client, bufToSend, lenToSend));
                        }

                        if (tasks.Count > 0)
                        {
                            await Task.WhenAll(tasks);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(frameBuffer);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Web.Broadcast", "快照广播失败", ex);
                }

                // 正常广播频率：每秒一次
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            }
        }

        private int EncodeFrameHeader(int payloadLen, byte[] buffer)
        {
            int offset = 0;

            // FIN(1) + RSV(000) + OpCode(1=Text) => 10000001 => 0x81
            buffer[offset++] = 0x81;

            // Mask(0) + Length
            if (payloadLen < 126)
            {
                buffer[offset++] = (byte)payloadLen;
            }
            else if (payloadLen <= 65535)
            {
                buffer[offset++] = 126;
                buffer[offset++] = (byte)((payloadLen >> 8) & 0xFF);
                buffer[offset++] = (byte)(payloadLen & 0xFF);
            }
            else
            {
                buffer[offset++] = 127;
                // 64-bit length
                buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 0;
                buffer[offset++] = (byte)((payloadLen >> 24) & 0xFF);
                buffer[offset++] = (byte)((payloadLen >> 16) & 0xFF);
                buffer[offset++] = (byte)((payloadLen >> 8) & 0xFF);
                buffer[offset++] = (byte)(payloadLen & 0xFF);
            }
            return offset;
        }

        private async Task SendFrameSafeAsync(TcpClient client, byte[] buffer, int length)
        {
            try
            {
                var stream = client.GetStream();
                // [Fix] Add timeout to prevent memory leak from stuck tasks
                using var cts = new CancellationTokenSource(2000); // 2s timeout
                await stream.WriteAsync(buffer, 0, length, cts.Token);
            }
            catch
            {
                // Remove dead client
                _wsClients.TryRemove(client, out _);
                try { client.Close(); } catch { }
            }
        }


        private async Task ReceiveLoop(TcpClient client, CancellationToken serviceToken)
        {
            byte[] buffer = null;
            try
            {
                var stream = client.GetStream();
                // [Optimization] Use ArrayPool for receive buffer
                buffer = ArrayPool<byte>.Shared.Rent(1024);

                while (client.Connected && _wsClients.ContainsKey(client))
                {
                    // 阻塞读取，如果断开会抛出异常或返回0；服务停止时 token 取消会让它抛出 OperationCanceledException
                    int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), serviceToken);
                    if (bytesRead == 0) break; // 客户端关闭连接

                    // 解析帧头判断是否为 Close 帧 (OpCode = 8)
                    // 0x88 = 1000 1000 = FIN + Close
                    if ((buffer[0] & 0x0F) == 0x08)
                    {
                        break; // 收到关闭帧
                    }
                }
            }
            catch { }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                _wsClients.TryRemove(client, out _);
                try { client.Close(); } catch { }
            }
        }

        private bool Handshake(NetworkStream stream, string request)
        {
            try
            {
                if (!CheckAuth(request))
                {
                    SendHttpResponse(stream, 401, "text/plain", Encoding.UTF8.GetBytes("Unauthorized"), 12);
                    return false;
                }

                // 1. 提取并校验握手键，标准要求其为 16 字节随机值的 Base64 编码。
                if (!TryGetHeaderValue(request, "Sec-WebSocket-Key", out var key)) return false;
                if (Convert.FromBase64String(key).Length != 16) return false;

                // 2. 生成 Accept Key (Magic String: 258EAFA5-E914-47DA-95CA-C5AB0DC85B11)
                string magic = key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(magic));
                string acceptKey = Convert.ToBase64String(hash);

                // 3. 发送握手响应
                var response = new StringBuilder();
                response.Append("HTTP/1.1 101 Switching Protocols\r\n");
                response.Append("Connection: Upgrade\r\n");
                response.Append("Upgrade: websocket\r\n");
                response.Append($"Sec-WebSocket-Accept: {acceptKey}\r\n");
                response.Append("\r\n");

                byte[] respBytes = Encoding.UTF8.GetBytes(response.ToString());
                stream.Write(respBytes, 0, respBytes.Length);
                return true;
            }
            catch { return false; }
        }
    }
}
