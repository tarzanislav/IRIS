"""The reference of the C# OCR learning (only in CI, PyTorch as a tool): one batch of rendered lines, the loss and the
gradients of every weight, one AdamW step after clipping – computed by PyTorch the way the C# trainer computes
(every line at its own width: convolutions per line, batch normalization over all pixels of all lines, a packed
LSTM, CTC per line) – and the time of a learning step, packed like C# and padded like train.py.

  reference_training.py OUT_DIR [--lines N] [--seed S] [--threads T] [--checkpoint checkpoint.pt]

Writes OUT_DIR/start.iris (kind iris-ocr-train: the weights before), OUT_DIR/batch.iris (the line pictures and their
texts) and OUT_DIR/expected.iris (loss, gradients, weights after the step, running statistics, times)."""
import argparse
import json
import random
import sys
import time
from pathlib import Path

import numpy as np
import torch
from torch import nn
from torch.nn.utils.rnn import pack_sequence, pad_packed_sequence

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "reference"))
import iris_file  # noqa: E402
from charset import CHARS, encode  # noqa: E402
from model import BLOCKS, Reader, config_for_iris  # noqa: E402
from render import render, to_model  # noqa: E402
from textgen import Texts  # noqa: E402
from train import collate, fits, fonts  # noqa: E402


def joint_norm(maps, norm):
    """Batch normalization of several lines together: statistics over all pixels of all lines (as C# does)."""
    sizes = [m.shape[2] * m.shape[3] for m in maps]
    flat = torch.cat([m.flatten(2) for m in maps], dim=2)  # [1, C, Σ pixels]
    normed = norm(flat.unsqueeze(-1)).squeeze(-1)
    return [part.reshape(m.shape) for part, m in zip(torch.split(normed, sizes, dim=2), maps)]


def packed_forward(model, pictures):
    """Logits of every line (a list), each line at its own width; pictures sorted from the widest."""
    maps = [p[None, None] for p in pictures]
    for (_, pool), conv, norm in zip(BLOCKS, model.convs, model.norms):
        maps = [torch.relu(m) for m in joint_norm([conv(m) for m in maps], norm)]
        if pool:
            maps = [nn.functional.max_pool2d(m, pool) for m in maps]
    maps = [torch.relu(m) for m in joint_norm([model.collapse(m) for m in maps], model.collapse_norm)]
    sequences = [m.squeeze(2).squeeze(0).transpose(0, 1) for m in maps]  # [frames, features]
    output, lengths = pad_packed_sequence(model.lstm(pack_sequence(sequences, enforce_sorted=True))[0], batch_first=True)
    return [model.output(output[i, :lengths[i]]) for i in range(len(sequences))]


def packed_loss(model, pictures, texts):
    logits = packed_forward(model, pictures)
    frames = max(l.shape[0] for l in logits)
    padded = torch.zeros(frames, len(logits), len(CHARS) + 1)
    for i, l in enumerate(logits):
        padded[:l.shape[0], i] = l
    labels = torch.tensor([c for t in texts for c in encode(t)], dtype=torch.long)
    ctc = nn.CTCLoss(blank=0, zero_infinity=True)
    return ctc(padded.log_softmax(-1), labels, torch.tensor([l.shape[0] for l in logits]),
               torch.tensor([len(t) for t in texts]))


def tensors(model):
    """The tensors of the iris-ocr-train file (names and shapes as C# keeps them)."""
    result = {}
    for i, (conv, norm) in enumerate(list(zip(model.convs, model.norms)) + [(model.collapse, model.collapse_norm)]):
        result[f"conv.{i}.w"] = conv.weight
        result[f"bn.{i}.gamma"] = norm.weight
        result[f"bn.{i}.beta"] = norm.bias
    for layer in range(model.lstm.num_layers):
        for direction, suffix in (("fw", ""), ("bw", "_reverse")):
            for mine, theirs in (("ih", "weight_ih"), ("hh", "weight_hh"), ("bih", "bias_ih"), ("bhh", "bias_hh")):
                result[f"lstm.{layer}.{direction}.{mine}"] = getattr(model.lstm, f"{theirs}_l{layer}{suffix}")
    result["out.w"] = model.output.weight
    result["out.b"] = model.output.bias
    return result


def statistics(model):
    result = {}
    for i, norm in enumerate(list(model.norms) + [model.collapse_norm]):
        result[f"bn.{i}.mean"] = norm.running_mean
        result[f"bn.{i}.var"] = norm.running_var
    return result


def numpy(t):
    return t.detach().float().numpy().copy()


def shape_config():
    config = config_for_iris(CHARS)
    config["state"] = {"step": 0, "minutes": 0, "plan": {"start": 0, "end": 0, "rate": 1e-3}, "adam_steps": 0}
    return config


def timed(step, repeats=3):
    step()  # the first one warms up
    start = time.perf_counter()
    for _ in range(repeats):
        step()
    return (time.perf_counter() - start) / repeats


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("out", type=Path)
    parser.add_argument("--lines", type=int, default=32)
    parser.add_argument("--seed", type=int, default=5)
    parser.add_argument("--threads", type=int, default=0)
    parser.add_argument("--checkpoint", type=Path)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    if args.threads:
        torch.set_num_threads(args.threads)
    torch.manual_seed(args.seed)

    learn, _ = fonts()
    r = random.Random(args.seed)
    source = Texts({}, args.seed)
    items = []
    while len(items) < args.lines:
        text = source.line()
        picture = to_model(render(text, learn, r))
        if fits(picture.shape[1], encode(text)):
            items.append((picture, text))
    items.sort(key=lambda item: -item[0].shape[1])
    pictures = [torch.from_numpy(p) for p, _ in items]
    texts = [t for _, t in items]

    model = Reader(len(CHARS) + 1)
    if args.checkpoint:
        model.load_state_dict(torch.load(args.checkpoint, weights_only=False)["model"])
    model.train()
    start = {k: numpy(v) for k, v in tensors(model).items()}
    start.update({k: numpy(v) for k, v in statistics(model).items()})
    for name in tensors(model):
        start[f"adam.m.{name}"] = np.zeros_like(start[name])
        start[f"adam.v.{name}"] = np.zeros_like(start[name])
    iris_file.write(args.out / "start.iris", "iris-ocr-train", shape_config(), {"source": "tools/ocr/reference_training.py"}, start, {})
    iris_file.write(args.out / "batch.iris", "iris-ocr-batch", {"texts": texts}, {}, {f"line.{i}": p for i, (p, _) in enumerate(items)}, {})

    # the reference: loss, gradients, running statistics, one step of AdamW after clipping
    optimizer = torch.optim.AdamW(model.parameters(), lr=1e-3, weight_decay=1e-4)
    optimizer.zero_grad()
    loss = packed_loss(model, pictures, texts)
    loss.backward()
    expected = {f"grad.{k}": numpy(v.grad) for k, v in tensors(model).items()}
    expected.update({f"stat.{k}": numpy(v) for k, v in statistics(model).items()})
    norm = nn.utils.clip_grad_norm_(model.parameters(), 5.0)
    optimizer.step()
    expected.update({f"after.{k}": numpy(v) for k, v in tensors(model).items()})

    # the time of a whole learning step: packed (as C#) and padded (as train.py learns)
    def packed_step():
        optimizer.zero_grad()
        packed_loss(model, pictures, texts).backward()
        nn.utils.clip_grad_norm_(model.parameters(), 5.0)
        optimizer.step()

    padded_batch = collate([(p, encode(t)) for p, t in items])
    ctc = nn.CTCLoss(blank=0, zero_infinity=True)

    def padded_step():
        optimizer.zero_grad()
        x, lengths, labels, label_lengths = padded_batch
        logits = model(x)
        ctc(logits.log_softmax(-1).transpose(0, 1), labels, lengths, label_lengths).backward()
        nn.utils.clip_grad_norm_(model.parameters(), 5.0)
        optimizer.step()

    times = {"packed": timed(packed_step), "padded": timed(padded_step)}
    info = {"loss": float(loss), "norm": float(norm), "threads": torch.get_num_threads(), "torch": torch.__version__,
            "seconds_packed": times["packed"], "seconds_padded": times["padded"],
            "pixels": int(sum(p.shape[1] for p in pictures) * 32), "padded_pixels": int(padded_batch[0].numel())}
    iris_file.write(args.out / "expected.iris", "iris-ocr-expected", info, {}, expected, {})
    print(json.dumps(info))


if __name__ == "__main__":
    main()
