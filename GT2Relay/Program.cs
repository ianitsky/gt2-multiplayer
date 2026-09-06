using GT2Relay;

int port = 34720;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--port" or "-p")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine("--port wants a number between 1 and 65535");
            return 1;
        }
        i++;
    }
    else
    {
        Console.Error.WriteLine($"gt2relay [--port {port}]");
        return 1;
    }
}

using var server = new RelayServer(port);
using var stopping = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

Say.Line($"gt2relay listening on udp/{server.BoundPort}");
Say.Line("rooms are forgotten after "
    + $"{RoomRegistry.Forgotten.TotalSeconds:F0}s without a publish");

// A line only when something moved, so a quiet night stays quiet and a
// scrolling log means traffic. "in" rising while "out" stays flat is the
// registry refusing to answer; both rising while players still see no rooms
// is the path back, not this.
long lastIn = 0, lastOut = 0;

server.Run(stopping.Token, it =>
{
    if (it.Received == lastIn && it.Sent == lastOut) return;
    lastIn = it.Received;
    lastOut = it.Sent;

    Say.Line(
        $"in {it.Received}  out {it.Sent}  failed {it.SendFailures}"
        + $"  rooms {it.RoomCount}  last {it.LastHeardFrom}");
});

Say.Line("stopped");
Say.Finish();
return 0;
