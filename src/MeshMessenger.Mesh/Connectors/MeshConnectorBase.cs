// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using MeshMessenger.Core;
using MeshMessenger.Discovery;

namespace MeshMessenger.Connectors;

/// <summary>Raised by a session when the node at the address is not the paired node.</summary>
public sealed class IdentityMismatchException(string expected, string actual)
    : Exception($"A different node is at this address ({actual}), not the paired node ({expected}).")
{
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}

/// <summary>Asks discovery to find a paired node that stopped answering at its address.</summary>
/// <returns>The node's new host, or null when it wasn't found.</returns>
public delegate Task<string?> RefindNode(MeshNetwork network, string identity, string lastHost, int port, CancellationToken ct);

public sealed class ConnectorOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Consecutive failed connects before looking for the node elsewhere.</summary>
    public int RefindAfterFailures { get; init; } = 3;

    /// <summary>Re-find retry schedule (1, 2, 5, then every 10 minutes).</summary>
    public IReadOnlyList<TimeSpan> RefindSchedule { get; init; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)];

    /// <summary>Looks for the node elsewhere; null disables re-finding.</summary>
    public RefindNode? Refind { get; init; }

    /// <summary>True when secrets (room / repeater passwords) are stored for this node: moves need confirmation.</summary>
    public Func<bool> HasStoredSecrets { get; init; } = () => false;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// Connection lifecycle shared by both protocols: connect, run a session,
/// back off and reconnect, check the node's identity, and look for the node
/// at a new address when it stops answering. Protocol code lives in the
/// subclasses' <see cref="RunSessionAsync"/>.
/// </summary>
public abstract class MeshConnectorBase : IMeshConnector
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _life = new();
    private Task? _loop;
    private string? _identity;
    private string? _pendingMove;
    private readonly HashSet<string> _ignoredMoves = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextRefind = DateTimeOffset.MinValue;
    private int _refindAttempts;

    protected MeshConnectorBase(NodeConfig config, ConnectorOptions? options = null)
    {
        Config = config;
        Options = options ?? new ConnectorOptions();
        Host = config.Host;
        _identity = string.IsNullOrWhiteSpace(config.Identity) ? null : config.Identity;
    }

    protected NodeConfig Config { get; }

    protected ConnectorOptions Options { get; }

    public string Id => Config.Id;

    public abstract MeshNetwork Network { get; }

    public abstract int DefaultPort { get; }

    public int Port => Config.Port > 0 ? Config.Port : DefaultPort;

    public string Host { get; private set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Config.Name) ? Config.Name : Self?.Name ?? Config.Id;

    public ConnectorState State { get; private set; } = ConnectorState.Disconnected;

    public string? StateDetail { get; private set; }

    public SelfInfo? Self { get; protected set; }

    /// <summary>The paired identity (recorded on first connect).</summary>
    public string? Identity { get { lock (_gate) return _identity; } }

    /// <summary>A new address found for a node with stored secrets, waiting for the operator (Use / Ignore).</summary>
    public string? PendingMove { get { lock (_gate) return _pendingMove; } }

    public abstract IReadOnlyList<PeerInfo> Peers { get; }

    public abstract IReadOnlyList<ChannelInfo> Channels { get; }

    public abstract int MaxTextBytes(ConversationKind kind);

    public abstract string? TitleFor(ConversationKey key);

    public event Action<InboundMessage>? MessageReceived;

    public event Action<DeliveryUpdate>? DeliveryChanged;

    public event Action? Changed;

    /// <summary>Raised when the node's identity is first learned (persist it).</summary>
    public event Action<string>? IdentityLearned;

    /// <summary>Raised when the connector switches to a new address (persist it).</summary>
    public event Action<string>? HostChanged;

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _loop ??= Task.Run(() => RunLoopAsync(_life.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public abstract Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct);

    /// <summary>Operator accepted the pending move.</summary>
    public bool AcceptMove()
    {
        string? to;
        lock (_gate)
        {
            to = _pendingMove;
            _pendingMove = null;
            if (to is null) return false;
            Host = to;
        }
        HostChanged?.Invoke(to);
        SetState(State, $"Moved to {to}.");
        return true;
    }

    /// <summary>Operator declined the pending move; that address isn't offered again this session.</summary>
    public bool IgnoreMove()
    {
        lock (_gate)
        {
            if (_pendingMove is null) return false;
            _ignoredMoves.Add(_pendingMove);
            _pendingMove = null;
        }
        RaiseChanged();
        return true;
    }

    /// <summary>Forget the paired identity; the next node found at the address becomes the paired one.</summary>
    public void Repair()
    {
        lock (_gate) _identity = null;
        RaiseChanged();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        var backoff = Options.MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            SetState(ConnectorState.Connecting, $"Connecting to {Host}:{Port}…");
            var connected = false;
            try
            {
                using var client = new TcpClient { NoDelay = true };
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(Options.ConnectTimeout);
                    await client.ConnectAsync(HostName(Host), Port, connectCts.Token).ConfigureAwait(false);
                }
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                connected = true;
                await using var stream = client.GetStream();
                await RunSessionAsync(stream, ct).ConfigureAwait(false);
                SetState(ConnectorState.Disconnected, "The node closed the connection.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IdentityMismatchException ex)
            {
                SetState(ConnectorState.Error, ex.Message);
                failures = Math.Max(failures, Options.RefindAfterFailures); // look elsewhere straight away
            }
            catch (Exception ex)
            {
                SetState(ConnectorState.Disconnected, connected ? $"Connection lost: {Describe(ex)}" : $"Can't reach {Host}:{Port}: {Describe(ex)}");
            }
            finally
            {
                OnSessionEnded();
            }

            if (ct.IsCancellationRequested) break;
            if (connected && State != ConnectorState.Error)
            {
                // We had a working session; reconnect promptly.
                failures = 0;
                backoff = Options.MinBackoff;
            }
            else
            {
                failures++;
            }

            if (failures >= Options.RefindAfterFailures && await TryRefindAsync(ct).ConfigureAwait(false))
            {
                failures = 0;
                backoff = Options.MinBackoff;
                continue;
            }

            try
            {
                await Task.Delay(backoff, Options.Time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, Options.MaxBackoff.Ticks));
        }
        SetState(ConnectorState.Disconnected, "Stopped.");
    }

    private async Task<bool> TryRefindAsync(CancellationToken ct)
    {
        var identity = Identity;
        if (!Config.FindIfAddressChanges || Options.Refind is null || identity is null) return false;
        if (PendingMove is not null) return false; // waiting for the operator
        var now = Options.Time.GetUtcNow();
        if (now < _nextRefind) return false;

        var previousState = State;
        var previousDetail = StateDetail;
        SetState(ConnectorState.Searching, $"Looking for this node; it stopped answering at {Host}.");
        string? found = null;
        try
        {
            found = await Options.Refind(Network, identity, Host, Port, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            found = null;
        }

        var delay = Options.RefindSchedule[Math.Min(_refindAttempts, Options.RefindSchedule.Count - 1)];
        _refindAttempts++;
        _nextRefind = Options.Time.GetUtcNow() + delay;

        if (found is null || string.Equals(found, Host, StringComparison.OrdinalIgnoreCase) || _ignoredMoves.Contains(found))
        {
            SetState(previousState, $"{previousDetail} Not found elsewhere; trying again later.");
            return false;
        }
        if (!HostValidator.IsLocalHost(found))
        {
            return false;
        }
        if (Options.HasStoredSecrets())
        {
            lock (_gate) _pendingMove = found;
            SetState(ConnectorState.Disconnected, $"Found at {found}. Use or Ignore the new address (passwords are saved for this node).");
            return false;
        }
        var old = Host;
        Host = found;
        _refindAttempts = 0;
        HostChanged?.Invoke(found);
        SetState(ConnectorState.Connecting, $"Moved from {old} to {found}.");
        return true;
    }

    /// <summary>Speak the protocol until the connection ends. Throw to signal failure.</summary>
    protected abstract Task RunSessionAsync(Stream stream, CancellationToken ct);

    /// <summary>Called after every session; clear per-session state.</summary>
    protected virtual void OnSessionEnded()
    {
    }

    /// <summary>Report the node's identity; throws <see cref="IdentityMismatchException"/> when it isn't the paired node.</summary>
    protected void CheckIdentity(string identity)
    {
        bool learned;
        lock (_gate)
        {
            if (_identity is not null && !string.Equals(_identity, identity, StringComparison.OrdinalIgnoreCase))
            {
                throw new IdentityMismatchException(_identity, identity);
            }
            learned = _identity is null;
            _identity = identity;
        }
        _refindAttempts = 0;
        _nextRefind = DateTimeOffset.MinValue;
        if (learned) IdentityLearned?.Invoke(identity);
    }

    protected void SetState(ConnectorState state, string? detail = null)
    {
        State = state;
        StateDetail = detail;
        RaiseChanged();
    }

    protected void RaiseChanged() => Changed?.Invoke();

    protected void PublishInbound(InboundMessage message)
    {
        if (!string.Equals(message.Conversation.ConnectorId, Id, StringComparison.Ordinal))
        {
            message = message with { Conversation = message.Conversation with { ConnectorId = Id } };
        }
        MessageReceived?.Invoke(message);
    }

    protected void PublishDelivery(DeliveryUpdate update) => DeliveryChanged?.Invoke(update);

    /// <summary>Host part of "host" or "host:port" (IPv6 brackets removed).</summary>
    protected static string HostName(string host)
    {
        if (Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri)) return uri.Host.Trim('[', ']');
        return host;
    }

    private static string Describe(Exception ex) => ex switch
    {
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "connection refused (is the node's Wi-Fi API on?)",
            SocketError.HostUnreachable or SocketError.NetworkUnreachable => "unreachable",
            SocketError.TimedOut => "timed out",
            SocketError.HostNotFound => "host name not found",
            _ => se.SocketErrorCode.ToString(),
        },
        OperationCanceledException => "timed out",
        EndOfStreamException => "the node closed the connection",
        IOException io when io.InnerException is SocketException inner => Describe(inner),
        _ => ex.Message,
    };

    public virtual async ValueTask DisposeAsync()
    {
        _life.Cancel();
        Task? loop;
        lock (_gate) loop = _loop;
        if (loop is not null)
        {
            await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        }
        _life.Dispose();
        GC.SuppressFinalize(this);
    }
}
