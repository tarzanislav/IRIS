"""Builds the tokenizer test data of IRIS with the SentencePiece library (only here, never in the program):
three small unigram models trained on an invented corpus, and the pieces SentencePiece gives for tricky texts.
The C# tokenizer must give exactly the same pieces. Run: python tools/reference/make_tokenizer_testdata.py OUT_DIR
"""
import json
import random
import sys
from pathlib import Path

import sentencepiece as spm

out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
random.seed(7)

words = {
    "en": "the film premiere festival director actor actress series season episode trailer review story world city night "
          "first new great dark light summer winter studio release cinema audience critics award prize best award-winning".split(),
    "sk": "film premiéra festival režisér herec herečka seriál séria epizóda ukážka recenzia príbeh svet mesto noc "
          "prvý nový veľký tmavý svetlo leto zima štúdio uvedenie kino diváci kritici cena najlepší ocenený".split(),
    "cs": "film premiéra festival režisér herec herečka seriál řada díl ukázka recenze příběh svět město noc "
          "první nový velký temný světlo léto zima studio uvedení kino diváci kritici cena nejlepší oceněný".split(),
}
lines = []
for _ in range(3000):
    lang = random.choice(list(words))
    n = random.randint(4, 14)
    sentence = " ".join(random.choice(words[lang]) for _ in range(n))
    sentence = sentence[0].upper() + sentence[1:] + random.choice([".", "!", "?", ",", " (2026).", ": 9/10."])
    lines.append(sentence)
corpus = out / "corpus.txt"
corpus.write_text("\n".join(lines), encoding="utf-8")

# the nfkc model carries the compiled NFKC rules (about 240 kB); the others keep text as it is
models = {
    "nfkc": dict(vocab_size=200, normalization_rule_name="nmt_nfkc"),
    "bytes": dict(vocab_size=500, byte_fallback=True, normalization_rule_name="identity"),
    "user": dict(vocab_size=200, user_defined_symbols=["<film>", "IRIS"], normalization_rule_name="identity"),
}
for name, extra in models.items():
    spm.SentencePieceTrainer.train(input=str(corpus), model_prefix=str(out / name), model_type="unigram",
                                   character_coverage=0.98, **extra)
    (out / f"{name}.vocab").unlink()
corpus.unlink()

texts = [
    "The film premiere is tonight.",
    "  Leading,   repeated   and trailing spaces  ",
    "Premiéra filmu bude zajtra večer!",
    "Příběh: první řada, díl 1 (2026).",
    "Decomposed: é ǎ ů",
    "Fullｗidth ＡＢＣ １２３ and ﬁ ligature",
    "No break thin　ideographic space",
    "Emoji 🎬🍿 and symbols ™ © ½ ⅓",
    "Unknown 漢字 and Ελληνικά and עברית",
    "<film> IRIS user symbols <film>IRIS",
    "Tab\tand\nnew line",
    "",
    "   ",
    "a",
    "Quotes “double” ‘single’ «guillemets» — dash – dash … ellipsis",
    "Numbers 3.14 1,000,000 50% 9/10 #1 @user email@example.com https://example.com/a?b=c",
    "Mixed ČESKÝ a SLOVENSKÝ text: Ľúbostný príbeh, Ďaleký svet, Ťažká noc, Ňufák",
]
# random texts: words of the corpus mixed with characters from many scripts, spaces and symbols
alphabet = [chr(c) for c in list(range(0x20, 0x7F)) + list(range(0xA0, 0x180)) + list(range(0x300, 0x370)) + list(range(0x2000, 0x2070))
            + list(range(0xFF01, 0xFF5F)) + list(range(0x3000, 0x3010)) + [0x1F3AC, 0x1F37F, 0x4E00, 0x6F22, 0x5B57, 0xFB01, 0x2122]]
for _ in range(150):
    parts = []
    for _ in range(random.randint(1, 8)):
        if random.random() < 0.6:
            parts.append(random.choice(random.choice(list(words.values()))))
        else:
            parts.append("".join(random.choice(alphabet) for _ in range(random.randint(1, 5))))
    texts.append(random.choice(["", " ", "  "]).join(parts) if random.random() < 0.3 else " ".join(parts))

expected = {}
for name in models:
    sp = spm.SentencePieceProcessor(model_file=str(out / f"{name}.model"))
    expected[name] = [{"text": t, "pieces": sp.encode(t, out_type=str), "ids": sp.encode(t), "decoded": sp.decode(sp.encode(t))} for t in texts]
(out / "expected.json").write_text(json.dumps(expected, ensure_ascii=False, indent=1), encoding="utf-8")
print("ok", {name: (out / f"{name}.model").stat().st_size for name in models})
