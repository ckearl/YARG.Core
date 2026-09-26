using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.StreamLink;

namespace YARG.Core.UnitTests.StreamLink;

public class ChartMutatorTests
{
    private const FiveFretGuitarFret G = FiveFretGuitarFret.Green;
    private const FiveFretGuitarFret R = FiveFretGuitarFret.Red;
    private const FiveFretGuitarFret Y = FiveFretGuitarFret.Yellow;
    private const FiveFretGuitarFret B = FiveFretGuitarFret.Blue;
    private const FiveFretGuitarFret O = FiveFretGuitarFret.Orange;
    private const FiveFretGuitarFret Open = FiveFretGuitarFret.Open;
    private const FiveFretGuitarFret Wild = FiveFretGuitarFret.Wildcard;

    // index: 0 G, 1 R+Y chord, 2 open, 3 open+B chord, 4 wildcard, 5 trill Y, 6 trill B, 7 G+O disjoint chord, 8 O hopo
    private static List<GuitarNote> BuildChart()
    {
        var notes = new List<GuitarNote>
        {
            Chord(0, GuitarNoteType.Strum, NoteFlags.StarPower, G),
            Chord(1, GuitarNoteType.Strum, NoteFlags.None, R, Y),
            Chord(2, GuitarNoteType.Hopo, NoteFlags.None, Open),
            Chord(3, GuitarNoteType.Strum, NoteFlags.Solo, Open, B),
            Chord(4, GuitarNoteType.Strum, NoteFlags.None, Wild),
            Chord(5, GuitarNoteType.Hopo, NoteFlags.Trill | NoteFlags.LaneStart, Y),
            Chord(6, GuitarNoteType.Hopo, NoteFlags.Trill | NoteFlags.LaneEnd, B),
            Chord(7, GuitarNoteType.Strum, NoteFlags.None, G, O),
            Chord(8, GuitarNoteType.Hopo, NoteFlags.None, O),
        };
        notes[7].GuitarFlags = GuitarNoteFlags.Disjoint;

        for (int i = 1; i < notes.Count; i++)
        {
            notes[i].PreviousNote = notes[i - 1];
            notes[i - 1].NextNote = notes[i];
        }

        return notes;
    }

    private static GuitarNote Chord(int index, GuitarNoteType type, NoteFlags flags, params FiveFretGuitarFret[] frets)
    {
        double time = index * 0.5;
        uint tick = (uint) index * 480;
        var parent = new GuitarNote(frets[0], type, GuitarNoteFlags.None, flags, time, 0, tick, 0);
        foreach (var fret in frets.Skip(1))
        {
            parent.AddChildNote(new GuitarNote(fret, type, GuitarNoteFlags.None, flags, time, 0, tick, 0));
        }

        return parent;
    }

    private static int[] Frets(GuitarNote parent)
    {
        var frets = new List<int>();
        foreach (var note in parent.AllNotes)
        {
            frets.Add(note.Fret);
        }

        return frets.ToArray();
    }

    private static void AssertSameMutableState(List<GuitarNote> actual, List<GuitarNote> expected, int from = 0)
    {
        for (int i = from; i < expected.Count; i++)
        {
            var a = actual[i].AllNotes.GetEnumerator();
            var e = expected[i].AllNotes.GetEnumerator();
            while (e.MoveNext())
            {
                Assert.That(a.MoveNext(), Is.True);
                Assert.That(a.Current.Fret, Is.EqualTo(e.Current.Fret), $"Fret at {i}");
                Assert.That(a.Current.Type, Is.EqualTo(e.Current.Type), $"Type at {i}");
                Assert.That(a.Current.NoteMask, Is.EqualTo(e.Current.NoteMask), $"NoteMask at {i}");
                Assert.That(a.Current.DisjointMask, Is.EqualTo(e.Current.DisjointMask), $"DisjointMask at {i}");
            }
        }
    }

    [Test]
    public void MirrorMap_SwapsOuterFretsAndKeepsYellow()
    {
        Assert.That(ChartMutator.MirrorMap[(int) G], Is.EqualTo((int) O));
        Assert.That(ChartMutator.MirrorMap[(int) R], Is.EqualTo((int) B));
        Assert.That(ChartMutator.MirrorMap[(int) Y], Is.EqualTo((int) Y));
        Assert.That(ChartMutator.MirrorMap[(int) B], Is.EqualTo((int) R));
        Assert.That(ChartMutator.MirrorMap[(int) O], Is.EqualTo((int) G));
        Assert.That(ChartMutator.MirrorMap[(int) Open], Is.EqualTo((int) Open));
        Assert.That(ChartMutator.MirrorMap[(int) Wild], Is.EqualTo((int) Wild));
    }

    [Test]
    public void ShuffleMap_IsDeterministicNonIdentityPermutation()
    {
        var seen = new HashSet<string>();
        for (int seed = -50; seed < 200; seed++)
        {
            var map = ChartMutator.ShuffleMap(seed);
            Assert.That(map, Is.EqualTo(ChartMutator.ShuffleMap(seed)), "same seed, same map");
            Assert.That(map[1..6].OrderBy(x => x), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }), "permutation of G..O");
            Assert.That(map, Is.Not.EqualTo(ChartMutator.IdentityMap), "never identity");
            Assert.That(map[0], Is.Zero);
            Assert.That(map[(int) Open], Is.EqualTo((int) Open));
            Assert.That(map[(int) Wild], Is.EqualTo((int) Wild));
            seen.Add(string.Join(",", map));
        }

        Assert.That(seen, Has.Count.GreaterThan(50), "different seeds should give varied maps");
    }

    [Test]
    public void ShuffleMap_IsStableAcrossRuntimes()
    {
        // Values from an independent SplitMix64 + Fisher-Yates implementation. A PRNG change would make
        // the same seed produce a different layout, so it should fail here first.
        Assert.That(ChartMutator.ShuffleMap(0), Is.EqualTo(new[] { 0, 3, 4, 2, 5, 1, 6, 7, 8 }));
        Assert.That(ChartMutator.ShuffleMap(12345), Is.EqualTo(new[] { 0, 4, 3, 1, 2, 5, 6, 7, 8 }));
    }

    [Test]
    public void ApplyThenRestore_ReturnsOriginalChart()
    {
        var live = BuildChart();
        var pristine = ChartMutator.Snapshot(live);

        ChartMutator.ApplyNoteType(live, 0, GuitarNoteType.Tap);
        ChartMutator.ApplyFretMap(live, 0, ChartMutator.ShuffleMap(7));
        ChartMutator.RestoreFrom(live, pristine, 0);

        AssertSameMutableState(live, BuildChart());
    }

    [Test]
    public void Mirror_MasksMatchFromScratchConstruction()
    {
        var live = BuildChart();
        ChartMutator.ApplyFretMap(live, 0, ChartMutator.MirrorMap);

        var expected = new List<GuitarNote>
        {
            Chord(0, GuitarNoteType.Strum, NoteFlags.StarPower, O),
            Chord(1, GuitarNoteType.Strum, NoteFlags.None, B, Y),
            Chord(2, GuitarNoteType.Hopo, NoteFlags.None, Open),
            Chord(3, GuitarNoteType.Strum, NoteFlags.Solo, Open, R),
            Chord(4, GuitarNoteType.Strum, NoteFlags.None, Wild),
            Chord(5, GuitarNoteType.Hopo, NoteFlags.Trill | NoteFlags.LaneStart, Y),
            Chord(6, GuitarNoteType.Hopo, NoteFlags.Trill | NoteFlags.LaneEnd, B),
            Chord(7, GuitarNoteType.Strum, NoteFlags.None, O, G),
            Chord(8, GuitarNoteType.Hopo, NoteFlags.None, G),
        };

        AssertSameMutableState(live, expected);
    }

    [Test]
    public void FretMap_SkipsOpensWildcardsAndLanes()
    {
        var live = BuildChart();
        ChartMutator.ApplyFretMap(live, 0, ChartMutator.MirrorMap);

        Assert.That(Frets(live[2]), Is.EqualTo(new[] { (int) Open }));
        Assert.That(Frets(live[3])[0], Is.EqualTo((int) Open), "open stays open inside a chord");
        Assert.That(Frets(live[4]), Is.EqualTo(new[] { (int) Wild }));
        Assert.That(Frets(live[5]), Is.EqualTo(new[] { (int) Y }), "trill lane start untouched");
        Assert.That(Frets(live[6]), Is.EqualTo(new[] { (int) B }), "trill lane end untouched");
    }

    [Test]
    public void Mutations_OnlyAffectNotesFromBoundary()
    {
        var live = BuildChart();
        var pristine = ChartMutator.Snapshot(live);

        ChartMutator.Rebuild(live, pristine, 3, GuitarNoteType.Tap, ChartMutator.MirrorMap);

        AssertSameMutableState(live.Take(3).ToList(), BuildChart().Take(3).ToList());
        Assert.That(live[8].Fret, Is.EqualTo((int) G));
        Assert.That(live[8].Type, Is.EqualTo(GuitarNoteType.Tap));
    }

    [Test]
    public void Mutations_NeverTouchTimingFlagsStateOrLinks()
    {
        var live = BuildChart();
        var pristine = ChartMutator.Snapshot(live);
        live[0].SetHitState(true, true);
        live[1].SetMissState(true, true);

        var before = live.Select(n => (n.Time, n.Tick, n.Flags, n.GuitarFlags, n.WasHit, n.WasMissed,
            n.PreviousNote, n.NextNote, n.ChildNotes.Count)).ToList();

        ChartMutator.Rebuild(live, pristine, 0, GuitarNoteType.Hopo, ChartMutator.ShuffleMap(3));
        ChartMutator.RestoreFrom(live, pristine, 0);

        var after = live.Select(n => (n.Time, n.Tick, n.Flags, n.GuitarFlags, n.WasHit, n.WasMissed,
            n.PreviousNote, n.NextNote, n.ChildNotes.Count)).ToList();

        Assert.That(after, Is.EqualTo(before));
    }

    [Test]
    public void NoteType_AppliesToChordChildren()
    {
        var live = BuildChart();
        ChartMutator.ApplyNoteType(live, 0, GuitarNoteType.Tap);

        foreach (var parent in live)
        {
            foreach (var note in parent.AllNotes)
            {
                Assert.That(note.Type, Is.EqualTo(GuitarNoteType.Tap));
            }
        }
    }

    [Test]
    public void Rebuild_IsDeterministicAndOrderIndependentOfHistory()
    {
        var a = BuildChart();
        var pristineA = ChartMutator.Snapshot(a);
        ChartMutator.Rebuild(a, pristineA, 0, GuitarNoteType.Strum, ChartMutator.ShuffleMap(99));

        // Same final effect set reached through a different history
        var b = BuildChart();
        var pristineB = ChartMutator.Snapshot(b);
        ChartMutator.Rebuild(b, pristineB, 0, GuitarNoteType.Tap, ChartMutator.MirrorMap);
        ChartMutator.Rebuild(b, pristineB, 0, null, ChartMutator.ShuffleMap(5));
        ChartMutator.Rebuild(b, pristineB, 0, GuitarNoteType.Strum, ChartMutator.ShuffleMap(99));

        AssertSameMutableState(b, a);
    }

    [Test]
    public void Chords_NeverCollapseOntoOneFret()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var live = BuildChart();
            ChartMutator.ApplyFretMap(live, 0, ChartMutator.ShuffleMap(seed));
            foreach (var parent in live)
            {
                var frets = Frets(parent);
                Assert.That(frets.Distinct().Count(), Is.EqualTo(frets.Length));
            }
        }
    }

    [Test]
    public void RestoreFrom_ThrowsOnMismatchedLists()
    {
        var live = BuildChart();
        var pristine = ChartMutator.Snapshot(live);
        pristine.RemoveAt(pristine.Count - 1);

        Assert.Throws<InvalidOperationException>(() => ChartMutator.RestoreFrom(live, pristine, 0));
    }

    [Test]
    public void ApplyFretMap_RejectsNonPermutation()
    {
        var map = (int[]) ChartMutator.IdentityMap.Clone();
        map[(int) R] = (int) G;

        Assert.Throws<ArgumentException>(() => ChartMutator.ApplyFretMap(BuildChart(), 0, map));
    }
}
