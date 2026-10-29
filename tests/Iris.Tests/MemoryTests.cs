using Iris.Memory;
using Iris.Translation;

namespace Iris.Tests;

/// <summary>The translation memory, the glossary of names and their use when a text is translated.</summary>
public static class MemoryTests
{
    [Test]
    public static void MemoryRemembersAndForgetsAcrossOpenings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iris-memory-{Guid.NewGuid():N}", "memory.jsonl");
        try
        {
            var memory = TranslationMemory.Open(path);
            memory.Remember("The premiere is tonight.", "Premiéra je dnes večer.");
            memory.Remember("Harbor  Lights returns.", "Harbor Lights sa vracia.");
            memory.Remember("The premiere is tonight.", "Premiéra bude dnes večer.");
            Assert.True(memory.TryGet("  The premiere is   tonight. ", out var t) && t == "Premiéra bude dnes večer.", "the last correction, whitespace ignored");

            var again = TranslationMemory.Open(path);
            Assert.Equal(2, again.Count, "both sentences after reopening");
            Assert.True(again.TryGet("Harbor Lights returns.", out _), "runs of spaces as one");
            Assert.True(again.Forget("Harbor Lights returns."), "forgotten");
            Assert.True(!again.Forget("Never remembered."), "nothing to forget");

            File.AppendAllText(path, "{\"source\": \"cut off\n");
            var third = TranslationMemory.Open(path);
            Assert.Equal(1, third.Count, "the forgetting holds after reopening, a damaged line is skipped");
            third.Compact();
            Assert.Equal(1, File.ReadAllLines(path).Length, "compacted to what holds");
            Assert.True(TranslationMemory.Open(path).TryGet("The premiere is tonight.", out var kept) && kept == "Premiéra bude dnes večer.", "kept after compacting");
            Assert.Equal("Café dnes", TranslationMemory.Key("Café   dnes "), "Unicode normalization (NFC)");
            Assert.Throws<ArgumentException>(() => third.Remember("  ", "x"), "an empty sentence");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public static void GlossaryFindsWholeTermsLongestFirst()
    {
        var glossary = new Glossary();
        glossary.Add("Harbor Lights");
        glossary.Add("Harbor");
        glossary.Add("The Night Desk", "The Night Desk");
        glossary.Add("Glass Orchard", "Sklenený sad");

        var matches = glossary.Find("Harbor Lights and The Night Desk beat Harbor, not Harborview or harbor lights; Glass Orchard!");
        Assert.Sequence(["Harbor Lights", "The Night Desk", "Harbor", "Glass Orchard"], matches.Select(m => m.Entry.Source), "terms found");
        Assert.Equal(0, matches[0].Start, "longest term at the start");
        Assert.Equal("Sklenený sad", matches[3].Entry.Output, "the wanted translation");
        Assert.Equal("Harbor", matches[2].Entry.Output, "kept as written");

        var path = Path.Combine(Path.GetTempPath(), $"iris-glossary-{Guid.NewGuid():N}.tsv");
        try
        {
            glossary.Save(path);
            File.AppendAllText(path, "# a comment\n\nSilver Meridian\t\n");
            var loaded = Glossary.Load(path);
            Assert.Equal(5, loaded.Count, "terms after loading");
            Assert.Equal("Sklenený sad", loaded.Find("Glass Orchard")[0].Entry.Output, "a translation after loading");
            Assert.Equal("Silver Meridian", loaded.Find("Silver Meridian")[0].Entry.Output, "an empty translation keeps the term");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public static void PlaceholdersComeBackOrTheSentenceIsLeftAlone()
    {
        var glossary = new Glossary();
        glossary.Add("Harbor Lights");
        glossary.Add("Glass Orchard", "Sklenený sad");
        const string sentence = "Harbor Lights and Glass Orchard open in 7301 cinemas.";
        var (hidden, marks) = Placeholders.Protect(sentence, glossary.Find(sentence));
        Assert.Equal("8401 and 8402 open in 7301 cinemas.", hidden, "numbers that are not in the sentence");

        Assert.True(Placeholders.TryRestore("8401 a 8402 sa premietajú v 7301 kinách.", marks, out var restored), "both back");
        Assert.Equal("Harbor Lights a Sklenený sad sa premietajú v 7301 kinách.", restored, "the terms in their wanted form");
        Assert.True(!Placeholders.TryRestore("8401 a sad sa premietajú.", marks, out var unchanged) && unchanged == "8401 a sad sa premietajú.", "a lost placeholder");
        Assert.True(!Placeholders.TryRestore("8401, 8401 a 8402.", marks, out _), "a doubled placeholder");
        Assert.True(!Placeholders.TryRestore("84012 a 8402.", marks, out _), "a placeholder inside a longer number does not count");
        Assert.Equal(0, Placeholders.Protect("No terms here.", []).Marks.Count, "nothing to hide");
    }

    [Test]
    public static void TranslatorUsesMemoryAndGlossary()
    {
        using var tiny = TinyModel.Create();
        var translator = Translator.Load(tiny.Path);
        var memory = TranslationMemory.InMemory();
        memory.Remember("the film premiere is tonight.", "Premiéra filmu je dnes večer.");
        var glossary = new Glossary();
        glossary.Add("night", "noc");

        var result = translator.TranslateText("the film premiere is tonight.\nthe night is new. New film", memory: memory, glossary: glossary);
        Assert.Equal(3, result.Sentences.Count, "three sentences");
        Assert.Equal(TranslationOrigin.Memory, result.Sentences[0].Origin, "the first from the memory");
        Assert.True(result.Text.StartsWith("Premiéra filmu je dnes večer.\n", StringComparison.Ordinal), "layout kept");
        var second = result.Sentences[1];
        Assert.True(second.Origin == TranslationOrigin.Glossary ? second.Target.Contains("noc", StringComparison.Ordinal) : second.Note is not null,
            $"the glossary was used, or the note says why not: {second.Origin} {second.Note}");
        Assert.Equal(TranslationOrigin.Model, result.Sentences[2].Origin, "no term, no memory: the model");
    }
}
