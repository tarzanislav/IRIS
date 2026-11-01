"""Real sentences for the OCR lines: invented English sentences (tools/reference/sentences.py) and their Slovak and
Czech translations by the OPUS-MT models of IRIS (greedy, in batches). Only in CI.

  sentences.py OUT_DIR COUNT EN_SK_MODEL_DIR EN_CS_MODEL_DIR"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "reference"))
import sentences as invented  # noqa: E402


def translate(model_dir, texts, batch=32):
    import torch
    from transformers import MarianMTModel, MarianTokenizer

    model = MarianMTModel.from_pretrained(model_dir).eval()
    tokenizer = MarianTokenizer.from_pretrained(model_dir)
    out = []
    for start in range(0, len(texts), batch):
        encoded = tokenizer(texts[start:start + batch], return_tensors="pt", padding=True)
        with torch.no_grad():
            ids = model.generate(**encoded, num_beams=1, do_sample=False, max_new_tokens=128)
        out.extend(tokenizer.batch_decode(ids, skip_special_tokens=True))
    return out


def main():
    out, count, en_sk, en_cs = Path(sys.argv[1]), int(sys.argv[2]), sys.argv[3], sys.argv[4]
    out.mkdir(parents=True, exist_ok=True)
    english = invented.sentences(count, seed=4242)
    (out / "en.txt").write_text("\n".join(english) + "\n", encoding="utf-8")
    for language, model in (("sk", en_sk), ("cs", en_cs)):
        lines = [line.replace("\n", " ") for line in translate(model, english)]
        (out / f"{language}.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
        print(f"{language}: {len(lines)} sentences, e.g. {lines[100]}", flush=True)


if __name__ == "__main__":
    main()
