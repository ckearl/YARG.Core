using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YARG.Core.StreamLink
{
    public sealed class StreamSource
    {
        public static readonly StreamSource Unknown = new StreamSource();

        public string Platform     { get; set; } = "";
        public string User         { get; set; } = "";
        public string Trigger      { get; set; } = "";
        public double Amount       { get; set; }
        public string RewardId     { get; set; } = "";
        public string RedemptionId { get; set; } = "";
    }

    public enum CommandType
    {
        Apply,
        Clear,
        Ping,
    }

    /// <summary>
    /// A command from Streamer.bot, already checked for shape and parameter ranges.
    /// </summary>
    public sealed class StreamCommand
    {
        public string       Id     { get; set; } = "";
        public CommandType  Type   { get; set; }
        public StreamSource Source { get; set; } = StreamSource.Unknown;

        // Only set for Apply
        public StreamModifier Modifier { get; set; }
        public double SpeedValue { get; set; } = 1.0;
        public int? Seed { get; set; }

        /// <summary>Requested duration, or null to use the configured default.</summary>
        public int? DurationMs { get; set; }

        /// <summary>The original "mod" string, echoed back in the ack even when it isn't recognized.</summary>
        public string ModName { get; set; } = "";
    }

    public enum ParseKind
    {
        /// <summary>Not a StreamLink command (another event, a request response, a Hello, etc.).</summary>
        Ignored,
        Command,
        /// <summary>A StreamLink command with a bad shape or parameters. Must be rejected with invalid_params.</summary>
        Invalid,
    }

    public readonly struct ParseResult
    {
        public readonly ParseKind Kind;

        /// <summary>
        /// Set for <see cref="ParseKind.Command"/>; for <see cref="ParseKind.Invalid"/> it holds whatever
        /// could be read (id, source), so the rejection can still be acked.
        /// </summary>
        public readonly StreamCommand? Command;

        public readonly string Error;

        public ParseResult(ParseKind kind, StreamCommand? command, string error)
        {
            Kind = kind;
            Command = command;
            Error = error;
        }

        public static ParseResult Ignored => new(ParseKind.Ignored, null, "");
    }

    public static class RejectReason
    {
        public const string NOT_IN_SONG            = "not_in_song";
        public const string UNSUPPORTED_INSTRUMENT = "unsupported_instrument";
        public const string INVALID_PARAMS         = "invalid_params";
        public const string RATE_LIMITED           = "rate_limited";
        public const string DUPLICATE              = "duplicate";
        public const string SONG_ENDING            = "song_ending";
        public const string INTERNAL_ERROR         = "internal_error";
    }

    public enum AckStatus
    {
        Applied,
        Rejected,
        /// <summary>Informational: an applied effect ran out.</summary>
        Expired,
    }

    public sealed class StreamAck
    {
        public string       CommandId { get; set; } = "";
        public AckStatus    Status    { get; set; }
        public string       Reason    { get; set; } = "";
        public string       Mod       { get; set; } = "";
        public StreamSource Source    { get; set; } = StreamSource.Unknown;
        public int          EndsInMs  { get; set; }

        public string StatusName => Status switch
        {
            AckStatus.Applied  => "applied",
            AckStatus.Rejected => "rejected",
            AckStatus.Expired  => "expired",
            _ => throw new ArgumentOutOfRangeException()
        };

        public static StreamAck Applied(StreamCommand command, int endsInMs = 0) => new()
        {
            CommandId = command.Id, Status = AckStatus.Applied, Mod = command.ModName, Source = command.Source,
            EndsInMs = endsInMs,
        };

        public static StreamAck Rejected(StreamCommand? command, string reason) => new()
        {
            CommandId = command?.Id ?? "", Status = AckStatus.Rejected, Reason = reason,
            Mod = command?.ModName ?? "", Source = command?.Source ?? StreamSource.Unknown,
        };

        public static StreamAck Expired(ActiveEffect effect) => new()
        {
            CommandId = effect.CommandId, Status = AckStatus.Expired, Mod = effect.Modifier.ToProtocolName(),
            Source = effect.Source,
        };
    }

    public static class StreamMessageParser
    {
        public const int PROTOCOL_VERSION = 1;

        public const int MIN_DURATION_MS = 5_000;
        public const int MAX_DURATION_MS = 120_000;

        /// <summary>
        /// Parses a text frame from the Streamer.bot socket. Accepts both a raw command object and one wrapped
        /// in a General.Custom event (whose "data" may itself be a JSON string).
        /// </summary>
        public static ParseResult Parse(string json)
        {
            JObject? obj;
            try
            {
                obj = JToken.Parse(json) as JObject;
            }
            catch (JsonException)
            {
                return ParseResult.Ignored;
            }

            if (obj == null)
            {
                return ParseResult.Ignored;
            }

            if (obj["event"] is JObject evt)
            {
                if ((string?) evt["source"] != "General" || (string?) evt["type"] != "Custom")
                {
                    return ParseResult.Ignored;
                }

                var data = obj["data"];
                if (data is { Type: JTokenType.String })
                {
                    try
                    {
                        data = JToken.Parse((string) data!);
                    }
                    catch (JsonException)
                    {
                        return ParseResult.Ignored;
                    }
                }

                obj = data as JObject;
                if (obj == null)
                {
                    return ParseResult.Ignored;
                }
            }

            if (obj["cmd"] == null || obj["v"] == null)
            {
                return ParseResult.Ignored;
            }

            return ParseCommand(obj);
        }

        private static ParseResult ParseCommand(JObject obj)
        {
            var command = new StreamCommand
            {
                Id = obj["id"] is { Type: JTokenType.String } idToken ? (string) idToken! : "",
                Source = ParseSource(obj["source"] as JObject),
                ModName = obj["mod"] is { Type: JTokenType.String } modToken ? (string) modToken! : "",
            };

            if (obj["v"] is not { Type: JTokenType.Integer } version || (int) version != PROTOCOL_VERSION)
            {
                return Invalid(command, "unsupported protocol version");
            }

            if (string.IsNullOrEmpty(command.Id))
            {
                return Invalid(command, "missing id");
            }

            switch ((string?) obj["cmd"])
            {
                case "ping":
                    command.Type = CommandType.Ping;
                    return Valid(command);
                case "clear":
                    command.Type = CommandType.Clear;
                    return Valid(command);
                case "apply":
                    command.Type = CommandType.Apply;
                    break;
                default:
                    return Invalid(command, "unknown cmd");
            }

            if (!StreamModifiers.TryParse(command.ModName, out var modifier))
            {
                return Invalid(command, "unknown mod");
            }

            command.Modifier = modifier;

            var durationToken = obj["durationMs"];
            if (durationToken != null && durationToken.Type != JTokenType.Null)
            {
                if (!TryGetNumber(durationToken, out double duration))
                {
                    return Invalid(command, "durationMs is not a number");
                }

                command.DurationMs = (int) Math.Round(Math.Clamp(duration, MIN_DURATION_MS, MAX_DURATION_MS));
            }

            var parameters = obj["params"] as JObject;
            switch (modifier)
            {
                case StreamModifier.Speed:
                {
                    if (parameters == null || !TryGetNumber(parameters["value"], out double value))
                    {
                        return Invalid(command, "speed requires a numeric params.value");
                    }

                    command.SpeedValue = Math.Clamp(value, StreamModifiers.MIN_SPEED, StreamModifiers.MAX_SPEED);
                    break;
                }
                case StreamModifier.Shuffle:
                {
                    var seedToken = parameters?["seed"];
                    if (seedToken != null && seedToken.Type != JTokenType.Null)
                    {
                        if (!TryGetNumber(seedToken, out double seed))
                        {
                            return Invalid(command, "shuffle seed is not a number");
                        }

                        // Accept any integer-ish value, folding it into int range
                        command.Seed = unchecked((int) (long) seed);
                    }

                    break;
                }
            }

            return Valid(command);
        }

        private static StreamSource ParseSource(JObject? source)
        {
            if (source == null)
            {
                return StreamSource.Unknown;
            }

            return new StreamSource
            {
                Platform = GetString(source, "platform"),
                User = GetString(source, "user"),
                Trigger = GetString(source, "trigger"),
                Amount = TryGetNumber(source["amount"], out double amount) ? amount : 0,
                RewardId = GetString(source, "rewardId"),
                RedemptionId = GetString(source, "redemptionId"),
            };
        }

        private static string GetString(JObject obj, string key)
        {
            var token = obj[key];
            return token == null || token.Type == JTokenType.Null ? "" : token.ToString();
        }

        private static bool TryGetNumber(JToken? token, out double value)
        {
            value = 0;
            if (token is not { Type: JTokenType.Integer or JTokenType.Float })
            {
                return false;
            }

            value = (double) token;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static ParseResult Valid(StreamCommand command) => new(ParseKind.Command, command, "");

        private static ParseResult Invalid(StreamCommand command, string error) =>
            new(ParseKind.Invalid, command, error);
    }

    /// <summary>
    /// Requests and responses for Streamer.bot's WebSocket server.
    /// </summary>
    public static class StreamerBotProtocol
    {
        public const string SUBSCRIBE_ID    = "sl-sub";
        public const string AUTHENTICATE_ID = "sl-auth";
        public const string ACK_ACTION_NAME = "StreamLink Ack";

        public readonly struct Hello
        {
            public readonly bool   RequiresAuth;
            public readonly string Salt;
            public readonly string Challenge;

            public Hello(bool requiresAuth, string salt, string challenge)
            {
                RequiresAuth = requiresAuth;
                Salt = salt;
                Challenge = challenge;
            }
        }

        public readonly struct Response
        {
            public readonly string  Id;
            public readonly bool    Ok;
            public readonly string  Error;

            public Response(string id, bool ok, string error)
            {
                Id = id;
                Ok = ok;
                Error = error;
            }
        }

        public static bool TryParseHello(string json, out Hello hello)
        {
            hello = default;
            if (!TryParseObject(json, out var obj) || (string?) obj["request"] != "Hello")
            {
                return false;
            }

            if (obj["authentication"] is JObject auth)
            {
                hello = new Hello(true, (string?) auth["salt"] ?? "", (string?) auth["challenge"] ?? "");
            }
            else
            {
                hello = new Hello(false, "", "");
            }

            return true;
        }

        /// <summary>
        /// Parses the reply to one of our requests (anything with an "id" and a "status").
        /// </summary>
        public static bool TryParseResponse(string json, out Response response)
        {
            response = default;
            if (!TryParseObject(json, out var obj) || obj["id"] == null || obj["status"] == null ||
                obj["request"] != null || obj["event"] != null)
            {
                return false;
            }

            string status = (string?) obj["status"] ?? "";
            string error = (string?) obj["error"] ?? "";
            response = new Response((string?) obj["id"] ?? "", status == "ok", error);
            return true;
        }

        /// <summary>
        /// secret = base64(sha256(password + salt)); auth = base64(sha256(secret + challenge))
        /// </summary>
        public static string ComputeAuthentication(string password, string salt, string challenge)
        {
            using var sha = SHA256.Create();
            string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
        }

        public static string AuthenticateRequest(string authentication)
        {
            return new JObject
            {
                ["request"] = "Authenticate",
                ["id"] = AUTHENTICATE_ID,
                ["authentication"] = authentication,
            }.ToString(Formatting.None);
        }

        public static string SubscribeRequest()
        {
            return new JObject
            {
                ["request"] = "Subscribe",
                ["id"] = SUBSCRIBE_ID,
                ["events"] = new JObject { ["General"] = new JArray("Custom") },
            }.ToString(Formatting.None);
        }

        public static string AckRequest(StreamAck ack, int sequence)
        {
            return new JObject
            {
                ["request"] = "DoAction",
                ["id"] = $"sl-ack-{sequence}",
                ["action"] = new JObject { ["name"] = ACK_ACTION_NAME },
                ["args"] = new JObject
                {
                    ["cmdId"] = ack.CommandId,
                    ["status"] = ack.StatusName,
                    ["reason"] = ack.Reason,
                    ["mod"] = ack.Mod,
                    ["user"] = ack.Source.User,
                    ["redemptionId"] = ack.Source.RedemptionId,
                    ["rewardId"] = ack.Source.RewardId,
                    ["platform"] = ack.Source.Platform,
                    ["endsInMs"] = ack.EndsInMs,
                },
            }.ToString(Formatting.None);
        }

        private static bool TryParseObject(string json, out JObject obj)
        {
            obj = null!;
            try
            {
                if (JToken.Parse(json) is JObject parsed)
                {
                    obj = parsed;
                    return true;
                }
            }
            catch (JsonException)
            {
            }

            return false;
        }
    }

    /// <summary>
    /// Remembers the most recent command ids so a double-fired trigger is only applied once.
    /// </summary>
    public sealed class RecentIdSet
    {
        private readonly int _capacity;
        private readonly HashSet<string> _set = new();
        private readonly Queue<string> _order = new();

        public RecentIdSet(int capacity = 256)
        {
            _capacity = capacity;
        }

        /// <returns>False if <paramref name="id"/> was already seen.</returns>
        public bool Add(string id)
        {
            if (!_set.Add(id))
            {
                return false;
            }

            _order.Enqueue(id);
            if (_order.Count > _capacity)
            {
                _set.Remove(_order.Dequeue());
            }

            return true;
        }

        public void Clear()
        {
            _set.Clear();
            _order.Clear();
        }
    }
}
