using System.Net;
using System.Net.Sockets;

namespace GT2Relay;

/// <summary>
/// The socket around <see cref="RoomRegistry"/>.
///
/// Deliberately thin. Everything worth arguing about - who may talk to whom,
/// what expires, what the caps are - was decided in the registry, where it can
/// be tested without a network. What is left here is reading a datagram,
/// asking, and sending the answers.
///
/// One thread and one socket. A relay for a handful of six-player rooms moves
/// a few thousand small datagrams a second at most, which one thread handles
/// without noticing; threads would buy nothing and cost a lock around every
/// room.
/// </summary>
public sealed class RelayServer : IDisposable
{
    readonly UdpClient _socket;
    readonly RoomRegistry _registry;
    bool _disposed;

    /// <summary>How often to forget what has gone quiet.</summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(5);

    public RelayServer(int port, Func<DateTime>? clock = null, Random? random = null)
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));

        // On Windows a UDP socket that sends to a closed port gets an ICMP
        // "port unreachable" back, and the default is to surface that on the
        // *next* receive as a ConnectionReset - so one departed player would
        // otherwise break reads for everybody. There is no way to answer it
        // usefully, so it is turned off.
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _socket.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
            // Not Windows. Nothing to turn off.
        }

        BoundPort = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _registry = new RoomRegistry(clock ?? (() => DateTime.UtcNow), random ?? new Random());
    }

    /// <summary>The port it actually got, which is not the one asked for when 0 was.</summary>
    public int BoundPort { get; }

    public int RoomCount => _registry.RoomCount;

    /// <summary>
    /// Answers everything already waiting, and returns how many datagrams that
    /// was. Never blocks: a test drives it a datagram at a time, and the loop
    /// in <see cref="Run"/> calls it when the socket says there is something.
    /// </summary>
    public int Pump()
    {
        if (_disposed) return 0;

        int handled = 0;
        while (_socket.Available > 0)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _socket.Receive(ref from);
            }
            catch (SocketException)
            {
                // One unhappy datagram is not a reason to stop serving
                // everybody else.
                break;
            }

            handled++;
            if (from is null) continue;

            foreach (var reply in _registry.Heard(from, data))
            {
                try
                {
                    _socket.Send(reply.Data, reply.Data.Length, reply.To);
                }
                catch (SocketException)
                {
                    // The peer went away between asking and being answered.
                    // It will expire on its own.
                }
            }
        }
        return handled;
    }

    public void Sweep() => _registry.Sweep();

    /// <summary>
    /// Serves until asked to stop. Sleeps a millisecond when there is nothing
    /// waiting rather than blocking on a receive, so the sweep still happens
    /// on a quiet night and stopping does not wait out a timeout.
    /// </summary>
    public void Run(CancellationToken stopping)
    {
        var nextSweep = DateTime.UtcNow + SweepEvery;

        while (!stopping.IsCancellationRequested)
        {
            if (_socket.Available > 0) Pump();
            else Thread.Sleep(1);

            if (DateTime.UtcNow >= nextSweep)
            {
                Sweep();
                nextSweep = DateTime.UtcNow + SweepEvery;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }
}
