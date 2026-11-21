using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using TIAgent.Models;

namespace TIAgent.Services
{
    public class BridgeCommandRequest
    {
        public string Action { get; set; } // "generate", "package", "approve", "reject", "connect", "delete", "status", "project", "dbs", "blocks", "tags"
        public string Prompt { get; set; }
        public string Language { get; set; } // "LAD" or "SCL"
        public string BlockType { get; set; } // "FB" or "FC"
        public string TargetFolder { get; set; }
        public bool? AutoApprove { get; set; }
        public string CustomSclCode { get; set; }
        public TiaPackage CustomPackage { get; set; }
        public System.Collections.Generic.List<string> BlocksToDelete { get; set; }
        public System.Collections.Generic.List<string> TagTablesToDelete { get; set; }
        public bool? ClearMainNetworks { get; set; }
        public System.Collections.Generic.List<string> DevicesToDelete { get; set; }
        public System.Collections.Generic.List<string> TechnologyObjectsToDelete { get; set; }
        public bool? UseStages { get; set; }
        public string SourceChannel { get; set; }
    }

    public class BridgeStatusSnapshot
    {
        public bool ok { get; set; } = true;
        public string timestamp { get; set; }
        public string tiaVersion { get; set; }
        public bool isTiaConnected { get; set; }
        public string projectName { get; set; }
        public string projectPath { get; set; }
        public string plcName { get; set; }
        public string importMode { get; set; }
        public string targetFolder { get; set; }
        public string lastUserPrompt { get; set; }
        public object pendingPackage { get; set; }
        public int existingDbsCount { get; set; }
        public int existingBlocksCount { get; set; }
        public int existingTagsCount { get; set; }
        public System.Collections.Generic.List<string> dataBlocks { get; set; } = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.List<object> programBlocks { get; set; } = new System.Collections.Generic.List<object>();
        public System.Collections.Generic.List<string> tagTables { get; set; } = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.List<object> tags { get; set; } = new System.Collections.Generic.List<object>();
        public System.Collections.Generic.List<string> recentSteps { get; set; } = new System.Collections.Generic.List<string>();
    }

    public class AntigravityBridgeService : IDisposable
    {
        private readonly string _inboxDir;
        private readonly string _outboxDir;
        private readonly string _processedDir;
        private readonly string _statusFilePath;
        private readonly string _openApiFilePath;
        private readonly Func<BridgeCommandRequest, Task<object>> _onCommandReceived;
        private readonly Func<BridgeStatusSnapshot> _getStatusSnapshot;

        private TcpListener _tcpListener;
        private FileSystemWatcher _watcher;
        private CancellationTokenSource _cts;
        private bool _isListeningHttp;

        public int Port { get; }
        public bool IsRunning => _isListeningHttp || _watcher != null;
        public string InboxFolder => _inboxDir;
        public string OutboxFolder => _outboxDir;
        public string StatusFilePath => _statusFilePath;

        public AntigravityBridgeService(
            string baseDir,
            int port,
            Func<BridgeCommandRequest, Task<object>> onCommandReceived,
            Func<BridgeStatusSnapshot> getStatusSnapshot)
        {
            Port = port;
            _onCommandReceived = onCommandReceived;
            _getStatusSnapshot = getStatusSnapshot;

            string pkgRoot = Path.Combine(baseDir, "Packages");

            _inboxDir = Path.Combine(pkgRoot, "Inbox");
            _outboxDir = Path.Combine(pkgRoot, "Outbox");
            _processedDir = Path.Combine(pkgRoot, "Processed");
            _statusFilePath = Path.Combine(pkgRoot, "status.json");
            _openApiFilePath = Path.Combine(baseDir, "openapi.json");

            Directory.CreateDirectory(_inboxDir);
            Directory.CreateDirectory(_outboxDir);
            Directory.CreateDirectory(_processedDir);
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();

            // 1. Start Hot-Folder Watcher
            try
            {
                _watcher = new FileSystemWatcher(_inboxDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                _watcher.Created += OnInboxFileCreated;
                _watcher.Changed += OnInboxFileCreated;

                Task.Run(() => ScanInboxFolderAsync());
            }
            catch { }

            // 2. Start Local HTTP Server via TcpListener (Never requires Administrator / URLACL)
            try
            {
                _tcpListener = new TcpListener(IPAddress.Loopback, Port);
                _tcpListener.Start();
                _isListeningHttp = true;
                Task.Run(() => TcpAcceptLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                _isListeningHttp = false;
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge_startup.log"), ex.ToString()); } catch { }
            }

            WriteStatusFile();
        }

        public void WriteStatusFile()
        {
            try
            {
                if (_getStatusSnapshot == null) return;
                var snap = _getStatusSnapshot();
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string json = serializer.Serialize(snap);
                File.WriteAllText(_statusFilePath, json, Encoding.UTF8);
            }
            catch { }
        }

        private readonly HashSet<string> _processingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void OnInboxFileCreated(object sender, FileSystemEventArgs e)
        {
            Task.Run(async () =>
            {
                await Task.Delay(200);
                await ProcessInboxFileAsync(e.FullPath);
            });
        }

        private async Task ScanInboxFolderAsync()
        {
            try
            {
                var files = Directory.GetFiles(_inboxDir, "*.json");
                foreach (var f in files)
                {
                    await ProcessInboxFileAsync(f);
                }
            }
            catch { }
        }

        private async Task ProcessInboxFileAsync(string filePath)
        {
            if (!File.Exists(filePath)) return;
            lock (_processingFiles)
            {
                if (_processingFiles.Contains(filePath)) return;
                _processingFiles.Add(filePath);
            }

            try
            {
                string json = null;
                for (int retry = 0; retry < 5; retry++)
                {
                    try
                    {
                        json = File.ReadAllText(filePath, Encoding.UTF8);
                        break;
                    }
                    catch
                    {
                        await Task.Delay(120);
                    }
                }

                if (string.IsNullOrWhiteSpace(json)) return;

                string destFile = Path.Combine(_processedDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(filePath)}");
                try
                {
                    if (File.Exists(destFile)) File.Delete(destFile);
                    File.Move(filePath, destFile);
                }
                catch
                {
                    try { File.Delete(filePath); } catch { }
                }

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                BridgeCommandRequest req = null;
                try { req = serializer.Deserialize<BridgeCommandRequest>(json); } catch { }
                if (req == null) req = new BridgeCommandRequest();

                // If CustomPackage was null, but the json is a raw TiaPackage:
                if (req.CustomPackage == null &&
                    (json.IndexOf("\"PackageName\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     json.IndexOf("\"Blocks\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     json.IndexOf("\"DataBlocks\"", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    try
                    {
                        var rawPkg = serializer.Deserialize<TiaPackage>(json);
                        if (rawPkg != null && (!string.IsNullOrWhiteSpace(rawPkg.PackageName) || (rawPkg.Blocks != null && rawPkg.Blocks.Count > 0) || (rawPkg.DataBlocks != null && rawPkg.DataBlocks.Count > 0)))
                        {
                            req.CustomPackage = rawPkg;
                            req.Action = "package";
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(req.SourceChannel))
                    req.SourceChannel = "Hot-Folder AI";

                if (req != null && _onCommandReceived != null)
                {
                    if (string.IsNullOrWhiteSpace(req.Action))
                    {
                        bool hasDelete = (req.BlocksToDelete != null && req.BlocksToDelete.Count > 0) ||
                                         (req.TagTablesToDelete != null && req.TagTablesToDelete.Count > 0) ||
                                         (req.DevicesToDelete != null && req.DevicesToDelete.Count > 0) ||
                                         (req.ClearMainNetworks == true);
                        if (hasDelete) req.Action = "delete";
                        else req.Action = req.CustomPackage != null ? "package" : "generate";
                    }

                    var result = await _onCommandReceived(req);
                    string resJson = serializer.Serialize(result);
                    File.WriteAllText(destFile + ".result.json", resJson, Encoding.UTF8);

                    // Also write to Outbox folder with original filename so external agents can easily poll/read response
                    try
                    {
                        string outboxFile = Path.Combine(_outboxDir, Path.GetFileName(filePath) + ".result.json");
                        File.WriteAllText(outboxFile, resJson, Encoding.UTF8);
                    }
                    catch { }

                    WriteStatusFile();
                }
            }
            catch { }
            finally
            {
                lock (_processingFiles)
                {
                    _processingFiles.Remove(filePath);
                }
            }
        }

        private async Task TcpAcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _tcpListener != null)
            {
                TcpClient client = null;
                try
                {
                    client = await _tcpListener.AcceptTcpClientAsync();
                }
                catch
                {
                    if (token.IsCancellationRequested) break;
                    continue;
                }

                _ = Task.Run(() => HandleHttpClientAsync(client));
            }
        }

        private async Task HandleHttpClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    stream.ReadTimeout = 5000;
                    stream.WriteTimeout = 5000;

                    var headerSb = new StringBuilder();
                    byte[] oneByte = new byte[1];
                    while (headerSb.Length < 16384)
                    {
                        int r = await stream.ReadAsync(oneByte, 0, 1);
                        if (r <= 0) break;
                        headerSb.Append((char)oneByte[0]);
                        int len = headerSb.Length;
                        if (len >= 4 &&
                            headerSb[len - 4] == '\r' &&
                            headerSb[len - 3] == '\n' &&
                            headerSb[len - 2] == '\r' &&
                            headerSb[len - 1] == '\n')
                        {
                            break;
                        }
                    }

                    string headerText = headerSb.ToString();
                    if (string.IsNullOrWhiteSpace(headerText)) return;

                    string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    string[] reqLine = lines[0].Split(' ');
                    string method = reqLine.Length > 0 ? reqLine[0].ToUpperInvariant() : "GET";
                    string path = reqLine.Length > 1 ? reqLine[1].ToLowerInvariant() : "/";

                    // Handle CORS Preflight
                    if (method == "OPTIONS")
                    {
                        string optHeader =
                            "HTTP/1.1 204 No Content\r\n" +
                            "Access-Control-Allow-Origin: *\r\n" +
                            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                            "Access-Control-Allow-Headers: Content-Type, Authorization, Accept\r\n" +
                            "Connection: close\r\n\r\n";
                        byte[] optBytes = Encoding.ASCII.GetBytes(optHeader);
                        await stream.WriteAsync(optBytes, 0, optBytes.Length);
                        await stream.FlushAsync();
                        return;
                    }

                    // Handle OpenAPI / Swagger JSON Specification
                    if (method == "GET" && (path == "/swagger.json" || path == "/openapi.json" || path == "/api/openapi.json"))
                    {
                        string openApiJson = "{}";
                        try
                        {
                            if (File.Exists(_openApiFilePath))
                                openApiJson = File.ReadAllText(_openApiFilePath, Encoding.UTF8);
                        }
                        catch { }

                        byte[] openApiBytes = Encoding.UTF8.GetBytes(openApiJson);
                        string openApiHeader =
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: application/json; charset=utf-8\r\n" +
                            "Access-Control-Allow-Origin: *\r\n" +
                            $"Content-Length: {openApiBytes.Length}\r\n" +
                            "Connection: close\r\n\r\n";
                        byte[] headBytes = Encoding.ASCII.GetBytes(openApiHeader);
                        await stream.WriteAsync(headBytes, 0, headBytes.Length);
                        await stream.WriteAsync(openApiBytes, 0, openApiBytes.Length);
                        await stream.FlushAsync();
                        return;
                    }

                    int contentLength = 0;
                    string userAgent = "";
                    foreach (var l in lines)
                    {
                        if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(l.Substring("Content-Length:".Length).Trim(), out contentLength);
                        }
                        else if (l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                        {
                            userAgent = l.Substring("User-Agent:".Length).Trim();
                        }
                    }

                    string body = "";
                    if (contentLength > 0)
                    {
                        byte[] bodyBytes = new byte[contentLength];
                        int totalRead = 0;
                        while (totalRead < contentLength)
                        {
                            int read = await stream.ReadAsync(bodyBytes, totalRead, contentLength - totalRead);
                            if (read <= 0) break;
                            totalRead += read;
                        }
                        body = Encoding.UTF8.GetString(bodyBytes, 0, totalRead);
                    }

                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    object responseObj;

                    if (method == "GET" && (path == "/" || path.StartsWith("/api/status") || path.StartsWith("/api/project") || path.StartsWith("/api/dbs") || path.StartsWith("/api/blocks") || path.StartsWith("/api/tags")))
                    {
                        var snap = _getStatusSnapshot?.Invoke() ?? new BridgeStatusSnapshot { ok = true };
                        if (path.StartsWith("/api/dbs"))
                        {
                            responseObj = new { ok = true, count = snap.dataBlocks.Count, dataBlocks = snap.dataBlocks };
                        }
                        else if (path.StartsWith("/api/blocks"))
                        {
                            responseObj = new { ok = true, count = snap.programBlocks.Count, programBlocks = snap.programBlocks };
                        }
                        else if (path.StartsWith("/api/tags"))
                        {
                            responseObj = new { ok = true, count = snap.tags.Count, tagTables = snap.tagTables, tags = snap.tags };
                        }
                        else
                        {
                            responseObj = snap;
                        }
                    }
                    else
                    {
                        BridgeCommandRequest cmd = null;
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            try { cmd = serializer.Deserialize<BridgeCommandRequest>(body); } catch { }
                        }
                        if (cmd == null) cmd = new BridgeCommandRequest();

                        // Detect source channel
                        if (string.IsNullOrWhiteSpace(cmd.SourceChannel))
                        {
                            if (userAgent.IndexOf("Cursor", StringComparison.OrdinalIgnoreCase) >= 0)
                                cmd.SourceChannel = "Cursor AI";
                            else if (userAgent.IndexOf("TIAgent-MCP", StringComparison.OrdinalIgnoreCase) >= 0)
                                cmd.SourceChannel = "MCP / Agent";
                            else if (userAgent.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0)
                                cmd.SourceChannel = "Claude AI";
                            else
                                cmd.SourceChannel = "External AI";
                        }

                        // If CustomPackage was null, but the body is actually a raw TiaPackage:
                        if (cmd.CustomPackage == null && !string.IsNullOrWhiteSpace(body) &&
                            (body.IndexOf("\"PackageName\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             body.IndexOf("\"Blocks\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             body.IndexOf("\"DataBlocks\"", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            try
                            {
                                var rawPkg = serializer.Deserialize<TiaPackage>(body);
                                if (rawPkg != null && (!string.IsNullOrWhiteSpace(rawPkg.PackageName) || (rawPkg.Blocks != null && rawPkg.Blocks.Count > 0) || (rawPkg.DataBlocks != null && rawPkg.DataBlocks.Count > 0)))
                                {
                                    cmd.CustomPackage = rawPkg;
                                    cmd.Action = "package";
                                }
                            }
                            catch { }
                        }

                        if (path.Contains("/approve")) cmd.Action = "approve";
                        else if (path.Contains("/reject")) cmd.Action = "reject";
                        else if (path.Contains("/connect")) cmd.Action = "connect";
                        else if (path.Contains("/delete") || path.Contains("/remove")) cmd.Action = "delete";
                        else if (path.Contains("/project") || path.Contains("/dbs") || path.Contains("/refresh")) cmd.Action = "project";
                        else if (path.Contains("/package")) cmd.Action = "package";
                        else if (path.Contains("/hardware")) cmd.Action = "hardware";
                        else if (path.Contains("/split") || path.Contains("/stages")) cmd.Action = "split";
                        else if (path.Contains("/gsd")) cmd.Action = "gsd";
                        else if (string.IsNullOrWhiteSpace(cmd.Action))
                        {
                            bool hasDelete = (cmd.BlocksToDelete != null && cmd.BlocksToDelete.Count > 0) ||
                                             (cmd.TagTablesToDelete != null && cmd.TagTablesToDelete.Count > 0) ||
                                             (cmd.DevicesToDelete != null && cmd.DevicesToDelete.Count > 0) ||
                                             (cmd.ClearMainNetworks == true);
                            if (hasDelete) cmd.Action = "delete";
                            else cmd.Action = cmd.CustomPackage != null ? "package" : "generate";
                        }

                        // GET /api/gsd?q=v90 → Prompt = "v90"
                        int qIdx = reqLine.Length > 1 ? reqLine[1].IndexOf('?') : -1;
                        if (qIdx >= 0 && string.IsNullOrWhiteSpace(cmd.Prompt))
                        {
                            foreach (var kv in reqLine[1].Substring(qIdx + 1).Split('&'))
                            {
                                var parts = kv.Split(new[] { '=' }, 2);
                                if (parts.Length == 2 && (parts[0] == "q" || parts[0] == "keyword"))
                                    cmd.Prompt = Uri.UnescapeDataString(parts[1].Replace('+', ' '));
                            }
                        }

                        responseObj = _onCommandReceived != null
                            ? await _onCommandReceived(cmd)
                            : new { ok = false, error = "Handler yok" };

                        WriteStatusFile();
                    }

                    string respJson = serializer.Serialize(responseObj);
                    byte[] respBody = Encoding.UTF8.GetBytes(respJson);
                    string respHeader =
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/json; charset=utf-8\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        $"Content-Length: {respBody.Length}\r\n" +
                        "Connection: close\r\n\r\n";
                    byte[] respHeaderBytes = Encoding.ASCII.GetBytes(respHeader);

                    await stream.WriteAsync(respHeaderBytes, 0, respHeaderBytes.Length);
                    await stream.WriteAsync(respBody, 0, respBody.Length);
                    await stream.FlushAsync();
                }
                catch { }
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _watcher?.Dispose(); } catch { }
            try { _tcpListener?.Stop(); } catch { }
        }
    }
}

