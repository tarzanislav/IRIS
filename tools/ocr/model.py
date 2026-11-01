"""The IRIS line reader (own design): a stack of 3×3 convolutions that turns a line picture of height 32 into a row of
column features (width / 4), two bidirectional LSTM layers that read the row in both directions, and a layer that
gives, for every column, the probability of each character or of "nothing here" (CTC blank).

    32×W ─conv32 pool2×2─ 16×W/2 ─conv64 pool2×2─ 8×W/4 ─conv128─ ─conv128 pool2×1─ 4×W/4 ─conv192─ ─conv192 pool2×1─
    2×W/4 ─conv(2×1)256─ 1×W/4 ─BiLSTM 192─ ─BiLSTM 192─ ─linear─ characters + blank

Batch normalization helps learning and is folded into the convolutions when the model is written for IRIS, so the C#
reader computes plain convolutions."""
import torch
from torch import nn

# (channels, pooling after the block or None)
BLOCKS = [(32, (2, 2)), (64, (2, 2)), (128, None), (128, (2, 1)), (192, None), (192, (2, 1))]
FEATURES = 256
HIDDEN = 192
LAYERS = 2


class Reader(nn.Module):
    def __init__(self, classes: int):
        super().__init__()
        self.convs = nn.ModuleList()
        self.norms = nn.ModuleList()
        channels = 1
        for out, _ in BLOCKS:
            self.convs.append(nn.Conv2d(channels, out, 3, padding=1, bias=False))
            self.norms.append(nn.BatchNorm2d(out))
            channels = out
        self.collapse = nn.Conv2d(channels, FEATURES, (2, 1), bias=False)
        self.collapse_norm = nn.BatchNorm2d(FEATURES)
        self.lstm = nn.LSTM(FEATURES, HIDDEN, num_layers=LAYERS, bidirectional=True, batch_first=True)
        self.output = nn.Linear(2 * HIDDEN, classes)

    def forward(self, x):  # x: [batch, 1, 32, width] → logits [batch, width / 4, classes]
        for (_, pool), conv, norm in zip(BLOCKS, self.convs, self.norms):
            x = torch.relu(norm(conv(x)))
            if pool:
                x = nn.functional.max_pool2d(x, pool)
        x = torch.relu(self.collapse_norm(self.collapse(x)))  # [batch, features, 1, width / 4]
        x = x.squeeze(2).transpose(1, 2)
        x, _ = self.lstm(x)
        return self.output(x)


def folded(conv: nn.Conv2d, norm: nn.BatchNorm2d):
    """Weights and bias of a convolution with its batch normalization (as in evaluation) folded in."""
    scale = norm.weight / torch.sqrt(norm.running_var + norm.eps)
    weight = conv.weight * scale[:, None, None, None]
    bias = norm.bias - norm.running_mean * scale
    return weight.detach().float().numpy(), bias.detach().float().numpy()


def tensors_for_iris(model: Reader) -> dict:
    """The tensors of the IRIS model file (own names): conv.N.w [out, in, kh, kw] and conv.N.b with the
    normalization folded in; lstm.L.{fw,bw}.ih [4H, in], .hh [4H, H], .b [4H] (gates i, f, g, o); out.w, out.b."""
    model = model.eval()
    tensors = {}
    pairs = list(zip(model.convs, model.norms)) + [(model.collapse, model.collapse_norm)]
    for i, (conv, norm) in enumerate(pairs):
        tensors[f"conv.{i}.w"], tensors[f"conv.{i}.b"] = folded(conv, norm)
    for layer in range(LAYERS):
        for direction, suffix in (("fw", ""), ("bw", "_reverse")):
            get = lambda name: getattr(model.lstm, f"{name}_l{layer}{suffix}").detach().float().numpy()  # noqa: E731
            tensors[f"lstm.{layer}.{direction}.ih"] = get("weight_ih")
            tensors[f"lstm.{layer}.{direction}.hh"] = get("weight_hh")
            tensors[f"lstm.{layer}.{direction}.b"] = get("bias_ih") + get("bias_hh")
    tensors["out.w"] = model.output.weight.detach().float().numpy()
    tensors["out.b"] = model.output.bias.detach().float().numpy()
    return tensors


def config_for_iris(charset: str) -> dict:
    return {
        "height": 32,
        "blocks": [{"channels": c, "pool": list(p) if p else None} for c, p in BLOCKS],
        "features": FEATURES,
        "hidden": HIDDEN,
        "layers": LAYERS,
        "charset": charset,
        "blank": 0,
        "input": "ink = 1 − gray / 255, height 32, width in proportion",
    }
