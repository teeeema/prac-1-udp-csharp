using System.Net;
using System.Net.Sockets;

namespace UdpGame.Transport;

public sealed class UdpTransport : IDisposable
{
    private readonly UdpClient _udp;
    private readonly NetworkEmulator? _emulator;
    private readonly object _sendLock = new();
    private readonly object _pendingLock = new();
    private readonly List<Task> _pendingSends = [];
    private bool _disposed;

    private UdpTransport(UdpClient udp, NetworkEmulator? emulator)
    {
        _udp = udp;
        _emulator = emulator;
    }

    public static UdpTransport Connect(
        string host,
        int port,
        int receiveTimeoutMilliseconds,
        NetworkProfile? profile = null)
    {
        var udp = new UdpClient();
        udp.Client.ReceiveTimeout = receiveTimeoutMilliseconds;
        udp.Connect(host, port);
        return new UdpTransport(udp, profile is null ? null : new NetworkEmulator(profile));
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

        _udp.Client.ReceiveTimeout = milliseconds;
    }

    public bool Send(ReadOnlySpan<byte> datagram)
    {
        ThrowIfDisposed();
        byte[] bytes = datagram.ToArray();
        EmulationDecision decision = _emulator?.Next() ?? new EmulationDecision(false, 0);

        if (decision.Drop)
        {
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
        return _udp.Receive(ref sender);
    }

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
