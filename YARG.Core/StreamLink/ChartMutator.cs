using System;
using System.Collections.Generic;
using YARG.Core.Chart;

namespace YARG.Core.StreamLink
{
    /// <summary>
    /// In-place, reversible changes to the upcoming part of a five-fret note list.
    /// </summary>
    /// <remarks>
    /// Only <see cref="GuitarNote.Type"/>, <see cref="GuitarNote.Fret"/> and the masks derived from the fret
    /// are ever changed. Timing, ticks, flags, hit state and the previous/next links are left alone, so the
    /// engine's view of the chart (note count, scoring totals, solos) stays valid.
    /// </remarks>
    public static class ChartMutator
    {
        // Indexed by fret number (see FiveFretGuitarFret). Index 0 and 6 are unused, 7 is open, 8 is wildcard.
        private const int FRET_MAP_LENGTH = 9;

        private const int FIRST_COLORED_FRET = (int) FiveFretGuitarFret.Green;
        private const int LAST_COLORED_FRET  = (int) FiveFretGuitarFret.Orange;

        /// <summary>
        /// Gâ†”O, Râ†”B, Y stays.
        /// </summary>
        public static readonly int[] MirrorMap = CreateMap(new[]
        {
            FiveFretGuitarFret.Orange, FiveFretGuitarFret.Blue, FiveFretGuitarFret.Yellow,
            FiveFretGuitarFret.Red, FiveFretGuitarFret.Green,
        });

        public static readonly int[] IdentityMap = CreateMap(new[]
        {
            FiveFretGuitarFret.Green, FiveFretGuitarFret.Red, FiveFretGuitarFret.Yellow,
            FiveFretGuitarFret.Blue, FiveFretGuitarFret.Orange,
        });

        /// <summary>
        /// A random permutation of G/R/Y/B/O that depends only on <paramref name="seed"/>.
        /// Never returns the identity permutation.
        /// </summary>
        /// <remarks>
        /// Uses its own PRNG rather than <see cref="Random"/> so the result is the same on every runtime
        /// (Mono in the player, CoreCLR in the tests).
        /// </remarks>
        public static int[] ShuffleMap(int seed)
        {
            var rng = new SplitMix64((ulong) (uint) seed);
            var frets = new int[LAST_COLORED_FRET - FIRST_COLORED_FRET + 1];

            do
            {
                for (int i = 0; i < frets.Length; i++)
                {
                    frets[i] = FIRST_COLORED_FRET + i;
                }

                for (int i = frets.Length - 1; i > 0; i--)
                {
                    int j = (int) (rng.Next() % (ulong) (i + 1));
                    (frets[i], frets[j]) = (frets[j], frets[i]);
                }
            } while (IsIdentity(frets));

            var map = (int[]) IdentityMap.Clone();
            for (int i = 0; i < frets.Length; i++)
            {
                map[FIRST_COLORED_FRET + i] = frets[i];
            }

            return map;
        }

        /// <summary>
        /// Copies the note type, frets and masks of every note at or after <paramref name="from"/>
        /// back from <paramref name="pristine"/>.
        /// </summary>
        public static void RestoreFrom(List<GuitarNote> live, List<GuitarNote> pristine, int from)
        {
            if (live.Count != pristine.Count)
            {
                throw new InvalidOperationException(
                    $"Live and pristine note lists differ in length ({live.Count} vs {pristine.Count})");
            }

            for (int i = Math.Max(from, 0); i < live.Count; i++)
            {
                var note = live[i];
                var original = pristine[i];

                if (note.ChildNotes.Count != original.ChildNotes.Count)
                {
                    throw new InvalidOperationException($"Chord size mismatch at note index {i}");
                }

                CopyMutableFields(note, original);
                for (int c = 0; c < note.ChildNotes.Count; c++)
                {
                    CopyMutableFields(note.ChildNotes[c], original.ChildNotes[c]);
                }
            }
        }

        /// <summary>
        /// Sets every note (and chord child) at or after <paramref name="from"/> to <paramref name="type"/>.
        /// </summary>
        public static void ApplyNoteType(List<GuitarNote> live, int from, GuitarNoteType type)
        {
            for (int i = Math.Max(from, 0); i < live.Count; i++)
            {
                foreach (var note in live[i].AllNotes)
                {
                    note.Type = type;
                }
            }
        }

        /// <summary>
        /// Remaps the colored frets of every note at or after <paramref name="from"/> through
        /// <paramref name="map"/> (indexed by fret number). Open notes, wildcards and chords that are
        /// part of a tremolo/trill lane are left as they are.
        /// </summary>
        public static void ApplyFretMap(List<GuitarNote> live, int from, int[] map)
        {
            ValidateMap(map);

            for (int i = Math.Max(from, 0); i < live.Count; i++)
            {
                var parent = live[i];
                if (IsInLane(parent))
                {
                    continue;
                }

                foreach (var note in parent.AllNotes)
                {
                    if (IsColoredFret(note.Fret))
                    {
                        note.Fret = map[note.Fret];
                    }
                }

                parent.RecalculateMasks();
            }
        }

        /// <summary>
        /// Restores the upcoming notes from <paramref name="pristine"/>, then applies the active effects in
        /// the fixed order: note type first, then fret map.
        /// </summary>
        public static void Rebuild(List<GuitarNote> live, List<GuitarNote> pristine, int from,
            GuitarNoteType? noteType, int[]? fretMap)
        {
            RestoreFrom(live, pristine, from);

            if (noteType.HasValue)
            {
                ApplyNoteType(live, from, noteType.Value);
            }

            if (fretMap != null)
            {
                ApplyFretMap(live, from, fretMap);
            }
        }

        /// <summary>
        /// Deep-copies a note list so it can serve as the pristine snapshot for <see cref="RestoreFrom"/>.
        /// Previous/next links are not copied, since only the per-note values are ever read back.
        /// </summary>
        public static List<GuitarNote> Snapshot(List<GuitarNote> live)
        {
            var copy = new List<GuitarNote>(live.Count);
            foreach (var note in live)
            {
                var clone = note.Clone();
                // Clone() rebuilds masks from the constructor; copy the real ones in case they differ
                CopyMutableFields(clone, note);
                for (int c = 0; c < clone.ChildNotes.Count; c++)
                {
                    CopyMutableFields(clone.ChildNotes[c], note.ChildNotes[c]);
                }

                copy.Add(clone);
            }

            return copy;
        }

        private static void CopyMutableFields(GuitarNote target, GuitarNote source)
        {
            target.Type = source.Type;
            target.Fret = source.Fret;
            target.NoteMask = source.NoteMask;
            target.DisjointMask = source.DisjointMask;
        }

        private static bool IsInLane(GuitarNote parent)
        {
            foreach (var note in parent.AllNotes)
            {
                if (note.IsLane)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsColoredFret(int fret)
        {
            return fret >= FIRST_COLORED_FRET && fret <= LAST_COLORED_FRET;
        }

        private static bool IsIdentity(int[] frets)
        {
            for (int i = 0; i < frets.Length; i++)
            {
                if (frets[i] != FIRST_COLORED_FRET + i)
                {
                    return false;
                }
            }

            return true;
        }

        private static int[] CreateMap(FiveFretGuitarFret[] coloredTargets)
        {
            var map = new int[FRET_MAP_LENGTH];
            for (int i = 0; i < map.Length; i++)
            {
                map[i] = i;
            }

            for (int i = 0; i < coloredTargets.Length; i++)
            {
                map[FIRST_COLORED_FRET + i] = (int) coloredTargets[i];
            }

            return map;
        }

        private static void ValidateMap(int[] map)
        {
            if (map.Length != FRET_MAP_LENGTH)
            {
                throw new ArgumentException($"Fret map must have {FRET_MAP_LENGTH} entries", nameof(map));
            }

            int seen = 0;
            for (int fret = FIRST_COLORED_FRET; fret <= LAST_COLORED_FRET; fret++)
            {
                int target = map[fret];
                if (!IsColoredFret(target) || (seen & (1 << target)) != 0)
                {
                    throw new ArgumentException("Fret map must be a permutation of the colored frets", nameof(map));
                }

                seen |= 1 << target;
            }
        }

        private struct SplitMix64
        {
            private ulong _state;

            public SplitMix64(ulong seed)
            {
                _state = seed;
            }

            public ulong Next()
            {
                ulong z = _state += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
