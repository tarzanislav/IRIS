"""Reference for the Marian core of IRIS, made with Hugging Face transformers and PyTorch – only in CI, never in the
program. The C# core must repeat what this script writes.

  tiny OUT_DIR                        a small random Marian model with own tiny SentencePiece models (invented texts)
  download REPO OUT_DIR [--revision R] an OPUS-MT model from Hugging Face; refuses a model without the Apache-2.0 licence
  convert MODEL_DIR OUT.iris          a Marian model → the IRIS model file (own tensor names, provenance, checksums)
  reference MODEL_DIR OUT.json [--set tiny|opus] [--count N]
                                      greedy and beam outputs of transformers for invented sentences
"""
import argparse
import datetime
import hashlib
import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import iris_file  # noqa: E402
import sentences as invented  # noqa: E402

SOURCE_FILE = "iris-source.json"
LICENSE = "apache-2.0"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def notice(message: str) -> None:
    print(f"::notice title=reference::{message}", flush=True)


# ---------------------------------------------------------------------------------------------------- tiny model
TINY_EN = ("the film premiere festival director actor actress series season episode trailer review story world city night "
           "first new great dark light summer winter studio release cinema audience critics award prize best house river "
           "road music camera ticket evening morning friend letter").split()
TINY_SK = ("film premiéra festival režisér herec herečka seriál séria epizóda ukážka recenzia príbeh svet mesto noc prvý "
           "nový veľký tmavý svetlo leto zima štúdio uvedenie kino diváci kritici cena najlepší dom rieka cesta hudba "
           "kamera lístok večer ráno priateľ list").split()


def tiny_texts() -> list:
    """Texts for the tiny model: invented sentences and tricky cases for the tokenizer."""
    import random
    r = random.Random(11)
    texts = []
    for _ in range(40):
        words = [r.choice(TINY_EN) for _ in range(r.randint(1, 12))]
        text = " ".join(words)
        texts.append(text[0].upper() + text[1:] + r.choice([".", "!", "?", ",", " (2026).", ""]))
    texts += ["", "a", "   ", "Unknown 漢字 and Ελληνικά", "The film </s> the end", "<unk> before <pad>",
              ">>slk<< the film is new", "Fullｗidth ＡＢＣ and ﬁ", "night " * 20]
    return texts


def make_tiny(out: Path) -> None:
    import sentencepiece as spm
    import torch
    from transformers import GenerationConfig, MarianConfig, MarianMTModel, MarianTokenizer

    out.mkdir(parents=True, exist_ok=True)
    work = out / "spm"
    work.mkdir(exist_ok=True)
    import random
    r = random.Random(5)
    for name, words in (("source", TINY_EN), ("target", TINY_SK)):
        corpus = work / f"{name}.txt"
        corpus.write_text("\n".join(" ".join(r.choice(words) for _ in range(r.randint(3, 12))) for _ in range(2000)),
                          encoding="utf-8")
        spm.SentencePieceTrainer.train(input=str(corpus), model_prefix=str(work / name), model_type="unigram",
                                       vocab_size=64, character_coverage=1.0, normalization_rule_name="nmt_nfkc",
                                       hard_vocab_limit=False)
    pieces = ["</s>", "<unk>"]
    for name in ("source", "target"):
        sp = spm.SentencePieceProcessor(model_file=str(work / f"{name}.model"))
        for i in range(sp.get_piece_size()):
            p = sp.id_to_piece(i)
            if p not in ("<unk>", "<s>", "</s>") and p not in pieces:
                pieces.append(p)
    pieces.append("<pad>")
    vocab = {p: i for i, p in enumerate(pieces)}
    (work / "vocab.json").write_text(json.dumps(vocab, ensure_ascii=False), encoding="utf-8")
    tokenizer = MarianTokenizer(str(work / "source.model"), str(work / "target.model"), str(work / "vocab.json"),
                                source_lang="en", target_lang="sk")
    tokenizer.save_pretrained(out)

    eos, unk, pad = 0, 1, len(pieces) - 1
    config = MarianConfig(vocab_size=len(pieces), d_model=16, encoder_layers=2, decoder_layers=2,
                          encoder_attention_heads=4, decoder_attention_heads=4, encoder_ffn_dim=32, decoder_ffn_dim=32,
                          max_position_embeddings=64, activation_function="swish", scale_embedding=True,
                          pad_token_id=pad, eos_token_id=eos, decoder_start_token_id=pad, forced_eos_token_id=eos,
                          share_encoder_decoder_embeddings=True)
    torch.manual_seed(1234)
    model = MarianMTModel(config).eval()
    with torch.no_grad():
        for name, p in model.named_parameters():
            if "embed_positions" in name:
                continue
            if name.endswith("layer_norm.weight"):
                p.copy_(1 + 0.2 * torch.randn_like(p))
            elif name.endswith(".bias"):
                p.copy_(0.1 * torch.randn_like(p))
            else:
                p.copy_((0.6 if "shared" in name else 0.3) * torch.randn_like(p))
        bias = torch.randn(1, len(pieces))
        bias[0, eos] = 0
        model.final_logits_bias.copy_(bias)
        # the end of sentence should win in about 15 % of the steps, so that translations end at different lengths
        gaps = []
        for text in tiny_texts()[:20]:
            encoded = tokenizer(text, return_tensors="pt")
            sequence = [pad]
            for _ in range(8):
                logits = model(**encoded, decoder_input_ids=torch.tensor([sequence])).logits[0, -1].clone()
                logits[pad] = -math.inf
                eos_logit = float(logits[eos])
                logits[eos] = -math.inf
                best = int(logits.argmax())
                gaps.append(float(logits[best]) - eos_logit)
                sequence.append(best)
        model.final_logits_bias[0, eos] = float(np.quantile(gaps, 0.15))
    # settings that differ from OPUS-MT, so that more of the search is checked
    model.generation_config = GenerationConfig(num_beams=4, max_length=24, bad_words_ids=[[pad]], forced_eos_token_id=eos,
                                               decoder_start_token_id=pad, pad_token_id=pad, eos_token_id=eos,
                                               length_penalty=0.8, renormalize_logits=True)
    model.save_pretrained(out)
    (out / SOURCE_FILE).write_text(json.dumps({
        "repo": "tiny random Marian model (tools/reference/marian_reference.py tiny)",
        "license": LICENSE, "revision": "seed 1234",
        "files": {f.name: sha256(f) for f in sorted(out.iterdir()) if f.is_file() and f.name != SOURCE_FILE},
    }, indent=1), encoding="utf-8")
    notice(f"tiny model: vocabulary {len(pieces)}, d_model 16, 2+2 layers")


# ---------------------------------------------------------------------------------------------------- download
def download(repo: str, out: Path, revision: str) -> None:
    from huggingface_hub import HfApi, snapshot_download

    info = HfApi().model_info(repo, revision=revision)
    card = info.card_data.to_dict() if info.card_data else {}
    license_ = card.get("license")
    licenses = license_ if isinstance(license_, list) else [license_]
    if LICENSE not in [str(x).lower() for x in licenses]:
        raise SystemExit(f"{repo}@{info.sha}: licence {license_!r} is not {LICENSE} – the model is not used")
    files = [s.rfilename for s in info.siblings]
    weights = "model.safetensors" if "model.safetensors" in files else "pytorch_model.bin"
    wanted = ["config.json", "generation_config.json", "tokenizer_config.json", "special_tokens_map.json", "vocab.json",
              "source.spm", "target.spm", "README.md", weights]
    snapshot_download(repo, revision=info.sha, allow_patterns=[f for f in wanted if f in files], local_dir=out)
    (out / SOURCE_FILE).write_text(json.dumps({
        "repo": repo, "revision": info.sha, "license": LICENSE,
        "url": f"https://huggingface.co/{repo}/tree/{info.sha}",
        "downloaded": datetime.date.today().isoformat(),
        "files": {f: sha256(out / f) for f in wanted if (out / f).exists()},
    }, indent=1), encoding="utf-8")
    notice(f"{repo} revision {info.sha} (licence {license_}), weights {weights}")


# ---------------------------------------------------------------------------------------------------- convert
def positions(count: int, dim: int) -> np.ndarray:
    """The sinusoidal table of Marian computed with libm (math.sin/cos) – as the C# core computes it."""
    table = np.zeros((count, dim), dtype=np.float32)
    sines = (dim + 1) // 2
    for pos in range(count):
        for k in range(sines):
            table[pos, k] = math.sin(pos / math.pow(10000, 2.0 * k / dim))
        for k in range(dim // 2):
            table[pos, sines + k] = math.cos(pos / math.pow(10000, 2.0 * k / dim))
    return table


def effective_generation(model):
    from transformers import GenerationConfig

    gc = model.generation_config
    defaults = GenerationConfig._get_default_generation_params()

    def get(key):
        value = getattr(gc, key, None)
        return defaults.get(key) if value is None else value

    unsupported = {"do_sample": False, "num_beam_groups": 1, "repetition_penalty": 1.0, "no_repeat_ngram_size": 0,
                   "encoder_no_repeat_ngram_size": 0, "min_length": 0, "min_new_tokens": None, "suppress_tokens": None,
                   "begin_suppress_tokens": None, "exponential_decay_length_penalty": None, "forced_bos_token_id": None,
                   "sequence_bias": None, "remove_invalid_values": False, "diversity_penalty": 0.0,
                   "encoder_repetition_penalty": 1.0}
    for key, neutral in unsupported.items():
        value = get(key)
        if value is not None and value != neutral:
            raise SystemExit(f"generation setting {key}={value!r} is not supported by the IRIS core")
    eos = gc.eos_token_id if gc.eos_token_id is not None else model.config.eos_token_id
    eos = eos if isinstance(eos, list) else [eos]
    if len(eos) != 1:
        raise SystemExit(f"several end-of-sentence tokens {eos} are not supported")
    bad = []
    for seq in get("bad_words_ids") or []:
        if len(seq) != 1:
            raise SystemExit(f"bad word sequences longer than one token are not supported: {seq}")
        if seq[0] not in eos:
            bad.append(int(seq[0]))
    forced = get("forced_eos_token_id")
    forced = [] if forced is None else (forced if isinstance(forced, list) else [forced])
    start = gc.decoder_start_token_id if gc.decoder_start_token_id is not None else model.config.decoder_start_token_id
    early = get("early_stopping")
    return {
        "num_beams": int(get("num_beams")), "max_length": int(get("max_length")),
        "length_penalty": float(get("length_penalty")), "early_stopping": early if early == "never" else bool(early),
        "bad_tokens": bad, "forced_eos": [int(x) for x in forced], "renormalize": gc.renormalize_logits is True,
    }, int(eos[0]), int(start)


def convert(model_dir: Path, out: Path) -> None:
    import torch
    import transformers
    from transformers import MarianMTModel, MarianTokenizer

    model = MarianMTModel.from_pretrained(model_dir, attn_implementation="eager").eval()
    tokenizer = MarianTokenizer.from_pretrained(model_dir)
    c = model.config
    if not c.share_encoder_decoder_embeddings or getattr(tokenizer, "separate_vocabs", False):
        raise SystemExit("only Marian models with one shared vocabulary are supported")
    m = model.model
    shared = m.shared.weight
    for other in (m.encoder.embed_tokens.weight, m.decoder.embed_tokens.weight, model.lm_head.weight):
        if not torch.equal(shared, other):
            raise SystemExit("the token vectors of encoder, decoder and output layer are not shared")

    state = model.state_dict()
    used = set()
    tensors = {}

    def take(hf: str, own: str):
        tensors[own] = state[hf].detach().float().numpy()
        used.add(hf)

    tensors["embed"] = shared.detach().float().numpy()
    used.update(k for k in state if k.endswith("embed_tokens.weight") or k in ("model.shared.weight", "lm_head.weight"))
    tensors["out.bias"] = model.final_logits_bias.detach().float().numpy().reshape(-1)
    used.add("final_logits_bias")
    eps = set()
    for side, layers, n in (("encoder", "enc", c.encoder_layers), ("decoder", "dec", c.decoder_layers)):
        for i in range(n):
            p = f"model.{side}.layers.{i}"
            blocks = [("self_attn", "attn" if side == "encoder" else "self")]
            if side == "decoder":
                blocks.append(("encoder_attn", "cross"))
            for hf_block, own_block in blocks:
                for proj, short in (("q_proj", "q"), ("k_proj", "k"), ("v_proj", "v"), ("out_proj", "o")):
                    take(f"{p}.{hf_block}.{proj}.weight", f"{layers}.{i}.{own_block}.{short}.w")
                    take(f"{p}.{hf_block}.{proj}.bias", f"{layers}.{i}.{own_block}.{short}.b")
                take(f"{p}.{hf_block}_layer_norm.weight", f"{layers}.{i}.{own_block}.ln.g")
                take(f"{p}.{hf_block}_layer_norm.bias", f"{layers}.{i}.{own_block}.ln.b")
            for k in (1, 2):
                take(f"{p}.fc{k}.weight", f"{layers}.{i}.ffn.{k}.w")
                take(f"{p}.fc{k}.bias", f"{layers}.{i}.ffn.{k}.b")
            take(f"{p}.final_layer_norm.weight", f"{layers}.{i}.ffn.ln.g")
            take(f"{p}.final_layer_norm.bias", f"{layers}.{i}.ffn.ln.b")
        layer = getattr(m, side).layers[0]
        eps.update({layer.self_attn_layer_norm.eps, layer.final_layer_norm.eps})

    # the positions are computed by the core, not stored: check that the formula gives the reference's table
    own = positions(c.max_position_embeddings, c.d_model)
    for side in ("encoder", "decoder"):
        key = f"model.{side}.embed_positions.weight"
        table = getattr(m, side).embed_positions.weight.detach().float().numpy()
        used.add(key)
        # within one unit in the last place of float32 (an absolute bound would let small values differ widely)
        ulp = np.spacing(np.maximum(np.abs(table), np.abs(own)))
        if not np.all(np.abs(table - own) <= ulp):
            worst = float(np.max(np.abs(table - own) / ulp))
            raise SystemExit(f"{side} positions differ from the formula of the core by up to {worst:.1f} ulp")
    unknown = sorted(k for k in state if k not in used)
    if unknown:
        raise SystemExit(f"weights the core does not know: {unknown}")
    if len(eps) != 1:
        raise SystemExit(f"different LayerNorm epsilons: {eps}")

    generation, eos, start = effective_generation(model)
    vocab_path = Path(model_dir) / "vocab.json"
    vocab = json.loads(vocab_path.read_text(encoding="utf-8"))
    if max(vocab.values()) >= c.vocab_size:
        raise SystemExit("the vocabulary has ids outside the model")
    specials = {}
    for token in list(tokenizer.all_special_tokens) + list(tokenizer.added_tokens_encoder):
        token_id = tokenizer.convert_tokens_to_ids(token)
        if token_id is not None and (token_id != tokenizer.unk_token_id or token == tokenizer.unk_token):
            specials[str(token)] = int(token_id)
    config = {
        "d_model": c.d_model, "encoder_layers": c.encoder_layers, "decoder_layers": c.decoder_layers,
        "encoder_heads": c.encoder_attention_heads, "decoder_heads": c.decoder_attention_heads,
        "encoder_ffn": c.encoder_ffn_dim, "decoder_ffn": c.decoder_ffn_dim, "vocab_size": c.vocab_size,
        "max_positions": c.max_position_embeddings, "activation": c.activation_function,
        "scale_embedding": bool(c.scale_embedding), "layer_norm_eps": float(eps.pop()),
        "pad_id": int(c.pad_token_id), "eos_id": eos, "unk_id": int(tokenizer.unk_token_id), "decoder_start_id": start,
        "special_tokens": specials, "generation": generation,
    }
    source = json.loads((Path(model_dir) / SOURCE_FILE).read_text(encoding="utf-8"))
    provenance = dict(source, converted=datetime.date.today().isoformat(),
                      converter=f"tools/reference/marian_reference.py (transformers {transformers.__version__}, torch {torch.__version__})")
    spm_files = [Path(f) for f in tokenizer.spm_files]
    blobs = {"source.spm": spm_files[0].read_bytes(), "target.spm": spm_files[1].read_bytes(),
             "vocab.json": vocab_path.read_bytes()}
    iris_file.write(out, "marian", config, provenance, tensors, blobs)
    params = sum(t.size for t in tensors.values())
    notice(f"converted {source.get('repo')} → {out.name}: {params:,} parameters, {len(tensors)} tensors, "
           f"{out.stat().st_size / 1e6:.1f} MB, SHA-256 {sha256(out)[:16]}…, generation {json.dumps(generation)}")


# ---------------------------------------------------------------------------------------------------- reference
def reference(model_dir: Path, out: Path, texts: list) -> None:
    import torch
    import transformers
    from transformers import MarianMTModel, MarianTokenizer

    torch.manual_seed(0)
    model = MarianMTModel.from_pretrained(model_dir, attn_implementation="eager").eval()
    tokenizer = MarianTokenizer.from_pretrained(model_dir)
    generation, eos, start = effective_generation(model)
    beams = generation["num_beams"] if generation["num_beams"] > 1 else 4
    pad = model.config.pad_token_id

    def generated(sequence):
        ids = sequence.tolist()[1:]
        while ids and ids[-1] == pad:
            ids.pop()
        return ids

    def text_of(ids):
        return tokenizer.decode(ids, skip_special_tokens=True, clean_up_tokenization_spaces=False)

    items = []
    lengths = []
    for text in texts:
        encoded = tokenizer(text, return_tensors="pt")
        with torch.no_grad():
            greedy = generated(model.generate(**encoded, num_beams=1, do_sample=False)[0])
            found = model.generate(**encoded, num_beams=beams, do_sample=False, num_return_sequences=1,
                                   return_dict_in_generate=True, output_scores=True)
            beam = generated(found.sequences[0])
            logits = model(**encoded, decoder_input_ids=torch.tensor([[start]])).logits[0, -1]
            top = torch.topk(logits, 10)
        lengths.append(len(beam))
        items.append({
            "text": text, "source": encoded["input_ids"][0].tolist(),
            "greedy": greedy, "greedy_text": text_of(greedy),
            "beam": beam, "beam_text": text_of(beam), "beam_score": float(found.sequences_scores[0]),
            "first_step": [[int(i), float(v)] for v, i in zip(top.values.tolist(), top.indices.tolist())],
        })
    source = json.loads((Path(model_dir) / SOURCE_FILE).read_text(encoding="utf-8"))
    data = {"format": 1, "beams": beams,
            "model": {"repo": source.get("repo"), "revision": source.get("revision"),
                      "transformers": transformers.__version__, "torch": torch.__version__,
                      "generation": generation},
            "sentences": items}
    out.write_text(json.dumps(data, ensure_ascii=False, indent=0), encoding="utf-8")
    notice(f"reference {out.name}: {len(items)} sentences, {beams} beams, beam output length "
           f"{min(lengths)}–{max(lengths)} tokens (mean {sum(lengths) / len(lengths):.1f}), "
           f"greedy = beam in {sum(i['greedy'] == i['beam'] for i in items)} sentences")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("tiny").add_argument("out", type=Path)
    d = commands.add_parser("download")
    d.add_argument("repo")
    d.add_argument("out", type=Path)
    d.add_argument("--revision", default="main")
    c = commands.add_parser("convert")
    c.add_argument("model", type=Path)
    c.add_argument("out", type=Path)
    r = commands.add_parser("reference")
    r.add_argument("model", type=Path)
    r.add_argument("out", type=Path)
    r.add_argument("--set", choices=["tiny", "opus"], default="opus")
    r.add_argument("--count", type=int, default=500)
    args = parser.parse_args()
    if args.command == "tiny":
        make_tiny(args.out)
    elif args.command == "download":
        download(args.repo, args.out, args.revision)
    elif args.command == "convert":
        convert(args.model, args.out)
    else:
        texts = tiny_texts() if args.set == "tiny" else invented.sentences(args.count)
        reference(args.model, args.out, texts)


if __name__ == "__main__":
    main()
