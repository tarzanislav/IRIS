"""Teaches the IRIS line reader (model.py) on rendered lines (render.py, textgen.py) – only in CI, PyTorch as a tool.

  train.py OUT_DIR --minutes M [--resume checkpoint.pt] [--sentences DIR] [--small]

Learns until the time is up (the learning rate falls along the time), measures the character error rate (CER) on a
fixed set of lines – fonts it learned on and one family it never saw – keeps a checkpoint, and at the end writes the
IRIS model file (OUT_DIR/iris-ocr.iris) with test lines and the outputs C# must repeat (OUT_DIR/reference/)."""
import argparse
import json
import math
import random
import sys
import time
from pathlib import Path

import numpy as np
import torch
from torch import nn

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "reference"))
import iris_file  # noqa: E402
from charset import CHARS, encode  # noqa: E402
from model import Reader, config_for_iris, tensors_for_iris  # noqa: E402
from render import HEIGHT, find_fonts, render, to_model  # noqa: E402
from textgen import Texts  # noqa: E402

# families whose licences allow everything (SIL OFL, Bitstream Vera / DejaVu, GUST, Apache): only these are used
FAMILIES = ("DejaVu", "Liberation", "NotoSans-", "NotoSerif-", "texgyre", "Carlito", "Caladea", "LinLibertine", "LinBiolinum")
UNSEEN = "Caladea"  # never learned from: measures how the reader copes with a typeface it does not know
FOLDERS = ("/usr/share/fonts", "/usr/share/texmf/fonts/opentype/public/tex-gyre")


def fonts():
    found = [f for f in find_fonts(FOLDERS) if any(Path(f).name.startswith(p) or p in Path(f).name for p in FAMILIES)]
    found = [f for f in found if "Math" not in f and "Display" not in f]
    learn = [f for f in found if UNSEEN not in f]
    unseen = [f for f in found if UNSEEN in f]
    if not learn:
        raise SystemExit("no usable font found – install the font packages (see the workflow)")
    return learn, unseen


def sentences(folder):
    result = {}
    if folder:
        for language in ("sk", "cs", "en"):
            path = Path(folder) / f"{language}.txt"
            if path.exists():
                result[language] = [line.strip() for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    return result


class Lines(torch.utils.data.IterableDataset):
    def __init__(self, fonts, texts, seed):
        self.fonts, self.texts, self.seed = fonts, texts, seed

    def __iter__(self):
        info = torch.utils.data.get_worker_info()
        worker = info.id if info else 0
        r = random.Random(self.seed * 1000 + worker)
        texts = Texts(self.texts, self.seed * 1000 + worker)
        while True:
            text = texts.line()
            picture = to_model(render(text, self.fonts, r))
            labels = encode(text)
            if fits(picture.shape[1], labels):
                yield picture, labels


def fits(width, labels):
    """CTC needs a frame for every character and one more between two equal neighbours."""
    repeats = sum(1 for a, b in zip(labels, labels[1:]) if a == b)
    return width // 4 >= len(labels) + repeats and width <= 1600


def batches(dataset, size, workers):
    loader = torch.utils.data.DataLoader(dataset, batch_size=None, num_workers=workers, prefetch_factor=64 if workers else None)
    items = []
    for item in loader:
        items.append(item)
        if len(items) == size:
            yield collate(items)
            items = []


def collate(items):
    width = max(p.shape[1] for p, _ in items)
    width += -width % 4
    pictures = np.zeros((len(items), 1, HEIGHT, width), dtype=np.float32)
    for i, (p, _) in enumerate(items):
        pictures[i, 0, :, :p.shape[1]] = p
    labels = torch.tensor([c for _, l in items for c in l], dtype=torch.long)
    return (torch.from_numpy(pictures), torch.tensor([p.shape[1] // 4 for p, _ in items]),
            labels, torch.tensor([len(l) for _, l in items]))


def decode(logits_row, length):
    ids = logits_row[:length].argmax(-1).tolist()
    out, last = [], 0
    for i in ids:
        if i != last and i != 0:
            out.append(CHARS[i - 1])
        last = i
    return "".join(out)


def distance(a, b):
    previous = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        current = [i]
        for j, cb in enumerate(b, 1):
            current.append(min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (ca != cb)))
        previous = current
    return previous[-1]


def fixed_set(fonts, texts, seed, count):
    r = random.Random(seed)
    source = Texts(texts, seed)
    items = []
    while len(items) < count:
        text = source.line()
        picture = to_model(render(text, fonts, r))
        if fits(picture.shape[1], encode(text)):
            items.append((picture, text))
    return items


def cer(model, items):
    model.eval()
    errors = chars = 0
    with torch.no_grad():
        for start in range(0, len(items), 32):
            part = items[start:start + 32]
            pictures, lengths, _, _ = collate([(p, encode(t)) for p, t in part])
            logits = model(pictures)
            for i, (_, text) in enumerate(part):
                errors += distance(decode(logits[i], lengths[i]), text)
                chars += len(text)
    model.train()
    return 100.0 * errors / max(1, chars)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("out", type=Path)
    parser.add_argument("--minutes", type=float, default=300)
    parser.add_argument("--resume", type=Path)
    parser.add_argument("--sentences", type=Path)
    parser.add_argument("--batch", type=int, default=32)
    parser.add_argument("--rate", type=float, default=1e-3)
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--eval", type=int, default=600)
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    torch.manual_seed(args.seed)

    learn_fonts, unseen_fonts = fonts()
    texts = sentences(args.sentences)
    print(f"{len(learn_fonts)} fonts to learn from, {len(unseen_fonts)} unseen; sentences: "
          f"{ {k: len(v) for k, v in texts.items()} }", flush=True)
    model = Reader(len(CHARS) + 1)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.rate, weight_decay=1e-4)
    step, spent = 0, 0.0
    if args.resume and args.resume.exists():
        state = torch.load(args.resume, weights_only=False)
        model.load_state_dict(state["model"])
        optimizer.load_state_dict(state["optimizer"])
        step, spent = state["step"], state["minutes"]
        print(f"resumed at step {step} after {spent:.0f} minutes", flush=True)

    total = spent + args.minutes
    known = fixed_set(learn_fonts, texts, 7777, args.eval)
    unseen = fixed_set(unseen_fonts, texts, 8888, args.eval // 2) if unseen_fonts else []
    ctc = nn.CTCLoss(blank=0, zero_infinity=True)
    start = time.time()
    last_eval = start
    history = []
    model.train()
    for pictures, lengths, labels, label_lengths in batches(Lines(learn_fonts, texts, args.seed + step), args.batch, args.workers):
        elapsed = spent + (time.time() - start) / 60
        if elapsed >= total:
            break
        # warm up for the first 500 steps, then a cosine fall along the time
        rate = args.rate * min(1.0, (step + 1) / 500) * (0.02 + 0.98 * 0.5 * (1 + math.cos(math.pi * elapsed / total)))
        for group in optimizer.param_groups:
            group["lr"] = rate
        logits = model(pictures)
        loss = ctc(logits.log_softmax(-1).transpose(0, 1), labels, lengths, label_lengths)
        optimizer.zero_grad()
        loss.backward()
        nn.utils.clip_grad_norm_(model.parameters(), 5.0)
        optimizer.step()
        step += 1
        if step % 200 == 0:
            print(f"step {step}, {elapsed:.0f} min, loss {loss.item():.3f}, rate {rate:.2e}", flush=True)
        if time.time() - last_eval > 20 * 60:
            last_eval = time.time()
            history.append({"step": step, "minutes": round(elapsed), "cer_known": round(cer(model, known), 2),
                            "cer_unseen": round(cer(model, unseen), 2) if unseen else None})
            print(f"CER {history[-1]}", flush=True)
            torch.save({"model": model.state_dict(), "optimizer": optimizer.state_dict(), "step": step,
                        "minutes": elapsed}, args.out / "checkpoint.pt")

    elapsed = spent + (time.time() - start) / 60
    torch.save({"model": model.state_dict(), "optimizer": optimizer.state_dict(), "step": step, "minutes": elapsed},
               args.out / "checkpoint.pt")
    result = {"step": step, "minutes": round(elapsed), "cer_known": round(cer(model, known), 2),
              "cer_unseen": round(cer(model, unseen), 2) if unseen else None,
              "fonts": [Path(f).name for f in learn_fonts], "unseen_fonts": [Path(f).name for f in unseen_fonts],
              "parameters": sum(p.numel() for p in model.parameters()), "history": history}
    (args.out / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"done: {json.dumps({k: v for k, v in result.items() if k not in ('fonts', 'history')})}", flush=True)
    export(model, args.out, known[:40], result)


def export(model, out, lines, result):
    """The IRIS model file and the reference: test lines as PNG and what the PyTorch model gives for them."""
    import torch as t
    from PIL import Image

    model.eval()
    provenance = {"source": "IRIS OCR, learned on rendered lines (tools/ocr)", "step": result["step"],
                  "minutes": result["minutes"], "cer_known": result["cer_known"], "cer_unseen": result["cer_unseen"],
                  "torch": t.__version__}
    iris_file.write(out / "iris-ocr.iris", "iris-ocr", config_for_iris(CHARS), provenance, tensors_for_iris(model), {})
    folder = out / "reference"
    folder.mkdir(exist_ok=True)
    items = []
    with t.no_grad():
        for i, (picture, text) in enumerate(lines):
            gray = np.clip(np.rint((1.0 - picture) * 255), 0, 255).astype(np.uint8)
            Image.fromarray(gray).save(folder / f"line-{i:03d}.png")
            # the reference sees exactly what C# will see: the PNG read back
            again = 1.0 - np.asarray(Image.open(folder / f"line-{i:03d}.png"), dtype=np.float32) / 255.0
            logits = model(t.from_numpy(again)[None, None])[0]
            items.append({"file": f"line-{i:03d}.png", "text": text, "read": decode(logits, logits.shape[0]),
                          "frames": logits.shape[0], "best": logits.argmax(-1).tolist(),
                          "logits_first": logits[0].tolist(), "logits_middle": logits[logits.shape[0] // 2].tolist()})
    (folder / "reference.json").write_text(json.dumps({"format": 1, "lines": items}, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
