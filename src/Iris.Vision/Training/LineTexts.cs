using System.Globalization;
using System.Text;

namespace Iris.Vision.Training;

/// <summary>
/// Texts for the lines the OCR learns to read (the same recipe as tools/ocr/textgen.py): real Slovak, Czech and
/// English sentences when there are some (invented sentences and their translations by IRIS), made-up words that
/// look like SK/CS/EN words (so the reader learns letters, not a vocabulary), and numbers, dates, prices and signs.
/// Everything is generated – no text of anyone.
/// </summary>
public sealed class LineTexts
{
    private static readonly Dictionary<string, string> Letters = new(StringComparer.Ordinal)
    {
        ["sk"] = "aaaaaaaaoooooooeeeeeeeiiiiinnnnnvvvvssssrrrrtttlllkkkddpppmmmuuucczzyjjbbháíéúýôäčďľňšťžĺŕ",
        ["cs"] = "ooooooeeeeeeaaaaaanniiiitttvvssllrrkkdduupppmmcczzyjjbbhhěáíéúůýčďňřšťžó",
        ["en"] = "eeeeeeeeettttttaaaaaooooooiiiiinnnnnssssshhhhrrrrdddlllcccuummwwffggyyppbbvkjxqz",
    };

    private static readonly Dictionary<string, string[]> Words = new(StringComparer.Ordinal)
    {
        ["sk"] = "a v na je sa že to s z do o pre ako by po aj ale ich tak pri od už len keď alebo ktorý svoje".Split(' '),
        ["cs"] = "a v na je se že to s z do o pro jako by po i ale jejich tak při od už jen když nebo který své".Split(' '),
        ["en"] = "the of and to in is that for it as was with be by on not he this are or his from at which".Split(' '),
    };

    private static readonly string[] Abbreviations =
    [
        "s. r. o.", "a. s.", "napr.", "atď.", "tzn.", "č.", "str.", "obr.", "tab.", "kap.", "e.g.", "i.e.", "etc.",
        "Dr.", "Ing.", "Mgr.", "PhDr.", "doc.", "prof.", "tj.", "resp.", "cca", "min.", "max.", "kol.",
    ];

    private static readonly string[] Languages = ["sk", "sk", "cs", "en"];
    private const string Vowels = "aáäeéěiíoóôuúůyý";

    private readonly OcrShape _shape;
    private readonly Dictionary<string, string[]> _sentences;
    private readonly Random _r;

    /// <param name="sentences">language ("sk", "cs", "en") → real sentences (may be empty)</param>
    public LineTexts(OcrShape shape, IReadOnlyDictionary<string, string[]> sentences, int seed)
    {
        _shape = shape;
        _sentences = sentences.ToDictionary(p => p.Key, p => p.Value.Select(shape.Clean).Where(s => s.Length > 0).ToArray(), StringComparer.Ordinal);
        _r = new Random(seed);
    }

    /// <summary>Sentences from a folder with sk.txt, cs.txt and en.txt (one per line), as tools/ocr/sentences.py writes them.</summary>
    public static Dictionary<string, string[]> ReadSentences(string? folder)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (folder is null)
        {
            return result;
        }

        foreach (var language in new[] { "sk", "cs", "en" })
        {
            var path = Path.Combine(folder, language + ".txt");
            if (File.Exists(path))
            {
                result[language] = [.. File.ReadAllLines(path, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0)];
            }
        }

        return result;
    }

    /// <summary>The next line of text (at most maxChars characters; a long sentence is cut at words, as a page line holds part of it).</summary>
    public string Next(int maxChars = 70)
    {
        var r = _r;
        var language = Languages[r.Next(Languages.Length)];
        var roll = r.NextDouble();
        var real = _sentences.GetValueOrDefault(language) ?? [];
        string text;
        if (roll < 0.45 && real.Length > 0)
        {
            text = real[r.Next(real.Length)];
        }
        else if (roll < 0.8)
        {
            text = MadeUpSentence(language);
        }
        else if (roll < 0.93)
        {
            text = Numbers() + (r.NextDouble() < 0.5 ? " " + MadeUpSentence(language) : "");
        }
        else
        {
            text = AnyCharacters();
        }

        if (r.NextDouble() < 0.06)
        {
            text = text.ToUpper(CultureInfo.InvariantCulture);
        }

        text = _shape.Clean(text);
        if (text.Length > maxChars)
        {
            var start = r.Next(text.Length - maxChars + 1);
            text = text.Substring(start, maxChars);
            if (start > 0 && text.Contains(' '))
            {
                text = text[(text.IndexOf(' ') + 1)..];
            }

            if (text.Length > maxChars / 2 && text.IndexOf(' ', maxChars / 2) >= 0)
            {
                text = text[..text.LastIndexOf(' ')];
            }
        }

        text = text.Trim();
        return text.Length > 0 ? text : "a";
    }

    private string MadeUpWord(string language)
    {
        var letters = Letters[language];
        var length = Math.Max(1, (int)Gauss(6, 2.5));
        var word = new StringBuilder(length);
        var vowelNext = _r.NextDouble() < 0.4;
        for (var i = 0; i < length; i++)
        {
            var c = letters[0];
            for (var tries = 0; tries < 20; tries++)
            {
                c = letters[_r.Next(letters.Length)];
                if (Vowels.Contains(c) == vowelNext || _r.NextDouble() < 0.25)
                {
                    break;
                }
            }

            word.Append(c);
            vowelNext = _r.NextDouble() < 0.8 ? !vowelNext : vowelNext;
        }

        return word.ToString();
    }

    private string MadeUpSentence(string language)
    {
        var words = new List<string>();
        var count = _r.Next(3, 13);
        for (var i = 0; i < count; i++)
        {
            var word = _r.NextDouble() < 0.3 ? Words[language][_r.Next(Words[language].Length)] : MadeUpWord(language);
            if (_r.NextDouble() < 0.08)
            {
                word += new[] { ",", ",", ";", ":" }[_r.Next(4)];
            }

            words.Add(word);
        }

        var text = string.Join(' ', words);
        text = char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
        if (_r.NextDouble() < 0.15)
        {
            var (opening, closing) = new[] { ("„", "“"), ("\"", "\""), ("(", ")"), ("‚", "‘") }[_r.Next(4)];
            text = opening + text + closing;
        }

        return text + new[] { ".", ".", ".", "?", "!", ":", "…", "" }[_r.Next(8)];
    }

    private string Numbers()
    {
        var r = _r;
        var c = CultureInfo.InvariantCulture;
        switch (r.Next(9))
        {
            case 0:
                return $"{r.Next(1, 32)}. {r.Next(1, 13)}. {r.Next(1900, 2100)}";
            case 1:
                return r.Next(0, 100000).ToString("#,0", c).Replace(',', ' ') + new[] { " €", " Kč", " %", " km", " kg", " m²", "", " °C" }[r.Next(8)];
            case 2:
                return (r.NextDouble() * 1000).ToString("F" + r.Next(1, 4), c).Replace('.', r.NextDouble() < 0.5 ? ',' : '.');
            case 3:
                return $"§ {r.Next(1, 601)} ods. {r.Next(1, 10)} písm. {"abcdef"[r.Next(6)]})";
            case 4:
                return $"{r.Next(1, 24):00}:{r.Next(0, 60):00}";
            case 5:
                return $"+421 {r.Next(900, 1000)} {r.Next(100, 1000)} {r.Next(100, 1000)}";
            case 6:
                return $"ISBN 978-{r.Next(80, 100)}-{r.Next(100, 1000)}-{r.Next(1000, 10000)}-{r.Next(0, 10)}";
            case 7:
                return $"{r.Next(1, 21)}.{r.Next(1, 21)}.{r.Next(1, 21)} {Abbreviations[r.Next(Abbreviations.Length)]}";
            default:
                return $"({r.Next(1, 1000)}) [{r.Next(1, 100)}] {Abbreviations[r.Next(Abbreviations.Length)]} {r.Next(1, 501)}";
        }
    }

    private string AnyCharacters()
    {
        var length = _r.Next(4, 31);
        var text = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            text.Append(_shape.Charset[_r.Next(_shape.Charset.Length)]);
        }

        var result = text.ToString().Trim();
        return result.Length > 0 ? result : "x";
    }

    private double Gauss(double mean, double sigma) =>
        mean + sigma * Math.Sqrt(-2 * Math.Log(1 - _r.NextDouble())) * Math.Cos(2 * Math.PI * _r.NextDouble());
}
