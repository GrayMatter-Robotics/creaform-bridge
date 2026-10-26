using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CreaformBridge
{
    /// <summary>
    /// Newline-delimited JSON over TCP. One request per line, one reply per line. See PROTOCOL.md.
    /// Request:  {"id": 1, "cmd": "connect"}
    /// Reply:    {"id": 1, "ok": true, "code": "CONNECTED", "message": "...", "data": {...}}
    /// Unsolicited events use {"event": "disconnected"}.
    ///
    /// A client that sends connect becomes a controller. When the last controller goes away without
    /// sending shutdown (socket closed, or silent for ClientIdleS), Creaform.OS is closed after
    /// ClientLossShutdownS unless some client sends connect first.
    /// </summary>
    public sealed class TcpListenerServer
    {
        private readonly CreaformSession mSession;
        private readonly IPAddress mAddress;
        private readonly int mPort;
        private readonly TimeSpan mClientIdle;
        private readonly TimeSpan mClientLossShutdown;
        private readonly List<ClientConnection> mClients = new List<ClientConnection>();
        private readonly object mClientsLock = new object();
        private CancellationTokenSource? mLossTimer;

        private static readonly JsonSerializerOptions ReplyJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private sealed class ClientConnection
        {
            public TcpClient Client { get; }
            public StreamWriter Writer { get; }
            public object WriteLock { get; } = new object();
            public string Remote { get; }
            public volatile bool IsController;
            public volatile bool SentShutdown;

            public ClientConnection(TcpClient pClient)
            {
                Client = pClient;
                Remote = pClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
                Writer = new StreamWriter(pClient.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            }

            public void SendLine(string pLine)
            {
                lock (WriteLock)
                {
                    Writer.WriteLine(pLine);
                }
            }
        }

        public TcpListenerServer(CreaformSession pSession, BridgeConfig pConfig)
        {
            mSession = pSession;
            mAddress = IPAddress.Parse(pConfig.BindAddress);
            mPort = pConfig.Port;
            mClientIdle = TimeSpan.FromSeconds(pConfig.ClientIdleS);
            mClientLossShutdown = TimeSpan.FromSeconds(pConfig.ClientLossShutdownS);
        }

        public async Task RunAsync(CancellationToken pToken)
        {
            var lListener = new TcpListener(mAddress, mPort);
            lListener.Start();
            Console.WriteLine($"[bridge] listening on {mAddress}:{mPort} (bridge {BridgeInfo.Version}, protocol {BridgeInfo.ProtocolVersion})");

            using (pToken.Register(() => lListener.Stop()))
            {
                while (!pToken.IsCancellationRequested)
                {
                    TcpClient lTcp;
                    try
                    {
                        lTcp = await lListener.AcceptTcpClientAsync(pToken);
                    }
                    catch (Exception) when (pToken.IsCancellationRequested)
                    {
                        break;
                    }

                    _ = Task.Run(() => HandleClientAsync(lTcp, pToken));
                }
            }

            CancelLossTimer();
        }

        /// <summary>Sends an event line to every connected client.</summary>
        public void Broadcast(string pEventName)
        {
            string lLine = new JsonObject { ["event"] = pEventName }.ToJsonString();
            List<ClientConnection> lSnapshot;
            lock (mClientsLock)
            {
                lSnapshot = new List<ClientConnection>(mClients);
            }

            foreach (ClientConnection lClient in lSnapshot)
            {
                try
                {
                    lClient.SendLine(lLine);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[bridge] event send to {lClient.Remote} failed: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient pTcp, CancellationToken pToken)
        {
            var lConnection = new ClientConnection(pTcp);
            lock (mClientsLock)
            {
                mClients.Add(lConnection);
            }
            Console.WriteLine($"[bridge] client connected: {lConnection.Remote}");

            try
            {
                using var lReader = new StreamReader(pTcp.GetStream(), new UTF8Encoding(false));
                while (!pToken.IsCancellationRequested)
                {
                    string? lLine;
                    using (var lReadCts = CancellationTokenSource.CreateLinkedTokenSource(pToken))
                    {
                        // Only controllers are expected to send a heartbeat, so only they can go idle.
                        if (lConnection.IsController && mClientIdle > TimeSpan.Zero)
                        {
                            lReadCts.CancelAfter(mClientIdle);
                        }

                        try
                        {
                            lLine = await lReader.ReadLineAsync(lReadCts.Token);
                        }
                        catch (OperationCanceledException) when (!pToken.IsCancellationRequested)
                        {
                            Console.WriteLine($"[bridge] controller {lConnection.Remote} sent nothing for {mClientIdle.TotalSeconds} s, treating it as lost");
                            break;
                        }
                    }

                    if (lLine == null)
                    {
                        break;
                    }
                    if (lLine.Length == 0)
                    {
                        continue;
                    }

                    (string lReply, string? lCommand) = await Task.Run(() => Dispatch(lLine));
                    if (lCommand != "ping")
                    {
                        Console.WriteLine($"[bridge] <- {lConnection.Remote}: {lLine}");
                        Console.WriteLine($"[bridge] -> {lConnection.Remote}: {lReply}");
                    }
                    OnCommandHandled(lConnection, lCommand);
                    lConnection.SendLine(lReply);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                // Client went away or the bridge is shutting down.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bridge] client {lConnection.Remote} error: {ex.Message}");
            }
            finally
            {
                bool lStartLossTimer;
                lock (mClientsLock)
                {
                    mClients.Remove(lConnection);
                    lStartLossTimer = lConnection.IsController
                        && !lConnection.SentShutdown
                        && !pToken.IsCancellationRequested
                        && !mClients.Any(c => c.IsController);
                }
                pTcp.Close();
                Console.WriteLine($"[bridge] client disconnected: {lConnection.Remote}");

                if (lStartLossTimer)
                {
                    StartLossTimer();
                }
            }
        }

        private void OnCommandHandled(ClientConnection pConnection, string? pCommand)
        {
            if (pCommand == "connect")
            {
                pConnection.IsController = true;
                pConnection.SentShutdown = false;
                CancelLossTimer();
            }
            else if (pCommand == "shutdown")
            {
                pConnection.SentShutdown = true;
                pConnection.IsController = false;
                CancelLossTimer();
            }
        }

        private void StartLossTimer()
        {
            if (mClientLossShutdown <= TimeSpan.Zero)
            {
                return;
            }

            CancellationTokenSource lTimer;
            lock (mClientsLock)
            {
                mLossTimer?.Cancel();
                mLossTimer = new CancellationTokenSource();
                lTimer = mLossTimer;
            }

            Console.WriteLine($"[bridge] controlling client lost; closing Creaform.OS in {mClientLossShutdown.TotalSeconds} s unless a client sends connect");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(mClientLossShutdown, lTimer.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[bridge] client reconnected, keeping Creaform.OS open");
                    return;
                }

                Result lResult = mSession.Shutdown();
                Console.WriteLine($"[bridge] client-loss shutdown: {lResult.Code}: {lResult.Message}");
            });
        }

        private void CancelLossTimer()
        {
            lock (mClientsLock)
            {
                mLossTimer?.Cancel();
                mLossTimer = null;
            }
        }

        private (string Reply, string? Command) Dispatch(string pLine)
        {
            JsonNode? lId = null;
            string? lCommand = null;
            Result lResult;

            try
            {
                JsonNode? lRequest = JsonNode.Parse(pLine);
                lId = lRequest?["id"]?.DeepClone();
                lCommand = lRequest?["cmd"]?.GetValue<string>();

                lResult = lCommand switch
                {
                    "ping" => mSession.Ping(),
                    "status" => mSession.GetStatus(),
                    "connect" => mSession.Connect(),
                    "readiness" => mSession.Readiness(),
                    "shutdown" => mSession.Shutdown(),
                    null => new Result(false, "BAD_REQUEST", "Missing \"cmd\""),
                    _ => new Result(false, "UNKNOWN_COMMAND", $"Unknown command \"{lCommand}\""),
                };
            }
            catch (JsonException ex)
            {
                lResult = new Result(false, "BAD_REQUEST", $"Invalid JSON: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                lResult = new Result(false, "BAD_REQUEST", ex.Message);
            }

            var lReply = new JsonObject
            {
                ["id"] = lId,
                ["ok"] = lResult.Ok,
                ["code"] = lResult.Code,
                ["message"] = lResult.Message,
            };
            if (lResult.Data != null)
            {
                lReply["data"] = JsonSerializer.SerializeToNode(lResult.Data, ReplyJsonOptions);
            }
            return (lReply.ToJsonString(), lCommand);
        }
    }
}
