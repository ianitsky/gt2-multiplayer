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

Console.WriteLine($"gt2relay listening on udp/{server.BoundPort}");
Console.WriteLine("rooms are forgotten after "
    + $"{RoomRegistry.Forgotten.TotalSeconds:F0}s without a publish");

server.Run(stopping.Token);

Console.WriteLine("stopped");
return 0;
