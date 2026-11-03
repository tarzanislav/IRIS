"""A checkpoint of tools/ocr/train.py (PyTorch) as an IRIS OCR learning file (kind iris-ocr-train), so the C# trainer
goes on where PyTorch stopped: weights with the batch normalization not folded, its running statistics, the moments
of AdamW and the state of the plan. Only in CI, PyTorch as a tool.

  to_training_file.py checkpoint.pt iris-ocr-train.iris"""
import sys
from pathlib import Path

import torch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "reference"))
import iris_file  # noqa: E402
from charset import CHARS  # noqa: E402
from model import Reader, config_for_iris  # noqa: E402
from reference_training import numpy, statistics, tensors  # noqa: E402


def main():
    source, target = Path(sys.argv[1]), Path(sys.argv[2])
    state = torch.load(source, weights_only=False)
    model = Reader(len(CHARS) + 1)
    model.load_state_dict(state["model"])
    named = tensors(model)
    by_parameter = {id(p): name for name, p in named.items()}
    optimizer = torch.optim.AdamW(model.parameters(), lr=1e-3, weight_decay=1e-4)
    optimizer.load_state_dict(state["optimizer"])
    data = {name: numpy(p) for name, p in named.items()}
    data.update({name: numpy(t) for name, t in statistics(model).items()})
    steps = 0
    for p in model.parameters():
        name = by_parameter[id(p)]
        moments = optimizer.state.get(p, {})
        data[f"adam.m.{name}"] = numpy(moments["exp_avg"]) if "exp_avg" in moments else numpy(torch.zeros_like(p))
        data[f"adam.v.{name}"] = numpy(moments["exp_avg_sq"]) if "exp_avg_sq" in moments else numpy(torch.zeros_like(p))
        steps = max(steps, int(moments.get("step", 0)))
    plan = state.get("plan") or {"start": 0, "end": 0, "rate": 1e-3}
    config = config_for_iris(CHARS)
    config["state"] = {"step": int(state["step"]), "minutes": float(state["minutes"]),
                       "plan": {"start": float(plan["start"]), "end": float(plan["end"]), "rate": float(plan["rate"])},
                       "adam_steps": steps}
    iris_file.write(target, "iris-ocr-train", config, {"source": f"tools/ocr/train.py checkpoint, step {state['step']}"}, data, {})
    print(f"{target}: step {state['step']}, {state['minutes']:.0f} min, {len(named)} tensors, Adam step {steps}")


if __name__ == "__main__":
    main()
