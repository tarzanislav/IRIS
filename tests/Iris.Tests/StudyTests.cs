using Iris.Assistant;
using Iris.Assistant.Packs;
using Iris.Assistant.Study;
using Iris.Learning;
using Iris.Memory;
using Iris.Model;
using Iris.Translation;

namespace Iris.Tests;

/// <summary>The study room: verified pairs, pairing the sentences of two texts, learning with a check, using what was learned.</summary>
public static class StudyTests
{
    [Test]
    public static void StudyPairsAreKeptOnceAndSurviveARestart()
    {
        var folder = Temp();
        try
        {
            var path = Path.Combine(folder, "study-en-sk.jsonl");
            var room = StudyRoom.Open(path);
            Assert.Equal(2, room.Add([("The cell divides.", "Bunka sa delí."), ("Water boils.", "Voda vrie."), ("  ", "x"), ("The cell divides.", "Bunka sa delí.")], "biology"),
                "empty pairs and repeats are left out");
            Assert.Equal(0, room.Add([("The cell divides.", "Bunka sa delí.")]), "a pair is kept once");
            File.AppendAllText(path, "{\"Source\":\"cut off");
            var again = StudyRoom.Open(path);
            Assert.Equal(2, again.Count, "read again (a damaged last line does not matter)");
            Assert.Equal("biology", again.Pairs[0].Label, "the label");
            Assert.True(again.Remove(again.Pairs[0]) && StudyRoom.Open(path).Count == 1, "removed for good");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public static void SentencesArePairedInOrderAlsoOneToTwo()
    {
        // a stand-in for IRIS: a small dictionary of sentences
        var known = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["The cell is the basic unit of life."] = "Bunka je základná jednotka života.",
            ["It has a membrane and a nucleus, and it divides when it grows."] = "Má membránu a jadro a delí sa, keď rastie.",
            ["Plants make sugar from light."] = "Rastliny vyrábajú cukor zo svetla.",
            ["Figure 3."] = "Obrázok 3.",
        };
        var original = string.Join(" ", known.Keys);
        // the translator of the book split the second sentence in two and left out the figure caption
        var translation = "Bunka je základnou jednotkou života. Má membránu a jadro. Delí sa, keď rastie. Rastliny vyrábajú cukor zo svetla.";
        var pairs = SentenceAligner.Align(original, translation, s => known[s]);
        Assert.Equal(3, pairs.Count, "three pairs");
        Assert.Equal("Bunka je základnou jednotkou života.", pairs[0].Target, "one to one");
        Assert.Equal("Má membránu a jadro. Delí sa, keď rastie.", pairs[1].Target, "one to two");
        Assert.Equal("Plants make sugar from light.", pairs[2].Source, "the caption without a partner is left out");
        Assert.True(pairs.All(p => p.IsSure), "all sure");
        Assert.Equal(0, SentenceAligner.Align("", translation, s => s).Count, "nothing to pair");
    }

    [Test]
    public static void LearningIsUsedOnlyWhenItPassedItsCheck()
    {
        var folder = Temp();
        try
        {
            using var tiny = TinyModel.Create(learnable: true);
            var store = new PackStore(Path.Combine(folder, "models"));
            Directory.CreateDirectory(store.Folder);
            var pair = LanguagePair.Parse("en-sk");
            File.Copy(tiny.Path, store.PathOf(pair));
            var learned = Path.Combine(folder, "learned-en-sk.iris");
            var words = "the film premiere festival director actor series season night city world story".Split(' ');
            var pairs = Enumerable.Range(0, 30).Select(i => ($"{words[i % words.Length]} {words[i / words.Length]}", $"{words[(i * 7 + 2) % words.Length]} film")).ToList();

            Assert.Throws<InvalidOperationException>(() => Learner.Learn(pair, store.PathOf(pair), pairs.Take(10).ToList(), learned), "too few pairs");
            var report = Learner.Learn(pair, store.PathOf(pair), pairs, learned, new LearningSettings { Epochs = 2, LearningRate = 1e-2f });
            Assert.True(report.Checked >= 2 && report.Pairs + report.Checked == 30, $"pairs kept aside for the check: {report.Checked}");
            Assert.Equal(report.Accepted, File.Exists(learned), "the adapter is written only when it passed");
            Assert.Equal(report, Learner.LastReport(learned), "the report is kept");

            // an adapter that changes the translation, written where the service looks for it
            var translator = Translator.Load(store.PathOf(pair));
            var adapter = Adapter.Create(translator.Model, ModelFile.Sha256(store.PathOf(pair)), rank: 2);
            var random = new Random(9);
            foreach (var low in adapter.Layers.Values)
            {
                for (var i = 0; i < low.B.Length; i++)
                {
                    low.B[i] = (float)(random.NextDouble() - 0.5) * 4;
                }
            }

            adapter.Save(learned);
            var service = new TranslationService(store, _ => TranslationMemory.InMemory(), _ => new Glossary(), p => Path.Combine(folder, $"learned-{p}.iris"));
            var plain = new TranslationService(store, _ => TranslationMemory.InMemory(), _ => new Glossary());
            var withLearning = service.Translate("the film premiere", "en", "sk", beams: 1).Text;
            Assert.True(service.IsLearned(pair) && !plain.IsLearned(pair), "the learned adapter is used");
            Assert.True(withLearning != plain.Translate("the film premiere", "en", "sk", beams: 1).Text, "and changes the translation");
            service.UseLearned = false;
            Assert.Equal(plain.Translate("the film premiere", "en", "sk", beams: 1).Text, service.Translate("the film premiere", "en", "sk", beams: 1).Text,
                "turned off: the pack alone");

            using var other = TinyModel.Create(learnable: true, d: 24);
            Adapter.Create(Translator.Load(other.Path).Model, ModelFile.Sha256(other.Path)).Save(learned);
            service.UseLearned = true;
            service.Translate("the film premiere", "en", "sk", beams: 1);
            Assert.True(!service.IsLearned(pair), "an adapter of another model is not used");
            Assert.True(UserData.Files(folder).Any(f => f.EndsWith("learned-en-sk.iris", StringComparison.Ordinal)), "what was learned is the user's data");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Temp()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"iris-study-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
