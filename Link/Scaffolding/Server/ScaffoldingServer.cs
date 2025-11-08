using PCL.Core.Link.Scaffolding.Client.Models;
using PCL.Core.Link.Scaffolding.Server.Abstractions;
using PCL.Core.Link.Scaffolding.Server.Handlers;
using PCL.Core.Logging;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Scaffolding.Server;

/// <summary>
/// A server for the Scaffolding data exchange protocol.
/// </summary>
public sealed class ScaffoldingServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IServerContext _context;
    private readonly Dictionary<string, IRequestHandler> _handlers;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private Task? _cleanupTask;

    private static readonly TimeSpan _PlayerTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _CleanupInterval = TimeSpan.FromSeconds(5);

    #region Events

    /// <summary>
    /// Occurs when the server has started successfully.
    /// </summary>
    public event Action<IReadOnlyList<PlayerProfile>>? ServerStarted;

    /// <summary>
    /// Occurs when the server has stopped.
    /// </summary>
    public event Action? ServerStopped;

    /// <summary>
    /// Occurs when the server encounters an exception.
    /// </summary>
    public event Action<Exception?>? ServerException;

    /// <summary>
    /// Occurs when the server receives a player profile ping.
    /// </summary>
    public event Action<IReadOnlyList<PlayerProfile>>? PlayerProfilePing;

    private void _OnContextPlayersPing(IReadOnlyList<PlayerProfile> players)
    {
        PlayerProfilePing?.Invoke(players);
    }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="ScaffoldingServer"/> class.
    /// </summary>
    /// <param name="port">The minecraft shared port.</param>
    /// <param name="context">The server running context.</param>
    public ScaffoldingServer(int port, IServerContext context)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _context = context;

        _context.PlayerProfilesPing += _OnContextPlayersPing;

        _handlers = new()
        {
            ["c:player_ping"] = new PlayerPingHandler(),
            ["c:server_port"] = new GetServerPortHandler(),
            ["c:player_profiles_list"] = new GetPlayerProfileListHandler(),
            ["c:protocols"] = new GetProtocolsHandler(),
            ["c:ping"] = new PingHandler()
        };
    }

    /// <summary>
    /// Starts the server.
    /// </summary>
    public void Start()
    {
        try
        {
            _listener.Start();
            LogWrapper.Info("ScaffoldingServer",
                $"Successfully bound to {_listener.LocalEndpoint}. Starting to accept clients.");
        }
        catch (SocketException ex)
        {
            LogWrapper.Error(ex, "ScaffoldingServer",
                $"Failed to start TCP listener on port {((IPEndPoint)_listener.LocalEndpoint).Port}. The port might be in use or blocked.");
            ServerException?.Invoke(ex);
            return;
        }

        _listenTask = _ListenForClientsAsync(_cts.Token);
        _cleanupTask = _MonitorPlayerLivenessAsync(_cts.Token);

        _listenTask.ContinueWith(t =>
        {
            LogWrapper.Error(t.Exception, "ScaffoldingServer",
                "The main listening task failed unexpectedly. The server is no longer accepting new connections.");
            ServerException?.Invoke(t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);

        _cleanupTask.ContinueWith(
            t =>
            {
                LogWrapper.Error(t.Exception, "ScaffoldingServer", "The player cleanup task failed unexpectedly.");
            }, TaskContinuationOptions.OnlyOnFaulted);

        LogWrapper.Debug("ScaffoldingServer", "Successfully scheduled server background tasks.");

        ServerStarted?.Invoke(_context.PlayerProfiles);
    }

    private async Task _MonitorPlayerLivenessAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_CleanupInterval, ct).ConfigureAwait(false);

                var now = DateTime.UtcNow;
                var timedOutPlayerKeys = new List<string>();

                foreach (var (machineId, trackedPlayer) in _context.TrackedPlayers)
                {
                    if (trackedPlayer.Profile.Kind is PlayerKind.HOST)
                    {
                        continue;
                    }

                    if (now - trackedPlayer.LastSeenUtc > _PlayerTimeout)
                    {
                        timedOutPlayerKeys.Add(machineId);
                    }
                }

                if (timedOutPlayerKeys.Count > 0)
                {
                    var listChanged = false;
                    foreach (var key in timedOutPlayerKeys)
                    {
                        if (_context.TrackedPlayers.TryRemove(key, out var removedPlayer))
                        {
                            listChanged = true;
                            LogWrapper.Info("ScaffoldingServer",
                                $"Player '{removedPlayer.Profile.Name}' timed out and was removed.");
                        }
                    }

                    if (listChanged)
                    {
                        _context.OnPlayerProfilesChanged();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogWrapper.Error(ex, "ScaffoldingServer", "An error occurred in the player cleanup task.");
            }
        }
    }

    private async Task _ListenForClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                LogWrapper.Debug("ScaffoldingServer", $"Client connected: {tcpClient.Client.RemoteEndPoint}");
                _ = _HandleClientAsync(tcpClient, ct);
            }
            catch (OperationCanceledException)
            {
                // normal condition, ignore
                break;
            }
            catch (Exception ex)
            {
                LogWrapper.Error(ex, "ScaffoldingServer", "Occurred an exception when server running.");

                try
                {
                    _listener.Stop();
                }
                catch (Exception lisEx)
                {
                    LogWrapper.Error(lisEx, "ScaffoldingServer", "Occurred an exception when stop listening port.");
                }


                ServerStopped?.Invoke();
                break;
            }
        }
    }

    private async Task _HandleClientAsync(TcpClient tcpClient, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString();
        var clientEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";

        LogWrapper.Debug("ScaffoldingServer", $"New connection {sessionId} from {clientEndPoint}.");

        using (tcpClient)
        {
            var stream = tcpClient.GetStream();
            var reader = PipeReader.Create(stream);
            var writer = PipeWriter.Create(stream);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    ReadResult readResult;
                    try
                    {
                        readResult = await reader.ReadAsync(ct).ConfigureAwait(false);
                    }
                    catch (IOException ex) when (ex.InnerException is SocketException
                    {
                        SocketErrorCode: SocketError.ConnectionReset
                    })
                    {
                        LogWrapper.Info("ScaffoldingServer",
                            $"Connection {sessionId} from {clientEndPoint} was closed by the client (Connection Reset).");
                        break;
                    }

                    var buffer = readResult.Buffer;
                    var consumedPosition = buffer.Start;

                    while (_TryParseFrame(in buffer, out var requestFrame, out var frameEndPosition))
                    {
                        LogWrapper.Debug("ScaffoldingServer", $"[{sessionId}] Received frame: {requestFrame.TypeInfo}");
                        if (_handlers.TryGetValue(requestFrame.TypeInfo, out var handler))
                        {
                            var (status, responseBody) = await handler
                                .HandleAsync(requestFrame.Body, _context, sessionId, ct).ConfigureAwait(false);

                            var responseHeader = new byte[5];
                            responseHeader[0] = status;
                            BinaryPrimitives.WriteUInt32BigEndian(responseHeader.AsSpan(1), (uint)responseBody.Length);
                            await writer.WriteAsync(responseHeader, ct).ConfigureAwait(false);
                            if (responseBody.Length > 0)
                            {
                                await writer.WriteAsync(responseBody, ct).ConfigureAwait(false);
                            }

                            await writer.FlushAsync(ct).ConfigureAwait(false);
                        }
                        else
                        {
                            LogWrapper.Warn("ScaffoldingServer",
                                $"[{sessionId}] No handler for type: {requestFrame.TypeInfo}");
                        }

                        consumedPosition = frameEndPosition;
                        buffer = buffer.Slice(consumedPosition);
                    }

                    reader.AdvanceTo(consumedPosition, buffer.End);

                    if (readResult.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                LogWrapper.Warn("ScaffoldingServer",
                    $"Malformed packet from {clientEndPoint} on connection {sessionId}. Closing connection. Reason: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                LogWrapper.Error(ex, "ScaffoldingServer", $"Unexpected error on connection {sessionId}.");
            }
        }
    }

    private static bool _TryParseFrame
        (in ReadOnlySequence<byte> buffer, out (string TypeInfo, byte[] Body) frame, out SequencePosition consumed)
    {
        frame = default;
        consumed = buffer.Start;

        const int maxTypeLength = 128;
        const int maxBodyLength = 65536;

        var reader = new SequenceReader<byte>(buffer);

        // 检查头部是否完整 (1字节类型长度 + 4字节内容长度)
        if (buffer.Length < 1) return false;
        if (!reader.TryRead(out var typeLength)) return false;

        if (typeLength is 0 or > maxTypeLength)
            throw new InvalidDataException($"Invalid frame type length: {typeLength}.");

        if (reader.Remaining < typeLength + 4) return false;

        // 读取类型信息
        Span<byte> typeInfoSpan = stackalloc byte[typeLength];
        if (!reader.TryCopyTo(typeInfoSpan)) return false;
        reader.Advance(typeLength);
        var typeInfo = Encoding.UTF8.GetString(typeInfoSpan);

        // 读取内容长度
        if (!reader.TryReadBigEndian(out int bodyLength32)) return false;
        var bodyLength = (uint)bodyLength32;
        if (bodyLength > maxBodyLength)
            throw new InvalidDataException($"Frame body length {bodyLength} exceeds maximum of {maxBodyLength}.");

        // 检查内容是否完整
        if (reader.Remaining < bodyLength) return false;

        // 提取内容
        var bodyBuffer = reader.Sequence.Slice(reader.Position, bodyLength);
        var body = bodyBuffer.ToArray();

        // 构造帧
        frame = (typeInfo, body);

        reader.Advance(bodyLength);
        consumed = reader.Position;

        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_listenTask != null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                LogWrapper.Error(ex, "ScaffoldingServer",
                    "An exception occurred while awaiting the listen task during disposal.");
            }
        }

        if (_cleanupTask != null)
        {
            try
            {
                await _cleanupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                LogWrapper.Error(ex, "ScaffoldingServer",
                    "An exception occurred while awaiting the cleanup task during disposal.");
            }
        }

        _listener.Stop();

        _cts.Dispose();

        ServerStopped?.Invoke();
    }
}