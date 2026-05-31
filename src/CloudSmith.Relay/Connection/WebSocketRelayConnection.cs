// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSmith.Relay.Connection;

/// <summary>
/// <see cref="IRelayConnection"/> backed by <see cref="ClientWebSocket"/>.
///
/// Wire format: every frame is UTF-8 JSON serialized via the
/// <see cref="RelayMessage"/> polymorphic contract.
///
/// Reconnect strategy: exponential backoff capped at
/// <see cref="RelayConnectionOptions.MaxBackoff"/>, with jitter to avoid
/// thundering-herd reconnects across multi-Relay deployments. Backoff resets
/// after a successful connect.
/// </summary>
public sealed class WebSocketRelayConnection : IRelayConnection
{
    private readonly RelayConnectionOptions _opts;
    private readonly ILogger<WebSocketRelayConnection> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Random _jitter = new();

    private ClientWebSocket? _socket;
    private Task? _runLoop;
    private CancellationTokenSource? _runCts;
    private int _started;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public WebSocketRelayConnection(
        IOptions<RelayConnectionOptions> opts,
        ILogger<WebSocketRelayConnection> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public event AsyncEventHandler<RelayMessage>? OnMessageReceived;

    public Task OpenAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            _logger.LogDebug("WebSocketRelayConnection.OpenAsync called more than once — ignoring.");
            return Task.CompletedTask;
        }

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runLoop = Task.Run(() => RunAsync(_runCts.Token), _runCts.Token);
        return Task.CompletedTask;
    }

    public async Task SendAsync(RelayMessage msg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(msg);
        var sock = _socket;
        if (sock is null || sock.State != WebSocketState.Open)
            throw new InvalidOperationException("Relay WebSocket is not open.");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, typeof(RelayMessage), JsonOpts);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await sock.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = _opts.InitialBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _socket = await ConnectAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Relay WebSocket connected to {Url}", BuildUri());
                backoff = _opts.InitialBackoff; // reset after a successful connect

                await ReceiveLoopAsync(_socket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Relay WebSocket disconnected; reconnect in {Delay}ms",
                    backoff.TotalMilliseconds);
            }
            finally
            {
                try { _socket?.Dispose(); } catch { /* ignore */ }
                _socket = null;
            }

            if (ct.IsCancellationRequested) break;

            // Jittered exponential backoff.
            var jitterMs = _jitter.Next(0, 250);
            try
            {
                await Task.Delay(backoff + TimeSpan.FromMilliseconds(jitterMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _opts.MaxBackoff.Ticks));
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = _opts.KeepAliveInterval;

        // F-1 fix: challenge-response possession proof.
        // The relay signs a random nonce with its RSA private key so the API can
        // verify against the stored public key — the RelayId header alone is spoofable.
        ws.Options.SetRequestHeader("X-CloudSmith-RelayId", _opts.RelayId);
        AddChallengeHeaders(ws);

        await ws.ConnectAsync(BuildUri(), ct).ConfigureAwait(false);
        return ws;
    }

    /// <summary>
    /// Attach the nonce + signature headers that prove possession of the relay private key.
    /// If no private key path is configured (e.g. first-run before enrollment completes)
    /// the headers are omitted and the API falls back to RelayId-only auth.
    /// </summary>
    private void AddChallengeHeaders(ClientWebSocket ws)
    {
        var keyPath = _opts.PrivateKeyPath;
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
        {
            _logger.LogDebug("No private key at {Path} — skipping challenge-response headers", keyPath);
            return;
        }

        try
        {
            // 32-byte cryptographically random nonce, base64url-encoded.
            var nonceBytes = RandomNumberGenerator.GetBytes(32);
            var nonce = Convert.ToBase64String(nonceBytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var jwt = RelayJwtService.FromPrivateKeyFile(keyPath);
            var sigBytes = jwt.Sign(System.Text.Encoding.UTF8.GetBytes(nonce));
            var sig = Convert.ToBase64String(sigBytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            ws.Options.SetRequestHeader("X-CloudSmith-Nonce", nonce);
            ws.Options.SetRequestHeader("X-CloudSmith-Signature", sig);
        }
        catch (Exception ex)
        {
            // Non-fatal: log and continue without the headers rather than blocking
            // the relay from reconnecting.
            _logger.LogWarning(ex,
                "Failed to generate challenge-response headers — connecting without signature proof");
        }
    }

    private Uri BuildUri()
    {
        var paas = _opts.PaasUrl.TrimEnd('/');
        // Rewrite https:// to wss:// (and http -> ws for local dev).
        if (paas.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            paas = "wss://" + paas["https://".Length..];
        else if (paas.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            paas = "ws://" + paas["http://".Length..];
        return new Uri($"{paas}/api/v1/relays/{_opts.RelayId}/connect");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket sock, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_opts.ReceiveBufferSize);
        try
        {
            using var assembler = new MemoryStream();
            while (sock.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                assembler.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await sock.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await sock.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "peer closed",
                            ct).ConfigureAwait(false);
                        return;
                    }
                    assembler.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (assembler.Length == 0) continue;

                RelayMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<RelayMessage>(
                        assembler.ToArray().AsSpan(),
                        JsonOpts);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize inbound RelayMessage ({Bytes} bytes) — discarding",
                        assembler.Length);
                    continue;
                }

                if (msg is null) continue;

                try
                {
                    var handler = OnMessageReceived;
                    if (handler is not null)
                    {
                        // Sequential dispatch keeps message ordering deterministic.
                        foreach (var d in handler.GetInvocationList().Cast<AsyncEventHandler<RelayMessage>>())
                            await d(this, msg).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OnMessageReceived handler threw");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _runCts?.Cancel(); } catch { }
        try
        {
            if (_runLoop is not null) await _runLoop.ConfigureAwait(false);
        }
        catch { /* swallow */ }
        try { _socket?.Dispose(); } catch { }
        _runCts?.Dispose();
        _sendLock.Dispose();
    }
}
