using System.Diagnostics;
using YARG.Core.Chart;
using YARG.Core.StreamLink;

// Runs the real StreamLinkClient and StreamDirector against a fake "always in a song" game,
// so the protocol can be tested end to end (e.g. with tools/mock_sb.py) without Unity.
//
//   dotnet run --project tools/StreamLinkHarness -- [--port 8080] [--password mock-password] [--seconds 60]

int port = 8080;
string password = "mock-password";
double seconds = double.PositiveInfinity;
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--password": password = args[++i]; break;
        case "--seconds": seconds = double.Parse(args[++i]); break;
    }
}

var host = new HarnessHost();
var director = new StreamDirector(host);
using var client = new StreamLinkClient(new StreamLinkSettings { Port = port, Password = password }, director.Enqueue);
host.Client = client;
director.EffectApplied += e => Console.WriteLine($"  banner: @{e.Source.User} -> {e.Describe()} ({e.DurationSeconds:0}s)");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

client.Start();
director.OnSongStart();

var status = client.Status;
var clock = Stopwatch.StartNew();
double last = 0;
while (!cts.IsCancellationRequested && clock.Elapsed.TotalSeconds < seconds)
{
    double now = clock.Elapsed.TotalSeconds;
    director.Tick(now - last);
    last = now;

    if (client.Status != status)
    {
        status = client.Status;
        Console.WriteLine($"link: {status}");
    }

    Thread.Sleep(16);
}

director.OnSongEnd();
Thread.Sleep(200);
client.Stop();

sealed class HarnessHost : IStreamGameHost
{
    public StreamLinkClient? Client;

    public bool IsSupportedInstrument => true;
    public bool IsPracticeOrReplay => false;
    public double SecondsOfNotesRemaining => 600;

    private double _speed = 1.0;

    public void SetSpeed(double speed)
    {
        // Only log the ends of a ramp
        if (speed == 1.0 || Math.Abs(speed - _speed) > 0.051 || speed % 0.25 == 0)
        {
            Console.WriteLine($"  speed: {speed:0.00}");
        }

        _speed = speed;
    }

    public bool RebuildUpcoming(GuitarNoteType? noteType, int[]? fretMap)
    {
        string map = fretMap == null ? "none" : string.Join("", fretMap[1..6].Select(f => "GRYBO"[f - 1]));
        Console.WriteLine($"  rebuild: type={noteType?.ToString() ?? "original"} frets={map}");
        return true;
    }

    public void SendAck(StreamAck ack)
    {
        Console.WriteLine($"  ack: {ack.CommandId[..Math.Min(8, ack.CommandId.Length)]} {ack.Mod} {ack.StatusName} {ack.Reason}");
        Client?.SendAck(ack);
    }
}
