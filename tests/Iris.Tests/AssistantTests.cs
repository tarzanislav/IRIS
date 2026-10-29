using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Iris.Assistant;
using Iris.Assistant.Documents;
using Iris.Assistant.Packs;
using Iris.Assistant.Strings;
using Iris.Memory;
using Iris.Translation;

namespace Iris.Tests;

/// <summary>The logic of the IRIS application: packs, translation through English, files, history, settings, texts.</summary>
public static class AssistantTests
{
    [Test]
    public static void LanguagePairsAndCatalogsAreChecked()
    {
        Assert.Equal(new LanguagePair("en", "sk"), LanguagePair.Parse("en-sk"), "a pair");
        Assert.Equal("sk-en", LanguagePair.Parse("en-sk").Reversed.ToString(), "reversed");
        foreach (var bad in new[] { "en", "en-en", "EN-SK", "en-sk-cs", "e-sk", "../x" })
        {
            Assert.True(!LanguagePair.TryParse(bad, out _), $"not a pair: {bad}");
        }

        var sha = new string('a', 64);
        var catalog = PackCatalog.Parse($$"""{"format":1,"packs":[{"pair":"en-sk","file":"opus-mt-en-sk-int8.iris","size":5,"sha256":"{{sha}}","license":"apache-2.0","source":"x"}]}""");
        Assert.Equal(5L, catalog.Find(LanguagePair.Parse("en-sk"))!.Size, "an entry");
        Assert.True(catalog.Find(LanguagePair.Parse("en-cs")) is null, "a missing pair");
        Assert.Throws<FormatException>(() => PackCatalog.Parse($$"""{"format":1,"packs":[{"pair":"en-sk","file":"../evil.iris","size":5,"sha256":"{{sha}}","license":"","source":""}]}"""),
            "a file outside the models folder");
        Assert.Throws<FormatException>(() => PackCatalog.Parse("""{"format":1,"packs":[{"pair":"en-sk"}]}"""), "an incomplete entry");
        Assert.Throws<FormatException>(() => PackCatalog.Parse("not json"), "not JSON");
        Assert.Throws<FormatException>(() => PackCatalog.Parse("""{"format":2,"packs":[]}"""), "another format");
    }

    [Test]
    public static async Task PacksAreInstalledOnlyWhenTheyAreRight()
    {
        using var folder = new TempFolder();
        var store = new PackStore(Path.Combine(folder.Path, "models"));
        byte[] model = [1, 2, 3, 4, 5, 6, 7];
        var pack = new PackInfo(LanguagePair.Parse("en-sk"), "opus-mt-en-sk-int8.iris", model.Length, Sha(model), "apache-2.0", "test");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(pack with { Sha256 = new string('0', 64) }, new MemoryStream(model)), "a wrong SHA-256");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(pack with { Size = 3 }, new MemoryStream(model)), "a larger file");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(pack with { Size = 9 }, new MemoryStream(model)), "a shorter file");
        Assert.True(!store.IsInstalled(pack.Pair) && Directory.GetFiles(store.Folder).Length == 0, "nothing is left behind");

        var reports = new List<InstallProgress>();
        await store.InstallAsync(pack, new MemoryStream(model), new Immediate<InstallProgress>(reports.Add));
        Assert.Sequence(model, File.ReadAllBytes(store.PathOf(pack.Pair)), "installed as en-sk.iris");
        Assert.Equal(new InstallProgress(7, 7), reports[^1], "progress up to the whole file");

        // a download from the release folder
        using var http = new HttpClient(new Serve(model));
        var cs = pack with { Pair = LanguagePair.Parse("en-cs"), File = "opus-mt-en-cs-int8.iris" };
        await store.InstallAsync(cs, http, new Uri("https://example.invalid/releases/download/v0.3.0/"));
        Assert.Sequence(["en-cs", "en-sk"], store.Installed().Select(p => p.ToString()), "both installed");

        Assert.True(store.Remove(cs.Pair) && !store.Remove(cs.Pair), "removed once");
    }

    [Test]
    public static void RoutesGoThroughEnglishWhenNeeded()
    {
        using var folder = new TempFolder();
        var store = new PackStore(folder.Path);
        foreach (var pair in new[] { "sk-en", "en-cs", "en-sk" })
        {
            File.WriteAllBytes(store.PathOf(LanguagePair.Parse(pair)), [0]);
        }

        Assert.Sequence(["en-sk"], store.Route("en", "sk")!.Select(p => p.ToString()), "direct");
        Assert.Sequence(["sk-en", "en-cs"], store.Route("sk", "cs")!.Select(p => p.ToString()), "through English");
        Assert.True(store.Route("cs", "sk") is null, "cs-en is missing");
        Assert.True(store.Route("cs", "en") is null, "English has no pivot");
    }

    [Test]
    public static void ServiceUsesMemoryGlossaryAndPivot()
    {
        using var folder = new TempFolder();
        using var tiny = TinyModel.Create();
        var store = new PackStore(folder.Path);
        foreach (var pair in new[] { "de-en", "en-sk" })
        {
            File.Copy(tiny.Path, store.PathOf(LanguagePair.Parse(pair)));
        }

        var memories = new Dictionary<LanguagePair, TranslationMemory>();
        var service = new TranslationService(store, pair => memories[pair] = TranslationMemory.InMemory(), _ => new Glossary());
        const string text = "the film premiere is tonight.\n\nnight";
        var direct = service.Translate(text, "en", "sk", beams: 1);
        Assert.True(!direct.ThroughPivot && direct.Sentences.Count == 2, "two sentences, direct");
        Assert.True(direct.Text.Contains("\n\n", StringComparison.Ordinal), "the paragraph break stays");

        var pivot = service.Translate(text, "de", "sk", beams: 1);
        Assert.True(pivot.ThroughPivot && pivot.Sentences.All(s => s.Note?.Contains("through English", StringComparison.Ordinal) == true),
            "through English, with a note");

        service.MemoryFor(LanguagePair.Parse("de-sk")).Remember("night", "noc");
        var remembered = service.Translate(text, "de", "sk", beams: 1);
        Assert.Equal(TranslationOrigin.Memory, remembered.Sentences[1].Origin, "the correction of de → sk wins");
        Assert.Equal("noc", remembered.Sentences[1].Target, "with its text");
        Assert.Throws<InvalidOperationException>(() => service.Translate(text, "sk", "en"), "no pack for sk → en");
    }

    [Test]
    public static void SubtitlesKeepNumbersAndTimes()
    {
        const string srt = "﻿1\r\n00:00:01,000 --> 00:00:03,500\r\nThe premiere\r\nis tonight.\r\n\r\n2\n00:00:04,000 --> 00:00:06,000\n<i>Where is the nearest cinema?</i>\n\n\n3\n00:00:07,000 --> 00:00:08,000\n\n";
        var subtitles = Subtitles.Parse(srt);
        Assert.Equal(3, subtitles.Count, "three subtitles, even an empty one");
        Assert.Equal("00:00:01,000 --> 00:00:03,500", subtitles[0].Time, "times as written");
        Assert.Equal(("The premiere is tonight.", false), Subtitles.TextOf(subtitles[0]), "lines joined");
        Assert.Equal(("Where is the nearest cinema?", true), Subtitles.TextOf(subtitles[1]), "italics noted, tags removed");
        Assert.Sequence(["Kde je najbližšie kino?"], Subtitles.Lines("Kde je najbližšie kino?", false), "a short line stays one");
        var two = Subtitles.Lines("Kritici chválili hudbu, ale koniec sa im zdal príliš pomalý.", true);
        Assert.True(two.Count == 2 && two.All(l => l.StartsWith("<i>", StringComparison.Ordinal)), "a long line in two, italics kept");
        Assert.Equal(subtitles.Count, Subtitles.Parse(Subtitles.Write(subtitles)).Count, "written and read back");
        Assert.Throws<FormatException>(() => Subtitles.Parse("hello\nworld"), "not subtitles");
    }

    [Test]
    public static void TextFilesKeepTheirEncoding()
    {
        Assert.Equal("Čaj", TextFiles.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Čaj")]).Text, "UTF-8 with BOM");
        Assert.Equal("Čaj", TextFiles.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Čaj")]).Text, "UTF-16");
        Assert.Equal("Čaj", TextFiles.Decode(Encoding.UTF8.GetBytes("Čaj")).Text, "UTF-8");
        Assert.Equal("Čaj šťava", TextFiles.Decode([0xC8, 0x61, 0x6A, 0x20, 0x9A, 0x9D, 0x61, 0x76, 0x61]).Text, "Windows-1250");
    }

    [Test]
    public static void FilesAreTranslatedIntoNewFiles()
    {
        using var folder = new TempFolder();
        using var tiny = TinyModel.Create();
        var store = new PackStore(Path.Combine(folder.Path, "models"));
        Directory.CreateDirectory(store.Folder);
        File.Copy(tiny.Path, store.PathOf(LanguagePair.Parse("en-sk")));
        var service = new TranslationService(store, _ => TranslationMemory.InMemory(), _ => new Glossary());

        var txt = Path.Combine(folder.Path, "notes.txt");
        File.WriteAllText(txt, "the film premiere is tonight.\nnight");
        var before = Sha(File.ReadAllBytes(txt));
        var translated = DocumentTranslator.Translate(service, txt, "en", "sk", beams: 1);
        Assert.Equal(Path.Combine(folder.Path, "notes.sk.txt"), translated, "a new file next to it");
        Assert.Equal(2, File.ReadAllLines(translated).Length, "two lines stay two");
        Assert.Equal(before, Sha(File.ReadAllBytes(txt)), "the original is unchanged");
        Assert.Equal(Path.Combine(folder.Path, "notes.sk (2).txt"), DocumentTranslator.Translate(service, txt, "en", "sk", beams: 1), "never overwritten");

        var srt = Path.Combine(folder.Path, "film.en.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:02,000\nthe premiere\n\n2\n00:00:03,000 --> 00:00:04,500\nnight\n");
        var subtitles = Subtitles.Parse(File.ReadAllText(DocumentTranslator.Translate(service, srt, "en", "sk", beams: 1)));
        Assert.Equal(Path.Combine(folder.Path, "film.sk.srt"), DocumentTranslator.TargetPath(srt, "en", "sk").Replace(" (2)", "", StringComparison.Ordinal), "the language in the name is replaced");
        Assert.Sequence(["00:00:01,000 --> 00:00:02,000", "00:00:03,000 --> 00:00:04,500"], subtitles.Select(s => s.Time), "times kept");

        var docx = Path.Combine(folder.Path, "article.docx");
        WriteDocx(docx);
        var copy = DocumentTranslator.Translate(service, docx, "en", "sk", beams: 1);
        using (var archive = ZipFile.OpenRead(copy))
        {
            Assert.Sequence(["[Content_Types].xml", "docProps/app.xml", "word/document.xml"], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal), "the same parts");
            using var stream = archive.GetEntry("word/document.xml")!.Open();
            var w = (XNamespace)"http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var paragraphs = XDocument.Load(stream).Descendants(w + "p").ToList();
            Assert.Equal(3, paragraphs.Count, "paragraphs kept");
            Assert.True(paragraphs[1].Descendants(w + "b").Any(), "run properties kept");
            Assert.True(paragraphs[1].Descendants(w + "t").Skip(1).All(t => t.Value.Length == 0), "the translation is in the first run");
            Assert.Equal("", string.Concat(paragraphs[2].Descendants(w + "t").Select(t => t.Value)), "an empty paragraph stays empty");
        }

        using (var archive = ZipFile.OpenRead(copy))
        using (var original = ZipFile.OpenRead(docx))
        {
            using var a = archive.GetEntry("docProps/app.xml")!.Open();
            using var b = original.GetEntry("docProps/app.xml")!.Open();
            Assert.Equal(new StreamReader(b).ReadToEnd(), new StreamReader(a).ReadToEnd(), "other parts are copied unchanged");
        }

        Assert.Throws<NotSupportedException>(() => DocumentTranslator.Translate(service, Path.Combine(folder.Path, "x.pdf"), "en", "sk"), "an unknown kind");
        Assert.True(!Directory.GetFiles(folder.Path).Any(f => f.EndsWith(".part", StringComparison.Ordinal)), "no temporary files are left");
        Assert.Equal(7, Directory.GetFiles(folder.Path).Length, "3 originals and 4 translations");
    }

    [Test]
    public static void HistorySettingsAndUserData()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "history.jsonl");
        var history = History.Open(path);
        history.Add("en-sk", "The premiere is tonight.", "Premiéra je dnes večer.");
        history.Add("en-cs", "Tickets are sold out.", "Vstupenky jsou vyprodány.");
        File.AppendAllText(path, "{\"time\": \"cut off");
        var reopened = History.Open(path);
        Assert.Sequence(["en-cs", "en-sk"], reopened.Entries().Select(e => e.Pair), "newest first, a cut-off line skipped");
        Assert.Equal(1, reopened.Entries("premiéra").Count, "search ignores case");
        for (var i = 0; i < History.Limit + 10; i++)
        {
            reopened.Add("en-sk", $"Sentence {i}.", $"Veta {i}.");
        }

        Assert.Equal(History.Limit, History.Open(path).Entries().Count, "at most the limit");
        reopened.Clear();
        Assert.True(!File.Exists(path) && reopened.Entries().Count == 0, "cleared");

        var settingsPath = Path.Combine(folder.Path, "settings.json");
        Assert.Equal(new Settings(), Settings.Load(settingsPath), "defaults without a file");
        File.WriteAllText(settingsPath, "{ not json");
        Assert.Equal(new Settings(), Settings.Load(settingsPath), "defaults from a damaged file");
        File.WriteAllText(settingsPath, """{"InterfaceLanguage":"de","SourceLanguage":"sk","TargetLanguage":"sk","Theme":"Dark"}""");
        var odd = Settings.Load(settingsPath);
        Assert.True(odd.InterfaceLanguage is null && odd.SourceLanguage == "en" && odd.TargetLanguage == "sk", "values out of range fall back");
        Assert.Equal(Theme.Dark, odd.Theme, "a good value is kept");
        var mine = new Settings { InterfaceLanguage = "cs", Theme = Theme.Light, SourceLanguage = "sk", TargetLanguage = "en", Quick = true };
        mine.Save(settingsPath);
        Assert.Equal(mine, Settings.Load(settingsPath), "saved and read back");

        Directory.CreateDirectory(Path.Combine(folder.Path, "models"));
        File.WriteAllText(Path.Combine(folder.Path, "models", "en-sk.iris"), "model");
        File.WriteAllText(Path.Combine(folder.Path, "memory-en-sk.jsonl"), "{}");
        File.WriteAllText(Path.Combine(folder.Path, "glossary-en-sk.tsv"), "Harbor Lights");
        File.WriteAllText(Path.Combine(folder.Path, "notes.txt"), "not IRIS data");
        Assert.Equal(3, UserData.DeleteAll(folder.Path), "memory, glossary and settings deleted");
        Assert.True(File.Exists(Path.Combine(folder.Path, "models", "en-sk.iris")) && File.Exists(Path.Combine(folder.Path, "notes.txt")),
            "language packs and other files stay");
    }

    [Test]
    public static void InterfaceTextsAreCompleteInEveryLanguage()
    {
        var english = Texts.For("en");
        var keys = english.Keys.Order(StringComparer.Ordinal).ToList();
        foreach (var language in Texts.Languages)
        {
            var texts = Texts.For(language);
            Assert.Sequence(keys, texts.Keys.Order(StringComparer.Ordinal), $"the same keys in {language}");
            foreach (var key in keys)
            {
                Assert.True(texts[key].Trim().Length > 0, $"{language}: {key} has text");
                Assert.Equal(Placeholders(english[key]), Placeholders(texts[key]), $"{language}: {key} has the same {{n}} places");
            }
        }

        Assert.Equal("Preklad", Texts.For("sk")["nav.translate"], "Slovak");
        Assert.Equal("Překlad", Texts.For("cs")["nav.translate"], "Czech");
        Assert.Equal("Translate", Texts.For("de")["nav.translate"], "another language falls back to English");
        Assert.Equal("missing.key", Texts.For("sk")["missing.key"], "a missing key shows itself");
        Assert.Equal("Oprava vety 2 z 4", Texts.For("sk").Format("correct.title", 2, 4), "values in their places");

        static string Placeholders(string text) => string.Join(",", Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // a minimal Word document: a heading, a paragraph of two runs (one bold) and an empty paragraph
    private static void WriteDocx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string xml)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        Part("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>""");
        Part("docProps/app.xml", """<?xml version="1.0" encoding="UTF-8"?><Properties><Application>Test</Application></Properties>""");
        Part("word/document.xml", """<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>the premiere</w:t></w:r></w:p><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>the film </w:t></w:r><w:r><w:t xml:space="preserve">premiere is tonight.</w:t></w:r></w:p><w:p/></w:body></w:document>""");
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"iris-app-{Guid.NewGuid():N}");

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class Immediate<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class Serve(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
