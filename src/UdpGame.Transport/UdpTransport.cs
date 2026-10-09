using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UdpGame.Transport;

public sealed class UdpTransport : IDisposable
{
    private readonly UdpClient _udp;
    private readonly NetworkEmulator? _emulator;
    private readonly NetworkEmulator? _receiveEmulator;
    private readonly object _sendLock = new();
    private readonly object _pendingLock = new();
    private readonly List<Task> _pendingSends = [];
    private bool _disposed;
    private readonly PriorityQueue<(byte[] Bytes, IPEndPoint Sender), long> _incoming = new();
    private int _receiveTimeoutMilliseconds;

    private UdpTransport(UdpClient udp, NetworkEmulator? emulator, NetworkEmulator? receiveEmulator = null)
    {
        _udp = udp;
        _emulator = emulator;
        _receiveEmulator = receiveEmulator;
        _receiveTimeoutMilliseconds = udp.Client.ReceiveTimeout;
    }

    public int DroppedSendCount { get; private set; }
    public int DroppedReceiveCount { get; private set; }

    public static UdpTransport Connect(
        string host,
        int port,
        int receiveTimeoutMilliseconds,
        NetworkProfile? profile = null,
        NetworkProfile? receiveProfile = null)
    {
        var udp = new UdpClient();
        udp.Client.ReceiveTimeout = receiveTimeoutMilliseconds;
        udp.Connect(host, port);
        return new UdpTransport(udp,
            profile is null ? null : new NetworkEmulator(profile),
            receiveProfile is null ? null : new NetworkEmulator(receiveProfile));
    }

    public static UdpTransport Bind(int port)
    {
        return new UdpTransport(new UdpClient(port), null);
    }

    public void SetReceiveTimeout(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        _receiveTimeoutMilliseconds = milliseconds;
        _udp.Client.ReceiveTimeout = milliseconds;
    }

    public bool Send(ReadOnlySpan<byte> datagram)
    {
        ThrowIfDisposed();
        byte[] bytes = datagram.ToArray();
        EmulationDecision decision = _emulator?.Next() ?? new EmulationDecision(false, 0);

        if (decision.Drop)
        {
            DroppedSendCount++;
            return false;
        }

        if (decision.DelayMs <= 0)
        {
            SendConnectedNow(bytes);
            return true;
        }

        Task pending = Task.Run(async () =>
        {
            await Task.Delay(decision.DelayMs).ConfigureAwait(false);
            SendConnectedNow(bytes);
        });

        lock (_pendingLock)
        {
            _pendingSends.RemoveAll(task => task.IsCompleted);
            _pendingSends.Add(pending);
        }

        return true;
    }

    public void SendTo(ReadOnlySpan<byte> datagram, IPEndPoint destination)
    {
        ThrowIfDisposed();
        byte[] bytes = datagram.ToArray();
        lock (_sendLock)
        {
            _udp.Send(bytes, bytes.Length, destination);
        }
    }

    public byte[] Receive(ref IPEndPoint sender)
    {
        ThrowIfDisposed();
        if (_receiveEmulator is null)
        {
            return _udp.Receive(ref sender);
        }

        long deadline = _receiveTimeoutMilliseconds == 0 ? long.MaxValue :
            Stopwatch.GetTimestamp() + MillisecondsToTicks(_receiveTimeoutMilliseconds);
        try
        {
            while (true)
            {
                long now = Stopwatch.GetTimestamp();
                if (_incoming.TryPeek(out var queued, out long due) && due <= now)
                {
                    _incoming.Dequeue();
                    sender = queued.Sender;
                    return queued.Bytes;
                }
                if (now >= deadline)
                {
                    throw new SocketException((int)SocketError.TimedOut);
                }
                long wakeAt = _incoming.Count > 0 ? Math.Min(deadline, due) : deadline;
                int waitMs = wakeAt == long.MaxValue ? 0 :
                    Math.Max(1, (int)Math.Min(int.MaxValue, Math.Ceiling((wakeAt - now) * 1000d / Stopwatch.Frequency)));
                _udp.Client.ReceiveTimeout = waitMs;
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = _udp.Receive(ref remote);
                    EmulationDecision decision = _receiveEmulator.Next();
                    if (decision.Drop)
                    {
                        DroppedReceiveCount++;
                        continue;
                    }
                    long availableAt = Stopwatch.GetTimestamp() + MillisecondsToTicks(decision.DelayMs);
                    _incoming.Enqueue((bytes, remote), availableAt);
                }
                catch (SocketException error) when (error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
                {
                    // Re-check scheduled datagrams and the caller's poll deadline.
                }
            }
        }
        finally
        {
            _udp.Client.ReceiveTimeout = _receiveTimeoutMilliseconds;
        }
    }

    private static long MillisecondsToTicks(int milliseconds) =>
        checked((long)Math.Ceiling(milliseconds * (double)Stopwatch.Frequency / 1000d));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Task[] pending;
        lock (_pendingLock)
        {
            pending = [.. _pendingSends];
        }

        if (pending.Length > 0)
        {
            Task.WaitAll(pending);
        }

        _disposed = true;
        _udp.Dispose();
    }

    private void SendConnectedNow(byte[] bytes)
    {
        lock (_sendLock)
        {
            if (_disposed)
            {
                return;
            }

            _udp.Send(bytes, bytes.Length);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
