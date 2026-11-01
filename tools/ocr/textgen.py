"""Texts for the lines the OCR learns to read: real Slovak, Czech and English sentences (invented sentences and their
translations by the OPUS-MT models of IRIS), made-up words that follow how SK/CS/EN words look (so the model learns
letters, not a vocabulary), and numbers, dates, prices and signs. Everything is generated – no text of anyone."""
import random

from charset import CHARS, clean

# letters by how often they appear (rough shares in running text, own estimates) – diacritics included
LETTERS = {
    "sk": "aaaaaaaaoooooooeeeeeeeiiiiinnnnnvvvvssssrrrrtttlllkkkddpppmmmuuucczzyjjbbháíéúýôäčďľňšťžĺŕ",
    "cs": "ooooooeeeeeeaaaaaanniiiitttvvssllrrkkdduupppmmcczzyjjbbhhěáíéúůýčďňřšťžó",
    "en": "eeeeeeeeettttttaaaaaooooooiiiiinnnnnssssshhhhrrrrdddlllcccuummwwffggyyppbbvkjxqz",
}
VOWELS = set("aáäeéěiíoóôuúůyý")
WORDS = {
    "sk": "a v na je sa že to s z do o pre ako by po aj ale ich tak pri od už len keď alebo ktorý svoje".split(),
    "cs": "a v na je se že to s z do o pro jako by po i ale jejich tak při od už jen když nebo který své".split(),
    "en": "the of and to in is that for it as was with be by on not he this are or his from at which".split(),
}
ABBREVIATIONS = ["s. r. o.", "a. s.", "napr.", "atď.", "tzn.", "č.", "str.", "obr.", "tab.", "kap.", "e.g.", "i.e.", "etc.",
                 "Dr.", "Ing.", "Mgr.", "PhDr.", "doc.", "prof.", "tj.", "resp.", "cca", "min.", "max.", "kol."]


def made_up_word(r: random.Random, language: str) -> str:
    letters = LETTERS[language]
    length = max(1, int(r.gauss(6, 2.5)))
    word = []
    vowel_next = r.random() < 0.4
    for _ in range(length):
        for _ in range(20):
            c = r.choice(letters)
            if (c in VOWELS) == vowel_next or r.random() < 0.25:
                break
        word.append(c)
        vowel_next = not vowel_next if r.random() < 0.8 else vowel_next
    return "".join(word)


def made_up_sentence(r: random.Random, language: str) -> str:
    words = []
    for _ in range(r.randint(3, 12)):
        words.append(r.choice(WORDS[language]) if r.random() < 0.3 else made_up_word(r, language))
        if r.random() < 0.08:
            words[-1] += r.choice([",", ",", ";", ":"])
    text = " ".join(words)
    text = text[0].upper() + text[1:]
    if r.random() < 0.15:
        opening, closing = r.choice([("„", "“"), ('"', '"'), ("(", ")"), ("‚", "‘")])
        text = opening + text + closing
    return text + r.choice([".", ".", ".", "?", "!", ":", "…", ""])


def numbers(r: random.Random) -> str:
    kinds = [
        lambda: f"{r.randint(1, 31)}. {r.randint(1, 12)}. {r.randint(1900, 2099)}",
        lambda: f"{r.randint(0, 99999):,}".replace(",", " ") + r.choice([" €", " Kč", " %", " km", " kg", " m²", "", " °C"]),
        lambda: f"{r.uniform(0, 1000):.{r.randint(1, 3)}f}".replace(".", r.choice([",", "."])),
        lambda: f"§ {r.randint(1, 600)} ods. {r.randint(1, 9)} písm. {r.choice('abcdef')})",
        lambda: f"{r.randint(1, 23):02d}:{r.randint(0, 59):02d}",
        lambda: f"+421 {r.randint(900, 999)} {r.randint(100, 999)} {r.randint(100, 999)}",
        lambda: f"ISBN 978-{r.randint(80, 99)}-{r.randint(100, 999)}-{r.randint(1000, 9999)}-{r.randint(0, 9)}",
        lambda: f"{r.randint(1, 20)}.{r.randint(1, 20)}.{r.randint(1, 20)} {r.choice(ABBREVIATIONS)}",
        lambda: f"({r.randint(1, 999)}) [{r.randint(1, 99)}] {r.choice(ABBREVIATIONS)} {r.randint(1, 500)}",
    ]
    return r.choice(kinds)()


def any_characters(r: random.Random) -> str:
    # every character of the charset now and then, also the rare ones
    return "".join(r.choice(CHARS) for _ in range(r.randint(4, 30))).strip() or "x"


class Texts:
    """A source of line texts. sentences: language → real sentences (may be empty)."""

    def __init__(self, sentences: dict, seed: int):
        self.sentences = {k: [clean(s) for s in v if clean(s)] for k, v in sentences.items()}
        self.r = random.Random(seed)

    def line(self, max_chars: int = 70) -> str:
        r = self.r
        language = r.choice(["sk", "sk", "cs", "en"])
        roll = r.random()
        real = self.sentences.get(language) or []
        if roll < 0.45 and real:
            text = r.choice(real)
        elif roll < 0.8:
            text = made_up_sentence(r, language)
        elif roll < 0.93:
            text = numbers(r) + (" " + made_up_sentence(r, language) if r.random() < 0.5 else "")
        else:
            text = any_characters(r)
        if r.random() < 0.06:
            text = text.upper()
        text = clean(text)
        if len(text) > max_chars:
            # a page line holds part of a sentence: cut at a word
            start = r.randint(0, len(text) - max_chars)
            text = text[start:start + max_chars]
            text = text[text.find(" ") + 1 if start and " " in text else 0:]
            text = text[:text.rfind(" ")] if " " in text[max_chars // 2:] else text
        return text.strip() or "a"
