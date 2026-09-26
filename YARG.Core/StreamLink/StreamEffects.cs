using System;
using System.Collections.Generic;
using YARG.Core.Chart;

namespace YARG.Core.StreamLink
{
    public enum StreamModifier
    {
        Speed,
        AllTaps,
        AllHopos,
        AllStrums,
        Shuffle,
        Mirror,
    }

    /// <summary>
    /// Only one effect per group can be active at a time. Effects in different groups combine.
    /// </summary>
    public enum ConflictGroup
    {
        Speed,
        NoteType,
        Lanes,
    }

    public static class StreamModifiers
    {
        public const double MIN_SPEED = 0.60;
        public const double MAX_SPEED = 1.50;

        public static bool TryParse(string? name, out StreamModifier modifier)
        {
            switch (name)
            {
                case "speed":     modifier = StreamModifier.Speed;     return true;
                case "allTaps":   modifier = StreamModifier.AllTaps;   return true;
                case "allHopos":  modifier = StreamModifier.AllHopos;  return true;
                case "allStrums": modifier = StreamModifier.AllStrums; return true;
                case "shuffle":   modifier = StreamModifier.Shuffle;   return true;
                case "mirror":    modifier = StreamModifier.Mirror;    return true;
                default:          modifier = default;                  return false;
            }
        }

        public static string ToProtocolName(this StreamModifier modifier)
        {
            return modifier switch
            {
                StreamModifier.Speed     => "speed",
                StreamModifier.AllTaps   => "allTaps",
                StreamModifier.AllHopos  => "allHopos",
                StreamModifier.AllStrums => "allStrums",
                StreamModifier.Shuffle   => "shuffle",
                StreamModifier.Mirror    => "mirror",
                _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, null)
            };
        }

        public static string ToDisplayName(this StreamModifier modifier)
        {
            return modifier switch
            {
                StreamModifier.Speed     => "Speed",
                StreamModifier.AllTaps   => "All Taps",
                StreamModifier.AllHopos  => "All HOPOs",
                StreamModifier.AllStrums => "All Strums",
                StreamModifier.Shuffle   => "Shuffle",
                StreamModifier.Mirror    => "Mirror",
                _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, null)
            };
        }

        public static ConflictGroup GetGroup(this StreamModifier modifier)
        {
            return modifier switch
            {
                StreamModifier.Speed     => ConflictGroup.Speed,
                StreamModifier.AllTaps   => ConflictGroup.NoteType,
                StreamModifier.AllHopos  => ConflictGroup.NoteType,
                StreamModifier.AllStrums => ConflictGroup.NoteType,
                StreamModifier.Shuffle   => ConflictGroup.Lanes,
                StreamModifier.Mirror    => ConflictGroup.Lanes,
                _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, null)
            };
        }
    }

    public sealed class ActiveEffect
    {
        public StreamModifier Modifier { get; }
        public ConflictGroup Group => Modifier.GetGroup();

        /// <summary>Target song speed; only meaningful for <see cref="StreamModifier.Speed"/>.</summary>
        public double SpeedValue { get; }

        /// <summary>Shuffle seed; only meaningful for <see cref="StreamModifier.Shuffle"/>.</summary>
        public int Seed { get; }

        public double DurationSeconds  { get; }
        public double RemainingSeconds { get; internal set; }

        public string CommandId { get; }
        public StreamSource Source { get; }

        public ActiveEffect(StreamModifier modifier, double durationSeconds, string commandId, StreamSource source,
            double speedValue = 1.0, int seed = 0)
        {
            Modifier = modifier;
            DurationSeconds = durationSeconds;
            RemainingSeconds = durationSeconds;
            CommandId = commandId;
            Source = source;
            SpeedValue = speedValue;
            Seed = seed;
        }

        public GuitarNoteType? NoteType => Modifier switch
        {
            StreamModifier.AllTaps   => GuitarNoteType.Tap,
            StreamModifier.AllHopos  => GuitarNoteType.Hopo,
            StreamModifier.AllStrums => GuitarNoteType.Strum,
            _ => null
        };

        public int[]? FretMap => Modifier switch
        {
            StreamModifier.Mirror  => ChartMutator.MirrorMap,
            StreamModifier.Shuffle => ChartMutator.ShuffleMap(Seed),
            _ => null
        };

        public string Describe()
        {
            return Modifier == StreamModifier.Speed
                ? $"{Modifier.ToDisplayName()} {Math.Round(SpeedValue * 100)}%"
                : Modifier.ToDisplayName();
        }
    }

    /// <summary>
    /// The set of active effects: at most one per <see cref="ConflictGroup"/>, newest wins.
    /// </summary>
    public sealed class EffectSet
    {
        private static readonly ConflictGroup[] Groups = (ConflictGroup[]) Enum.GetValues(typeof(ConflictGroup));

        private readonly ActiveEffect?[] _byGroup = new ActiveEffect?[Groups.Length];

        public ActiveEffect? Get(ConflictGroup group) => _byGroup[(int) group];

        public ActiveEffect? Speed    => Get(ConflictGroup.Speed);
        public ActiveEffect? NoteType => Get(ConflictGroup.NoteType);
        public ActiveEffect? Lanes    => Get(ConflictGroup.Lanes);

        public int Count
        {
            get
            {
                int count = 0;
                foreach (var effect in _byGroup)
                {
                    if (effect != null) count++;
                }

                return count;
            }
        }

        /// <summary>Active effects in group order.</summary>
        public IEnumerable<ActiveEffect> Active
        {
            get
            {
                foreach (var effect in _byGroup)
                {
                    if (effect != null) yield return effect;
                }
            }
        }

        /// <summary>
        /// Adds <paramref name="effect"/>, replacing whatever was active in its group.
        /// </summary>
        /// <returns>The replaced effect, if any.</returns>
        public ActiveEffect? Apply(ActiveEffect effect)
        {
            int index = (int) effect.Group;
            var replaced = _byGroup[index];
            _byGroup[index] = effect;
            return replaced;
        }

        /// <summary>
        /// Counts every effect down by <paramref name="seconds"/> and removes the ones that ran out.
        /// </summary>
        public List<ActiveEffect> Advance(double seconds)
        {
            var expired = new List<ActiveEffect>();
            for (int i = 0; i < _byGroup.Length; i++)
            {
                var effect = _byGroup[i];
                if (effect == null) continue;

                effect.RemainingSeconds -= seconds;
                if (effect.RemainingSeconds <= 0)
                {
                    effect.RemainingSeconds = 0;
                    _byGroup[i] = null;
                    expired.Add(effect);
                }
            }

            return expired;
        }

        public List<ActiveEffect> Clear()
        {
            var cleared = new List<ActiveEffect>(Active);
            Array.Clear(_byGroup, 0, _byGroup.Length);
            return cleared;
        }
    }

    /// <summary>
    /// Moves the song speed toward a target in fixed steps (0.05 every 50 ms of real time).
    /// </summary>
    public sealed class SpeedRamp
    {
        public const double STEP_SIZE     = 0.05;
        public const double STEP_INTERVAL = 0.05;

        private double _accumulator;

        public double Current { get; private set; } = 1.0;
        public double Target  { get; private set; } = 1.0;

        public bool IsRamping => Current != Target;

        public void SetTarget(double target)
        {
            Target = target;
        }

        /// <summary>Jumps straight to <paramref name="speed"/> with no ramp.</summary>
        public void Reset(double speed = 1.0)
        {
            Current = Target = speed;
            _accumulator = 0;
        }

        /// <summary>
        /// Advances the ramp by <paramref name="seconds"/>.
        /// </summary>
        /// <returns>True if <see cref="Current"/> changed.</returns>
        public bool Advance(double seconds)
        {
            if (!IsRamping)
            {
                _accumulator = 0;
                return false;
            }

            _accumulator += seconds;
            bool changed = false;
            while (_accumulator >= STEP_INTERVAL && IsRamping)
            {
                _accumulator -= STEP_INTERVAL;

                double delta = Target - Current;
                // Rounding keeps values like 1.2500000001 from accumulating
                Current = Math.Abs(delta) <= STEP_SIZE
                    ? Target
                    : Math.Round(Current + Math.Sign(delta) * STEP_SIZE, 4);
                changed = true;
            }

            return changed;
        }
    }
}
