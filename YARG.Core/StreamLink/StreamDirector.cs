using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using YARG.Core.Chart;
using YARG.Core.Logging;

namespace YARG.Core.StreamLink
{
    public enum SongState
    {
        NotInSong,
        Playing,
        Paused,
    }

    /// <summary>
    /// What the director needs from the game. Every call happens on the main thread, inside
    /// <see cref="StreamDirector.Tick"/> or one of the song lifecycle methods.
    /// </summary>
    public interface IStreamGameHost
    {
        /// <summary>True when the (single) local player is on five-fret guitar or bass.</summary>
        bool IsSupportedInstrument { get; }

        bool IsPracticeOrReplay { get; }

        /// <summary>Song seconds between now and the last note.</summary>
        double SecondsOfNotesRemaining { get; }

        /// <summary>Sets the song speed and the engines' speed.</summary>
        void SetSpeed(double speed);

        /// <summary>
        /// Restores the upcoming notes from the original chart and applies the given effects to them.
        /// </summary>
        /// <returns>False if it wasn't safe to rebuild this frame; the director retries on the next tick.</returns>
        bool RebuildUpcoming(GuitarNoteType? noteType, int[]? fretMap);

        void SendAck(StreamAck ack);
    }

    /// <summary>
    /// Turns validated commands into active effects, keeps their timers, and drives the speed ramp and chart
    /// rebuilds through <see cref="IStreamGameHost"/>.
    /// </summary>
    public sealed class StreamDirector
    {
        public const int    MAX_COMMANDS_PER_TICK = 8;
        public const int    MAX_PAUSED_QUEUE      = 5;
        public const double SPEED_CHANGE_COOLDOWN = 3.0;
        public const double MIN_NOTES_REMAINING   = 5.0;

        private readonly IStreamGameHost _host;

        private readonly ConcurrentQueue<ParseResult> _incoming = new();
        private readonly Queue<StreamCommand> _pausedQueue = new();
        private readonly RecentIdSet _recentIds = new();
        private readonly SpeedRamp _speed = new();
        private readonly Random _seedRandom = new();

        private bool   _rebuildPending;
        private double _clock;
        private double _lastSpeedChange = double.NegativeInfinity;

        public EffectSet Effects { get; } = new();
        public SongState State   { get; private set; } = SongState.NotInSong;

        public double CurrentSpeed => _speed.Current;

        public double DefaultDurationSeconds { get; set; } = 20;

        /// <summary>Raised on the main thread whenever an effect is applied (for the on-screen banner).</summary>
        public event Action<ActiveEffect>? EffectApplied;

        public StreamDirector(IStreamGameHost host)
        {
            _host = host;
        }

        /// <summary>
        /// Queues a parsed message. Safe to call from any thread.
        /// </summary>
        public void Enqueue(ParseResult message)
        {
            if (message.Kind != ParseKind.Ignored)
            {
                _incoming.Enqueue(message);
            }
        }

        public void OnSongStart()
        {
            Effects.Clear();
            _speed.Reset();
            _pausedQueue.Clear();
            _rebuildPending = false;
            _lastSpeedChange = double.NegativeInfinity;
            State = SongState.Playing;
        }

        /// <summary>
        /// Song finished or was quit: drop every effect, snap the speed back to 1.0, and reject anything
        /// that was waiting for the pause to end.
        /// </summary>
        public void OnSongEnd()
        {
            if (State == SongState.NotInSong)
            {
                return;
            }

            State = SongState.NotInSong;
            Effects.Clear();
            _rebuildPending = false;

            if (_speed.Current != 1.0)
            {
                _speed.Reset();
                _host.SetSpeed(1.0);
            }

            while (_pausedQueue.Count > 0)
            {
                _host.SendAck(StreamAck.Rejected(_pausedQueue.Dequeue(), RejectReason.NOT_IN_SONG));
            }
        }

        public void SetPaused(bool paused)
        {
            if (State == SongState.NotInSong)
            {
                return;
            }

            State = paused ? SongState.Paused : SongState.Playing;
        }

        /// <param name="realDeltaSeconds">Unscaled (wall-clock) time since the last tick.</param>
        public void Tick(double realDeltaSeconds)
        {
            _clock += realDeltaSeconds;

            if (State == SongState.Playing)
            {
                while (_pausedQueue.Count > 0)
                {
                    Execute(_pausedQueue.Dequeue());
                }
            }

            for (int i = 0; i < MAX_COMMANDS_PER_TICK && _incoming.TryDequeue(out var message); i++)
            {
                Receive(message);
            }

            if (State != SongState.Playing)
            {
                return;
            }

            foreach (var expired in Effects.Advance(realDeltaSeconds))
            {
                OnEffectRemoved(expired);
                _host.SendAck(StreamAck.Expired(expired));
            }

            if (_speed.Advance(realDeltaSeconds))
            {
                _host.SetSpeed(_speed.Current);
            }

            if (_rebuildPending)
            {
                _rebuildPending = !_host.RebuildUpcoming(Effects.NoteType?.NoteType, Effects.Lanes?.FretMap);
            }
        }

        private void Receive(ParseResult message)
        {
            var command = message.Command;
            if (message.Kind == ParseKind.Invalid || command == null)
            {
                YargLogger.LogWarning($"[StreamLink] Rejecting invalid command {command?.Id}: {message.Error}");
                _host.SendAck(StreamAck.Rejected(command, RejectReason.INVALID_PARAMS));
                return;
            }

            if (command.Type == CommandType.Ping)
            {
                _host.SendAck(StreamAck.Applied(command));
                return;
            }

            if (!_recentIds.Add(command.Id))
            {
                _host.SendAck(StreamAck.Rejected(command, RejectReason.DUPLICATE));
                return;
            }

            switch (State)
            {
                case SongState.NotInSong:
                    _host.SendAck(StreamAck.Rejected(command, RejectReason.NOT_IN_SONG));
                    break;
                case SongState.Paused:
                    if (_pausedQueue.Count < MAX_PAUSED_QUEUE)
                    {
                        _pausedQueue.Enqueue(command);
                    }
                    else
                    {
                        _host.SendAck(StreamAck.Rejected(command, RejectReason.RATE_LIMITED));
                    }
                    break;
                case SongState.Playing:
                    Execute(command);
                    break;
            }
        }

        private void Execute(StreamCommand command)
        {
            try
            {
                var rejection = Validate(command);
                if (rejection != null)
                {
                    _host.SendAck(StreamAck.Rejected(command, rejection));
                    return;
                }

                if (command.Type == CommandType.Clear)
                {
                    foreach (var effect in Effects.Clear())
                    {
                        OnEffectRemoved(effect);
                    }

                    _host.SendAck(StreamAck.Applied(command));
                    return;
                }

                double duration = command.DurationMs.HasValue
                    ? command.DurationMs.Value / 1000.0
                    : DefaultDurationSeconds;
                int seed = command.Seed ?? _seedRandom.Next();

                var applied = new ActiveEffect(command.Modifier, duration, command.Id, command.Source,
                    command.SpeedValue, seed);
                Effects.Apply(applied);

                if (applied.Group == ConflictGroup.Speed)
                {
                    _speed.SetTarget(applied.SpeedValue);
                    _lastSpeedChange = _clock;
                }
                else
                {
                    _rebuildPending = true;
                }

                _host.SendAck(StreamAck.Applied(command, (int) Math.Round(duration * 1000)));
                EffectApplied?.Invoke(applied);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "[StreamLink] Failed to execute command " + command.Id);
                _host.SendAck(StreamAck.Rejected(command, RejectReason.INTERNAL_ERROR));
            }
        }

        private string? Validate(StreamCommand command)
        {
            if (_host.IsPracticeOrReplay)
            {
                return RejectReason.NOT_IN_SONG;
            }

            if (!_host.IsSupportedInstrument)
            {
                return RejectReason.UNSUPPORTED_INSTRUMENT;
            }

            if (command.Type != CommandType.Apply)
            {
                return null;
            }

            if (_host.SecondsOfNotesRemaining < MIN_NOTES_REMAINING)
            {
                return RejectReason.SONG_ENDING;
            }

            if (command.Modifier == StreamModifier.Speed && _clock - _lastSpeedChange < SPEED_CHANGE_COOLDOWN)
            {
                return RejectReason.RATE_LIMITED;
            }

            return null;
        }

        private void OnEffectRemoved(ActiveEffect effect)
        {
            if (effect.Group == ConflictGroup.Speed)
            {
                _speed.SetTarget(1.0);
            }
            else
            {
                _rebuildPending = true;
            }
        }
    }
}
