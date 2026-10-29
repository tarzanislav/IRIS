using Iris.Memory;
using Iris.Translation;

namespace Iris.Tests;

/// <summary>What another program (UNIVERSUM, the IRIS application) relies on: shared memory, cancellation, paths.</summary>
public static class IntegrationTests
{
    [Test]
    public static void TwoProgramsShareOneMemory()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"iris-shared-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "memory-en-sk.jsonl");
        try
        {
            // two instances on one file stand for two programs
            var universum = TranslationMemory.Open(path);
            var application = TranslationMemory.Open(path);
            universum.Remember("The premiere is tonight.", "Premiéra je dnes večer.");
            Assert.True(application.TryGet("The premiere is tonight.", out var seen) && seen == "Premiéra je dnes večer.",
                "a correction made in one program is known in the other");
            Assert.True(application.Forget("The premiere is tonight."), "the other program can forget it");
            Assert.True(!universum.TryGet("The premiere is tonight.", out _), "and the first one sees that too");

            // both write at the same time: nothing is lost
            Parallel.For(0, 200, i => (i % 2 == 0 ? universum : application).Remember($"Sentence number {i}.", $"Veta číslo {i}."));
            Assert.Equal(200, TranslationMemory.Open(path).Count, "every correction of both programs is in the file");
            Assert.Equal(200, universum.Count, "and in each program");
            application.Compact();
            Assert.Equal(200, universum.Count, "compacting in one program keeps what the other one wrote");
            Assert.Equal(200, File.ReadAllLines(path).Length, "one line per sentence after compacting");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public static void LongTextsCanBeCancelledAndReportProgress()
    {
        using var tiny = TinyModel.Create();
        var translator = Translator.Load(tiny.Path);
        const string text = "The film premiere is tonight. Night. The premiere. Film film film.";
        var reports = new List<TranslationProgress>();
        var progress = new SynchronousProgress(reports.Add);
        var translation = translator.TranslateText(text, beams: 1, progress: progress);
        Assert.Equal(4, translation.Sentences.Count, "four sentences");
        Assert.Sequence([1, 2, 3, 4], reports.Select(r => r.Done), "progress after each sentence");
        Assert.True(reports.All(r => r.Total == 4), "of four");

        using var cancel = new CancellationTokenSource();
        var counted = 0;
        var stopAfterTwo = new SynchronousProgress(p =>
        {
            counted = p.Done;
            if (p.Done == 2)
            {
                cancel.Cancel();
            }
        });
        Assert.Throws<OperationCanceledException>(() => translator.TranslateText(text, 1, null, null, cancel.Token, stopAfterTwo),
            "cancelled between sentences");
        Assert.Equal(2, counted, "no sentence after the cancellation");
    }

    [Test]
    public static void SharedPathsLiveInOneFolder()
    {
        var before = Environment.GetEnvironmentVariable("IRIS_HOME");
        try
        {
            Environment.SetEnvironmentVariable("IRIS_HOME", Path.Combine(Path.GetTempPath(), "iris-home"));
            Assert.Equal(Path.Combine(Path.GetTempPath(), "iris-home", "memory-en-sk.jsonl"), IrisPaths.Memory("en-sk"), "memory");
            Assert.Equal(Path.Combine(Path.GetTempPath(), "iris-home", "glossary-cs-en.tsv"), IrisPaths.Glossary("cs-en"), "glossary");
            Assert.Throws<ArgumentException>(() => IrisPaths.Memory("../x"), "a pair cannot leave the folder");
            Assert.Throws<ArgumentException>(() => IrisPaths.Memory("EN-SK"), "pairs are lower case");
            Environment.SetEnvironmentVariable("IRIS_HOME", null);
            Assert.True(IrisPaths.Home.EndsWith("IRIS", StringComparison.Ordinal), $"the user's application data: {IrisPaths.Home}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("IRIS_HOME", before);
        }
    }

    // Progress<T> posts to a synchronization context; tests want each report at once
    private sealed class SynchronousProgress(Action<TranslationProgress> report) : IProgress<TranslationProgress>
    {
        public void Report(TranslationProgress value) => report(value);
    }
}
