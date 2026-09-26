using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.StreamLink;

namespace YARG.Core.UnitTests.StreamLink;

public class StreamDirectorTests
{
    private sealed class FakeHost : IStreamGameHost
    {
        public bool IsSupportedInstrument { get; set; } = true;
        public bool IsPracticeOrReplay { get; set; }
        public double SecondsOfNotesRemaining { get; set; } = 120;

        public readonly List<double> Speeds = new();
        public readonly List<(GuitarNoteType? Type, int[]? Map)> Rebuilds = new();
        public readonly List<StreamAck> Acks = new();
        public bool RebuildSucceeds = true;

        public void SetSpeed(double speed) => Speeds.Add(speed);

        public bool RebuildUpcoming(GuitarNoteType? noteType, int[]? fretMap)
        {
            if (!RebuildSucceeds) return false;
            Rebuilds.Add((noteType, fretMap));
            return true;
        }

        public void SendAck(StreamAck ack) => Acks.Add(ack);

        public StreamAck LastAck => Acks[^1];
    }

    private FakeHost _host = null!;
    private StreamDirector _director = null!;
    private int _nextId;

    [SetUp]
    public void SetUp()
    {
        _host = new FakeHost();
        _director = new StreamDirector(_host);
        _nextId = 0;
    }

    private string Send(string mod, string extra = "", string? id = null)
    {
        id ??= $"id{_nextId++}";
        _director.Enqueue(StreamMessageParser.Parse(
            $$$"""{"v":1,"id":"{{{id}}}","cmd":"apply","mod":"{{{mod}}}"{{{extra}}},"source":{"user":"u"}}"""));
        return id;
    }

    private void SendRaw(string json) => _director.Enqueue(StreamMessageParser.Parse(json));

    private void Tick(double seconds = 0.016) => _director.Tick(seconds);

    private void Run(double seconds, double step = 0.01)
    {
        for (double t = 0; t < seconds - 1e-9; t += step)
        {
            _director.Tick(step);
        }
    }

    [Test]
    public void RejectsWhenNotInSong()
    {
        Send("mirror");
        Tick();
        Assert.That(_host.LastAck.Status, Is.EqualTo(AckStatus.Rejected));
        Assert.That(_host.LastAck.Reason, Is.EqualTo(RejectReason.NOT_IN_SONG));
    }

    [Test]
    public void PingIsAlwaysAcked()
    {
        SendRaw("""{"v":1,"id":"p","cmd":"ping"}""");
        SendRaw("""{"v":1,"id":"p","cmd":"ping"}""");
        Tick();
        Assert.That(_host.Acks.Select(a => a.Status), Is.EqualTo(new[] { AckStatus.Applied, AckStatus.Applied }));
    }

    [Test]
    public void AppliesNoteEffectAndRebuilds()
    {
        _director.OnSongStart();
        string id = Send("allTaps", ""","durationMs":10000""");
        Tick();

        Assert.That(_host.LastAck.CommandId, Is.EqualTo(id));
        Assert.That(_host.LastAck.Status, Is.EqualTo(AckStatus.Applied));
        Assert.That(_host.LastAck.EndsInMs, Is.EqualTo(10000));
        Assert.That(_host.LastAck.Mod, Is.EqualTo("allTaps"));
        Assert.That(_host.Rebuilds, Has.Count.EqualTo(1));
        Assert.That(_host.Rebuilds[0].Type, Is.EqualTo(GuitarNoteType.Tap));
        Assert.That(_host.Rebuilds[0].Map, Is.Null);
    }

    [Test]
    public void UsesDefaultDuration()
    {
        _director.DefaultDurationSeconds = 15;
        _director.OnSongStart();
        Send("mirror");
        Tick();
        Assert.That(_host.LastAck.EndsInMs, Is.EqualTo(15000));
    }

    [Test]
    public void RejectsInvalidAndDuplicate()
    {
        _director.OnSongStart();
        SendRaw("""{"v":1,"id":"bad","cmd":"apply","mod":"nope"}""");
        Send("mirror", id: "same");
        Send("mirror", id: "same");
        Tick();

        Assert.That(_host.Acks.Select(a => (a.CommandId, a.Status, a.Reason)), Is.EqualTo(new[]
        {
            ("bad", AckStatus.Rejected, RejectReason.INVALID_PARAMS),
            ("same", AckStatus.Applied, ""),
            ("same", AckStatus.Rejected, RejectReason.DUPLICATE),
        }));
    }

    [Test]
    public void RejectsUnsupportedInstrumentPracticeAndSongEnding()
    {
        _director.OnSongStart();

        _host.IsSupportedInstrument = false;
        Send("mirror");
        Tick();
        Assert.That(_host.LastAck.Reason, Is.EqualTo(RejectReason.UNSUPPORTED_INSTRUMENT));

        _host.IsSupportedInstrument = true;
        _host.IsPracticeOrReplay = true;
        Send("mirror");
        Tick();
        Assert.That(_host.LastAck.Reason, Is.EqualTo(RejectReason.NOT_IN_SONG));

        _host.IsPracticeOrReplay = false;
        _host.SecondsOfNotesRemaining = 4.9;
        Send("mirror");
        Tick();
        Assert.That(_host.LastAck.Reason, Is.EqualTo(RejectReason.SONG_ENDING));
    }

    [Test]
    public void ProcessesAtMostEightCommandsPerTick()
    {
        _director.OnSongStart();
        for (int i = 0; i < 10; i++) SendRaw($$"""{"v":1,"id":"p{{i}}","cmd":"ping"}""");

        Tick();
        Assert.That(_host.Acks, Has.Count.EqualTo(8));
        Tick();
        Assert.That(_host.Acks, Has.Count.EqualTo(10));
    }

    [Test]
    public void NewestEffectInGroupWins_DifferentGroupsCombine()
    {
        _director.OnSongStart();
        Send("allTaps");
        Send("mirror");
        Send("allHopos");
        Send("shuffle", ""","params":{"seed":3}""");
        Tick();

        Assert.That(_director.Effects.Count, Is.EqualTo(2));
        Assert.That(_director.Effects.NoteType!.Modifier, Is.EqualTo(StreamModifier.AllHopos));
        Assert.That(_director.Effects.Lanes!.Modifier, Is.EqualTo(StreamModifier.Shuffle));

        // One rebuild per tick, with the final state
        Assert.That(_host.Rebuilds, Has.Count.EqualTo(1));
        Assert.That(_host.Rebuilds[0].Type, Is.EqualTo(GuitarNoteType.Hopo));
        Assert.That(_host.Rebuilds[0].Map, Is.EqualTo(ChartMutator.ShuffleMap(3)));
    }

    [Test]
    public void ReplacingEffectRestartsTimer()
    {
        _director.OnSongStart();
        Send("mirror", ""","durationMs":10000""");
        Tick();
        Run(8);
        Send("shuffle", ""","durationMs":10000""");
        Tick();
        Run(8);
        Assert.That(_director.Effects.Lanes, Is.Not.Null, "replacement should have a fresh 10 s timer");
    }

    [Test]
    public void EffectsExpireInRealTime_AndPauseStopsTheClock()
    {
        _director.OnSongStart();
        Send("mirror", ""","durationMs":5000""");
        Tick(0);
        _host.Rebuilds.Clear();

        Run(3);
        _director.SetPaused(true);
        Run(10);
        Assert.That(_director.Effects.Lanes, Is.Not.Null, "paused time must not count");

        _director.SetPaused(false);
        Run(1.9);
        Assert.That(_director.Effects.Lanes, Is.Not.Null);
        Run(0.2);
        Assert.That(_director.Effects.Lanes, Is.Null);

        Assert.That(_host.LastAck.Status, Is.EqualTo(AckStatus.Expired));
        Assert.That(_host.Rebuilds, Has.Count.EqualTo(1));
        Assert.That(_host.Rebuilds[0], Is.EqualTo(((GuitarNoteType?) null, (int[]?) null)));
    }

    [Test]
    public void PausedCommandsAreQueuedUpToFive_ThenAppliedInOrderOnResume()
    {
        _director.OnSongStart();
        _director.SetPaused(true);
        var ids = Enumerable.Range(0, 6).Select(_ => Send("mirror")).ToList();
        Tick();

        Assert.That(_host.Acks, Has.Count.EqualTo(1), "only the overflow is answered while paused");
        Assert.That(_host.LastAck.CommandId, Is.EqualTo(ids[5]));
        Assert.That(_host.LastAck.Reason, Is.EqualTo(RejectReason.RATE_LIMITED));

        _director.SetPaused(false);
        Tick();
        Assert.That(_host.Acks.Skip(1).Select(a => a.CommandId), Is.EqualTo(ids.Take(5)));
        Assert.That(_host.Acks.Skip(1).All(a => a.Status == AckStatus.Applied), Is.True);
    }

    [Test]
    public void SpeedRampsInStepsAndBack()
    {
        _director.OnSongStart();
        Send("speed", ""","params":{"value":1.25},"durationMs":5000""");
        Run(0.05);
        Assert.That(_host.Speeds, Is.EqualTo(new[] { 1.05 }));

        Run(0.2);
        Assert.That(_host.Speeds, Is.EqualTo(new[] { 1.05, 1.10, 1.15, 1.20, 1.25 }));

        _host.Speeds.Clear();
        Run(5);
        Assert.That(_host.Speeds.Last(), Is.EqualTo(1.0));
        Assert.That(_host.Speeds, Is.Ordered.Descending);
        Assert.That(_director.CurrentSpeed, Is.EqualTo(1.0));
    }

    [Test]
    public void SpeedChangesAreRateLimited()
    {
        _director.OnSongStart();
        Send("speed", ""","params":{"value":1.25}""");
        Tick();
        Run(2.9);
        Send("speed", ""","params":{"value":0.8}""");
        Tick();
        Assert.That(_host.LastAck.Reason, Is.EqualTo(RejectReason.RATE_LIMITED));

        Run(0.2);
        Send("speed", ""","params":{"value":0.8}""");
        Tick();
        Assert.That(_host.LastAck.Status, Is.EqualTo(AckStatus.Applied));
    }

    [Test]
    public void ClearEndsEverything()
    {
        _director.OnSongStart();
        Send("speed", ""","params":{"value":1.5}""");
        Send("allTaps");
        Send("mirror");
        Run(1);
        SendRaw("""{"v":1,"id":"clr","cmd":"clear"}""");
        Run(1);

        Assert.That(_director.Effects.Count, Is.Zero);
        Assert.That(_director.CurrentSpeed, Is.EqualTo(1.0));
        Assert.That(_host.Rebuilds.Last(), Is.EqualTo(((GuitarNoteType?) null, (int[]?) null)));
        Assert.That(_host.Acks.Single(a => a.CommandId == "clr").Status, Is.EqualTo(AckStatus.Applied));
    }

    [Test]
    public void SongEnd_ResetsSpeedImmediatelyAndRejectsQueued()
    {
        _director.OnSongStart();
        Send("speed", ""","params":{"value":1.5}""");
        Run(1);
        _director.SetPaused(true);
        string queued = Send("mirror");
        Tick();
        _host.Speeds.Clear();

        _director.OnSongEnd();

        Assert.That(_host.Speeds, Is.EqualTo(new[] { 1.0 }));
        Assert.That(_director.Effects.Count, Is.Zero);
        Assert.That(_host.LastAck.CommandId, Is.EqualTo(queued));
        Assert.That(_host.LastAck.Reason, Is.EqualTo(RejectReason.NOT_IN_SONG));
    }

    [Test]
    public void FailedRebuildIsRetried()
    {
        _director.OnSongStart();
        _host.RebuildSucceeds = false;
        Send("mirror");
        Tick();
        Tick();
        Assert.That(_host.Rebuilds, Is.Empty);

        _host.RebuildSucceeds = true;
        Tick();
        Tick();
        Assert.That(_host.Rebuilds, Has.Count.EqualTo(1));
    }

    [Test]
    public void EffectAppliedEventFires()
    {
        var seen = new List<ActiveEffect>();
        _director.EffectApplied += seen.Add;
        _director.OnSongStart();
        Send("speed", ""","params":{"value":0.75}""");
        Tick();

        Assert.That(seen, Has.Count.EqualTo(1));
        Assert.That(seen[0].Describe(), Is.EqualTo("Speed 75%"));
        Assert.That(seen[0].Source.User, Is.EqualTo("u"));
    }
}
