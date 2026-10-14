"""Fine-tune Gemma 3 270M to write plans.

    python train.py [--epochs 2] [--out runs/gemma270m]

Each example is the request, a newline, then the plan and <eos>; the loss is on the plan only.
The prompt is nothing but the request, so the model reads a dozen tokens before it writes. At
the end the model is saved to --out and the test set is scored (see predict.py).
"""

import argparse
import json
import math
import pathlib
import random
import time

import torch
from transformers import AutoModelForCausalLM, AutoTokenizer

HERE = pathlib.Path(__file__).parent


def device():
    if hasattr(torch, "xpu") and torch.xpu.is_available():
        return "xpu"
    return "cuda" if torch.cuda.is_available() else "cpu"


def load(path):
    return [json.loads(l) for l in open(path, encoding="utf-8") if l.strip()]


def encode(tok, rows):
    out = []
    for r in rows:
        p = tok(r["q"] + "\n", add_special_tokens=True)["input_ids"]
        a = tok(r["plan"], add_special_tokens=False)["input_ids"] + [tok.eos_token_id]
        out.append((p, a))
    return out


def batches(data, bs, pad, shuffle):
    idx = list(range(len(data)))
    if shuffle:
        random.shuffle(idx)
    for i in range(0, len(idx), bs):
        chunk = [data[j] for j in idx[i : i + bs]]
        n = max(len(p) + len(a) for p, a in chunk)
        ids = torch.full((len(chunk), n), pad)
        att = torch.zeros((len(chunk), n), dtype=torch.long)
        lab = torch.full((len(chunk), n), -100)
        for k, (p, a) in enumerate(chunk):
            s = p + a
            ids[k, : len(s)] = torch.tensor(s)
            att[k, : len(s)] = 1
            lab[k, len(p) : len(s)] = torch.tensor(a)
        yield ids, att, lab


def loss_on(model, ids, att, lab):
    """Cross-entropy on the plan tokens only. The logits are made only where there is a label:
    over Gemma's 262k-word vocabulary, logits for every position would dwarf the model."""
    h = model.model(input_ids=ids, attention_mask=att).last_hidden_state
    # the token at t predicts t+1
    h, lab = h[:, :-1], lab[:, 1:]
    m = lab != -100
    logits = model.lm_head(h[m]).float()
    cap = getattr(model.config, "final_logit_softcapping", None)
    if cap:
        logits = torch.tanh(logits / cap) * cap
    return torch.nn.functional.cross_entropy(logits, lab[m])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="google/gemma-3-270m-it")
    ap.add_argument("--epochs", type=float, default=2)
    ap.add_argument("--bs", type=int, default=32)
    ap.add_argument("--lr", type=float, default=1e-4)
    ap.add_argument("--out", default=str(HERE / "runs" / "gemma270m"))
    ap.add_argument("--limit", type=int, default=0, help="train on this many examples only (a quick try)")
    args = ap.parse_args()
    random.seed(0)
    torch.manual_seed(0)

    dev = device()
    print(f"device {dev}")
    tok = AutoTokenizer.from_pretrained(args.model)
    model = AutoModelForCausalLM.from_pretrained(args.model, torch_dtype=torch.float32, attn_implementation="eager").to(dev)
    model.config.use_cache = False

    train = encode(tok, load(HERE / "data" / "train.jsonl"))
    valid = encode(tok, load(HERE / "data" / "valid.jsonl"))
    if args.limit:
        train = train[: args.limit]
    lens = sorted(len(p) + len(a) for p, a in train)
    print(f"{len(train)} examples, tokens median {lens[len(lens) // 2]} max {lens[-1]}")

    steps = math.ceil(len(train) / args.bs) * args.epochs
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.01, betas=(0.9, 0.98))
    warm = max(1, int(0.05 * steps))
    sched = torch.optim.lr_scheduler.LambdaLR(
        opt, lambda s: min(1, (s + 1) / warm) * 0.5 * (1 + math.cos(math.pi * min(1, s / steps)))
    )
    amp = dict(device_type=dev, dtype=torch.bfloat16, enabled=dev != "cpu")

    def evaluate():
        model.eval()
        tot, n = 0.0, 0
        with torch.no_grad(), torch.autocast(**amp):
            for ids, att, lab in batches(valid, 64, tok.pad_token_id, False):
                l = loss_on(model, ids.to(dev), att.to(dev), lab.to(dev))
                tot += l.item() * len(ids)
                n += len(ids)
        model.train()
        return tot / n

    step, t0 = 0, time.time()
    model.train()
    done = False
    while not done:
        for ids, att, lab in batches(train, args.bs, tok.pad_token_id, True):
            with torch.autocast(**amp):
                loss = loss_on(model, ids.to(dev), att.to(dev), lab.to(dev))
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            opt.step()
            sched.step()
            opt.zero_grad(set_to_none=True)
            step += 1
            if step % 50 == 0:
                rate = step / (time.time() - t0)
                print(f"step {step}/{steps}  loss {loss.item():.4f}  {rate:.2f} steps/s  eta {(steps - step) / rate / 60:.1f} min", flush=True)
            if step % 500 == 0:
                print(f"  valid loss {evaluate():.4f}", flush=True)
            if step >= steps:
                done = True
                break
    print(f"valid loss {evaluate():.4f}  ({(time.time() - t0) / 60:.1f} min)")

    out = pathlib.Path(args.out)
    model.config.use_cache = True
    model.save_pretrained(out)
    tok.save_pretrained(out)
    print(f"saved {out}")

    import predict

    predict.run(model, tok, dev, out / "test_pred.jsonl")


if __name__ == "__main__":
    main()
