using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YARG.Core.Logging;

namespace YARG.Core.StreamLink
{
    public enum LinkStatus
    {
        Disconnected,
        Connecting,
        /// <summary>Authenticated (if required) and subscribed.</summary>
        Connected,
        Reconnecting,
        AuthFailed,
    }

    public sealed class StreamLinkSettings
    {
        public string Host     { get; set; } = "127.0.0.1";
        public int    Port     { get; set; } = 8080;
        public string Password { get; set; } = "";

        public Uri Uri => new UriBuilder("ws", Host, Port, "/").Uri;
    }

    /// <summary>
    /// A WebSocket client for Streamer.bot's server. Connects, authenticates, subscribes to General.Custom
    /// events and reconnects forever, all on a background thread. Parsed commands are handed to the callback
    /// on that background thread; acks can be sent from any thread.
    /// </summary>
    public sealed class StreamLinkClient : IDisposable
    {
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        };

        // Streamer.bot sends Hello right away; if it doesn't, assume an old server with no auth and subscribe
        private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(3);

        private const int MAX_PENDING_ACKS = 64;

        private readonly StreamLinkSettings _settings;
        private readonly Action<ParseResult> _onMessage;

        private readonly ConcurrentQueue<StreamAck> _pendingAcks = new();
        private readonly SemaphoreSlim _ackSignal = new(0);
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private CancellationTokenSource? _cts;
        private Task? _loop;
        private int _ackSequence;
        private volatile LinkStatus _status = LinkStatus.Disconnected;

        public LinkStatus Status => _status;

        public StreamLinkClient(StreamLinkSettings settings, Action<ParseResult> onMessage)
        {
            _settings = settings;
            _onMessage = onMessage;
        }

        public void Start()
        {
            if (_loop != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            if (_cts == null)
            {
                return;
            }

            _cts.Cancel();
            try
            {
                _loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Cancellation
            }

            _cts.Dispose();
            _cts = null;
            _loop = null;
            _status = LinkStatus.Disconnected;
        }

        /// <summary>
        /// Queues an ack. It is sent once the connection is up; a bounded number are kept across reconnects.
        /// </summary>
        public void SendAck(StreamAck ack)
        {
            _pendingAcks.Enqueue(ack);
            while (_pendingAcks.Count > MAX_PENDING_ACKS && _pendingAcks.TryDequeue(out var dropped))
            {
                YargLogger.LogWarning($"[StreamLink] Dropping unsent ack for {dropped.CommandId}");
            }

            _ackSignal.Release();
        }

        public void Dispose()
        {
            Stop();
            _ackSignal.Dispose();
            _sendLock.Dispose();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            int attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                if (_status != LinkStatus.AuthFailed)
                {
                    _status = attempt == 0 ? LinkStatus.Connecting : LinkStatus.Reconnecting;
                }

                bool subscribed = false;
                try
                {
                    subscribed = await RunConnectionAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e) when (e is WebSocketException || e is IOException ||
                    e is OperationCanceledException || e is InvalidOperationException)
                {
                    YargLogger.LogDebug($"[StreamLink] Connection lost: {e.Message}");
                }
                catch (Exception e)
                {
                    YargLogger.LogException(e, "[StreamLink] Unexpected client error");
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (_status != LinkStatus.AuthFailed)
                {
                    _status = LinkStatus.Reconnecting;
                }

                attempt = subscribed ? 1 : attempt + 1;
                var delay = Backoff[Math.Min(attempt - 1, Backoff.Length - 1)];
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _status = LinkStatus.Disconnected;
        }

        /// <returns>True if the connection got as far as a successful subscribe.</returns>
        private async Task<bool> RunConnectionAsync(CancellationToken ct)
        {
            using var socket = new ClientWebSocket();
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = connectionCts.Token;

            await socket.ConnectAsync(_settings.Uri, token);
            YargLogger.LogInfo($"[StreamLink] Connected to {_settings.Uri}");

            bool subscribed = false;
            Task? ackLoop = null;
            var handshake = new HandshakeState();

            // Cancelling a pending ReceiveAsync aborts the socket, so the Hello timeout runs on the side
            var helloTimer = Task.Run(async () =>
            {
                await Task.Delay(HelloTimeout, token);
                if (Interlocked.Exchange(ref handshake.Started, 1) == 0)
                {
                    YargLogger.LogWarning("[StreamLink] No Hello received, subscribing without authentication");
                    await SendTextAsync(socket, StreamerBotProtocol.SubscribeRequest(), token);
                }
            }, token);

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    string? text = await ReceiveTextAsync(socket, token);
                    if (text == null)
                    {
                        break;
                    }

                    if (StreamerBotProtocol.TryParseHello(text, out var hello))
                    {
                        if (Interlocked.Exchange(ref handshake.Started, 1) != 0)
                        {
                            continue;
                        }

                        if (hello.RequiresAuth)
                        {
                            string auth = StreamerBotProtocol.ComputeAuthentication(_settings.Password, hello.Salt,
                                hello.Challenge);
                            await SendTextAsync(socket, StreamerBotProtocol.AuthenticateRequest(auth), token);
                        }
                        else
                        {
                            await SendTextAsync(socket, StreamerBotProtocol.SubscribeRequest(), token);
                        }

                        continue;
                    }

                    if (StreamerBotProtocol.TryParseResponse(text, out var response))
                    {
                        switch (response.Id)
                        {
                            case StreamerBotProtocol.AUTHENTICATE_ID when response.Ok:
                                await SendTextAsync(socket, StreamerBotProtocol.SubscribeRequest(), token);
                                break;
                            case StreamerBotProtocol.AUTHENTICATE_ID:
                                YargLogger.LogError($"[StreamLink] Authentication failed: {response.Error}");
                                _status = LinkStatus.AuthFailed;
                                return false;
                            case StreamerBotProtocol.SUBSCRIBE_ID when response.Ok:
                                subscribed = true;
                                _status = LinkStatus.Connected;
                                YargLogger.LogInfo("[StreamLink] Subscribed to General.Custom");
                                ackLoop = SendAcksAsync(socket, token);
                                break;
                            case StreamerBotProtocol.SUBSCRIBE_ID:
                                YargLogger.LogError($"[StreamLink] Subscribe failed: {response.Error}");
                                return false;
                            default:
                                if (!response.Ok)
                                {
                                    YargLogger.LogWarning($"[StreamLink] Request {response.Id} failed: {response.Error}");
                                }
                                break;
                        }

                        continue;
                    }

                    var parsed = StreamMessageParser.Parse(text);
                    if (parsed.Kind != ParseKind.Ignored)
                    {
                        _onMessage(parsed);
                    }
                }
            }
            finally
            {
                connectionCts.Cancel();
                foreach (var task in new[] { ackLoop, helloTimer })
                {
                    if (task == null) continue;
                    try
                    {
                        await task;
                    }
                    catch (Exception)
                    {
                        // Cancelled, or the socket already failed (the receive loop reports that)
                    }
                }
            }

            return subscribed;
        }

        private sealed class HandshakeState
        {
            public int Started;
        }

        private async Task SendAcksAsync(ClientWebSocket socket, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                if (!_pendingAcks.TryPeek(out var ack))
                {
                    await _ackSignal.WaitAsync(ct);
                    continue;
                }

                string json = StreamerBotProtocol.AckRequest(ack, Interlocked.Increment(ref _ackSequence));
                await SendTextAsync(socket, json, ct);
                // Only drop it once it's actually been handed to the socket
                _pendingAcks.TryDequeue(out _);
            }
        }

        private async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _sendLock.WaitAsync(ct);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <returns>The next text message, or null if the server closed the connection.</returns>
        private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                    }
                    catch (WebSocketException)
                    {
                    }

                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        message.SetLength(0);
                        continue;
                    }

                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int) message.Length);
                }
            }
        }
    }
}
