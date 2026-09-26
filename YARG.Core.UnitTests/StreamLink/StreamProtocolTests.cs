using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YARG.Core.StreamLink;

namespace YARG.Core.UnitTests.StreamLink;

public class StreamProtocolTests
{
    private const string SPEED_COMMAND =
        """{"v":1,"id":"abc","cmd":"apply","mod":"speed","params":{"value":1.25},"durationMs":20000,"source":{"platform":"twitch","user":"SomeViewer","trigger":"redeem","amount":0,"rewardId":"r1","redemptionId":"x1"}}""";

    [Test]
    public void ParsesRawCommand()
    {
        var result = StreamMessageParser.Parse(SPEED_COMMAND);

        Assert.That(result.Kind, Is.EqualTo(ParseKind.Command));
        var cmd = result.Command!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cmd.Id, Is.EqualTo("abc"));
            Assert.That(cmd.Type, Is.EqualTo(CommandType.Apply));
            Assert.That(cmd.Modifier, Is.EqualTo(StreamModifier.Speed));
            Assert.That(cmd.SpeedValue, Is.EqualTo(1.25));
            Assert.That(cmd.DurationMs, Is.EqualTo(20000));
            Assert.That(cmd.Source.Platform, Is.EqualTo("twitch"));
            Assert.That(cmd.Source.User, Is.EqualTo("SomeViewer"));
            Assert.That(cmd.Source.RewardId, Is.EqualTo("r1"));
            Assert.That(cmd.Source.RedemptionId, Is.EqualTo("x1"));
        }
    }

    [Test]
    public void ParsesWrappedCommand_ObjectData()
    {
        string json = $$"""{"event":{"source":"General","type":"Custom"},"data":{{SPEED_COMMAND}}}""";
        var result = StreamMessageParser.Parse(json);

        Assert.That(result.Kind, Is.EqualTo(ParseKind.Command));
        Assert.That(result.Command!.Id, Is.EqualTo("abc"));
    }

    [Test]
    public void ParsesWrappedCommand_StringData()
    {
        var wrapper = new JObject
        {
            ["event"] = new JObject { ["source"] = "General", ["type"] = "Custom" },
            ["data"] = SPEED_COMMAND,
        };
        var result = StreamMessageParser.Parse(wrapper.ToString());

        Assert.That(result.Kind, Is.EqualTo(ParseKind.Command));
        Assert.That(result.Command!.SpeedValue, Is.EqualTo(1.25));
    }

    [TestCase("not json")]
    [TestCase("[1,2]")]
    [TestCase("""{"id":"sl-sub","status":"ok"}""")]
    [TestCase("""{"request":"Hello","info":{}}""")]
    [TestCase("""{"event":{"source":"Twitch","type":"ChatMessage"},"data":{"v":1,"cmd":"apply"}}""")]
    [TestCase("""{"event":{"source":"General","type":"Custom"},"data":{"something":"else"}}""")]
    [TestCase("""{"event":{"source":"General","type":"Custom"},"data":"not json"}""")]
    [TestCase("""{"cmd":"apply","id":"x"}""")]
    public void IgnoresUnrelatedMessages(string json)
    {
        Assert.That(StreamMessageParser.Parse(json).Kind, Is.EqualTo(ParseKind.Ignored));
    }

    [TestCase("""{"v":2,"id":"a","cmd":"ping"}""")]
    [TestCase("""{"v":1,"cmd":"ping"}""")]
    [TestCase("""{"v":1,"id":"a","cmd":"explode"}""")]
    [TestCase("""{"v":1,"id":"a","cmd":"apply","mod":"doubleNotes"}""")]
    [TestCase("""{"v":1,"id":"a","cmd":"apply","mod":"speed"}""")]
    [TestCase("""{"v":1,"id":"a","cmd":"apply","mod":"speed","params":{"value":"fast"}}""")]
    [TestCase("""{"v":1,"id":"a","cmd":"apply","mod":"mirror","durationMs":"long"}""")]
    [TestCase("""{"v":1,"id":"a","cmd":"apply","mod":"shuffle","params":{"seed":"abc"}}""")]
    public void RejectsMalformedCommands(string json)
    {
        Assert.That(StreamMessageParser.Parse(json).Kind, Is.EqualTo(ParseKind.Invalid));
    }

    [Test]
    public void InvalidCommand_KeepsIdAndSourceForTheAck()
    {
        var result = StreamMessageParser.Parse(
            """{"v":1,"id":"a","cmd":"apply","mod":"nope","source":{"user":"u","redemptionId":"r"}}""");

        Assert.That(result.Kind, Is.EqualTo(ParseKind.Invalid));
        Assert.That(result.Command!.Id, Is.EqualTo("a"));
        Assert.That(result.Command.ModName, Is.EqualTo("nope"));
        Assert.That(result.Command.Source.RedemptionId, Is.EqualTo("r"));
    }

    [TestCase(0.1, 0.60)]
    [TestCase(9.0, 1.50)]
    [TestCase(0.85, 0.85)]
    public void ClampsSpeed(double requested, double expected)
    {
        var result = StreamMessageParser.Parse(
            $$$"""{"v":1,"id":"a","cmd":"apply","mod":"speed","params":{"value":{{{requested}}}}}""");

        Assert.That(result.Command!.SpeedValue, Is.EqualTo(expected));
    }

    [TestCase(1000, 5000)]
    [TestCase(999999, 120000)]
    [TestCase(30000, 30000)]
    public void ClampsDuration(int requested, int expected)
    {
        var result = StreamMessageParser.Parse(
            $$"""{"v":1,"id":"a","cmd":"apply","mod":"mirror","durationMs":{{requested}}}""");

        Assert.That(result.Command!.DurationMs, Is.EqualTo(expected));
    }

    [Test]
    public void MissingDurationAndSeed_AreLeftForTheDirector()
    {
        var result = StreamMessageParser.Parse("""{"v":1,"id":"a","cmd":"apply","mod":"shuffle"}""");

        Assert.That(result.Kind, Is.EqualTo(ParseKind.Command));
        Assert.That(result.Command!.DurationMs, Is.Null);
        Assert.That(result.Command.Seed, Is.Null);
    }

    [Test]
    public void ParsesShuffleSeed()
    {
        var result = StreamMessageParser.Parse(
            """{"v":1,"id":"a","cmd":"apply","mod":"shuffle","params":{"seed":42}}""");

        Assert.That(result.Command!.Seed, Is.EqualTo(42));
    }

    [Test]
    public void ParsesClearAndPing()
    {
        Assert.That(StreamMessageParser.Parse("""{"v":1,"id":"a","cmd":"clear"}""").Command!.Type,
            Is.EqualTo(CommandType.Clear));
        Assert.That(StreamMessageParser.Parse("""{"v":1,"id":"b","cmd":"ping"}""").Command!.Type,
            Is.EqualTo(CommandType.Ping));
    }

    [Test]
    public void Authentication_MatchesReferenceImplementation()
    {
        // Reference value computed with Python's hashlib/base64 using the documented algorithm
        Assert.That(StreamerBotProtocol.ComputeAuthentication("hunter2", "c2FsdA==", "Y2hhbGxlbmdl"),
            Is.EqualTo("HbemcTRAK8GnBZpkRzKZdmk94xa5VYtjm6/uKbA1epI="));
    }

    [Test]
    public void ParsesHello()
    {
        Assert.That(StreamerBotProtocol.TryParseHello(
            """{"request":"Hello","info":{"name":"Streamer.bot"},"authentication":{"challenge":"c","salt":"s"}}""",
            out var hello), Is.True);
        Assert.That(hello.RequiresAuth, Is.True);
        Assert.That(hello.Salt, Is.EqualTo("s"));
        Assert.That(hello.Challenge, Is.EqualTo("c"));

        Assert.That(StreamerBotProtocol.TryParseHello("""{"request":"Hello","info":{}}""", out hello), Is.True);
        Assert.That(hello.RequiresAuth, Is.False);

        Assert.That(StreamerBotProtocol.TryParseHello(SPEED_COMMAND, out _), Is.False);
    }

    [Test]
    public void ParsesResponses()
    {
        Assert.That(StreamerBotProtocol.TryParseResponse("""{"id":"sl-auth","status":"ok"}""", out var ok), Is.True);
        Assert.That(ok.Id, Is.EqualTo("sl-auth"));
        Assert.That(ok.Ok, Is.True);

        Assert.That(StreamerBotProtocol.TryParseResponse(
            """{"id":"sl-auth","status":"error","error":"Authentication failed"}""", out var err), Is.True);
        Assert.That(err.Ok, Is.False);
        Assert.That(err.Error, Is.EqualTo("Authentication failed"));

        Assert.That(StreamerBotProtocol.TryParseResponse(SPEED_COMMAND, out _), Is.False);
    }

    [Test]
    public void SubscribeRequest_MatchesSpec()
    {
        var obj = JObject.Parse(StreamerBotProtocol.SubscribeRequest());
        Assert.That(JToken.DeepEquals(obj,
            JObject.Parse("""{"request":"Subscribe","id":"sl-sub","events":{"General":["Custom"]}}""")), Is.True);
    }

    [Test]
    public void AckRequest_HasAllFields()
    {
        var command = StreamMessageParser.Parse(SPEED_COMMAND).Command!;
        var obj = JObject.Parse(StreamerBotProtocol.AckRequest(StreamAck.Applied(command, 20000), 7));

        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?) obj["request"], Is.EqualTo("DoAction"));
            Assert.That((string?) obj["id"], Is.EqualTo("sl-ack-7"));
            Assert.That((string?) obj["action"]!["name"], Is.EqualTo("StreamLink Ack"));
            var args = obj["args"]!;
            Assert.That((string?) args["cmdId"], Is.EqualTo("abc"));
            Assert.That((string?) args["status"], Is.EqualTo("applied"));
            Assert.That((string?) args["reason"], Is.EqualTo(""));
            Assert.That((string?) args["mod"], Is.EqualTo("speed"));
            Assert.That((string?) args["user"], Is.EqualTo("SomeViewer"));
            Assert.That((string?) args["redemptionId"], Is.EqualTo("x1"));
            Assert.That((string?) args["platform"], Is.EqualTo("twitch"));
            Assert.That((int) args["endsInMs"]!, Is.EqualTo(20000));
        }
    }

    [Test]
    public void RecentIdSet_ForgetsOldestBeyondCapacity()
    {
        var set = new RecentIdSet(3);
        Assert.That(set.Add("a"), Is.True);
        Assert.That(set.Add("a"), Is.False);
        set.Add("b");
        set.Add("c");
        set.Add("d");
        Assert.That(set.Add("a"), Is.True, "a should have been evicted");
        Assert.That(set.Add("d"), Is.False);
    }
}
