using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Game;
using YARG.Core.StreamLink;
using YARG.Core.UnitTests.Engine;

namespace YARG.Core.UnitTests.StreamLink;

/// <summary>
/// Runs the five-fret bot over real charts while randomly re-mutating the upcoming notes, the way the
/// in-game director does. Extra charts (.mid or .chart files, searched recursively) can be added by
/// pointing the SL_SOAK_CHARTS environment variable at a folder.
/// </summary>
public class ChartMutatorSoakTests : EngineTester
{
    private const double FRAME_TIME      = 1.0 / 240;
    private const double MUTATE_INTERVAL = 2.0;
    // Roughly the highway's spawn lookahead plus the director's 0.25 s safety margin
    private const double SPAWN_LOOKAHEAD = 1.25;

    public static IEnumerable<TestCaseData> Charts()
    {
        string testCharts = Path.Combine(
            Directory.GetParent(Environment.CurrentDirectory)!.Parent!.Parent!.Parent!.FullName, "Engine", "Test Charts");
        var files = Directory.GetFiles(testCharts, "*.mid").ToList();

        string? extra = Environment.GetEnvironmentVariable("SL_SOAK_CHARTS");
        if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra))
        {
            files.AddRange(Directory.GetFiles(extra, "*.mid", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(extra, "*.chart", SearchOption.AllDirectories));
        }

        foreach (var file in files)
        {
            foreach (bool bass in new[] { false, true })
            {
                string name = $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)} {(bass ? "bass" : "guitar")}";
                yield return new TestCaseData(file, bass).SetName($"Soak({name})");
            }
        }
    }

    [TestCaseSource(nameof(Charts))]
    public void BotSurvivesRandomMutations(string chartPath, bool isBass)
    {
        var baseline = Run(chartPath, isBass, mutate: false);
        if (baseline == null)
        {
            Assert.Ignore("Chart has no expert five-fret notes for this instrument");
        }

        var mutated = Run(chartPath, isBass, mutate: true)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mutated.NoteIndex, Is.EqualTo(mutated.NoteCount), "engine stalled before the end of the chart");
            Assert.That(mutated.Stats.Overstrums, Is.Zero, "overstrums");
            Assert.That(mutated.Stats.NotesHit, Is.EqualTo(baseline.Stats.NotesHit), "notes hit vs. unmodified run");
            Assert.That(mutated.Stats.MaxCombo, Is.EqualTo(baseline.Stats.MaxCombo), "max combo vs. unmodified run");
            Assert.That(mutated.Stats.NotesHit, Is.EqualTo(mutated.Stats.TotalNotes), "full combo");
            Assert.That(mutated.Mutations, Is.GreaterThan(0), "no mutations were applied");
        }
    }

    private sealed record RunResult(GuitarStats Stats, int NoteIndex, int NoteCount, int Mutations);

    private static RunResult? Run(string chartPath, bool isBass, bool mutate)
    {
        var chart = SongChart.FromFile(ParseSettings.Default, chartPath);
        var notes = (isBass ? chart.FiveFretBass : chart.FiveFretGuitar).GetDifficulty(Difficulty.Expert);
        if (notes.Notes.Count == 0)
        {
            return null;
        }

        var parameters = EnginePreset.Default.FiveFretGuitar.Create(StarMultiplierThresholds,
            SoloBonusStarMultiplierThresholds, isBass);
        var engine = new YargFiveFretGuitarEngine(notes, chart.SyncTrack, parameters, isBot: true);

        var live = notes.Notes;
        var pristine = ChartMutator.Snapshot(live);
        var rng = new Random(chartPath.GetHashCode() ^ (isBass ? 1 : 0));
        var noteTypes = new GuitarNoteType?[] { null, GuitarNoteType.Strum, GuitarNoteType.Hopo, GuitarNoteType.Tap };

        double end = live[^1].TimeEnd + 2;
        double nextMutation = MUTATE_INTERVAL;
        int mutations = 0;

        for (double time = 0; time <= end; time += FRAME_TIME)
        {
            if (mutate && time >= nextMutation)
            {
                nextMutation += MUTATE_INTERVAL;

                int from = FindBoundary(live, engine.NoteIndex, time + SPAWN_LOOKAHEAD);
                var noteType = noteTypes[rng.Next(noteTypes.Length)];
                int[]? map = rng.Next(3) switch
                {
                    0 => null,
                    1 => ChartMutator.MirrorMap,
                    _ => ChartMutator.ShuffleMap(rng.Next()),
                };

                ChartMutator.Rebuild(live, pristine, from, noteType, map);
                engine.SetSpeed(0.6 + rng.NextDouble() * 0.9);
                mutations++;
            }

            engine.Update(time);
        }

        return new RunResult(new GuitarStats(engine.EngineStats), engine.NoteIndex, live.Count, mutations);
    }

    private static int FindBoundary(List<GuitarNote> notes, int noteIndex, double minTime)
    {
        int i = noteIndex + 1;
        while (i < notes.Count && notes[i].Time < minTime)
        {
            i++;
        }

        return i;
    }
}
