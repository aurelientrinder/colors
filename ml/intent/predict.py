"""Plans for the test requests from a trained model, scored.

    python predict.py runs/gemma270m [--device cpu]

Greedy decoding, the request alone as the prompt, as the engine will run it.
"""

import argparse
import json
import pathlib
import subprocess
import sys
import time

import torch

HERE = pathlib.Path(__file__).parent


def run(model, tok, dev, out_path, bs=32):
    rows = [json.loads(l) for l in open(HERE / "test.jsonl", encoding="utf-8") if l.strip()]
    model.eval()
    tok.padding_side = "left"
    preds = []
    t0 = time.time()
    with torch.no_grad():
        for i in range(0, len(rows), bs):
            qs = [r["q"] + "\n" for r in rows[i : i + bs]]
            enc = tok(qs, return_tensors="pt", padding=True, add_special_tokens=True).to(dev)
            gen = model.generate(**enc, max_new_tokens=48, do_sample=False, eos_token_id=tok.eos_token_id,
                                 pad_token_id=tok.pad_token_id)
            for r, g in zip(rows[i : i + bs], gen[:, enc["input_ids"].shape[1] :]):
                preds.append({"q": r["q"], "plan": tok.decode(g, skip_special_tokens=True).strip()})
    print(f"{len(rows)} plans in {time.time() - t0:.1f} s")
    with open(out_path, "w", encoding="utf-8") as f:
        for p in preds:
            f.write(json.dumps(p, ensure_ascii=False) + "\n")
    subprocess.run([sys.executable, str(HERE / "score.py"), str(out_path), "--show", "40"], check=False)


def main():
    from transformers import AutoModelForCausalLM, AutoTokenizer

    from train import device

    ap = argparse.ArgumentParser()
    ap.add_argument("model")
    ap.add_argument("--device", default=None)
    args = ap.parse_args()
    dev = args.device or device()
    tok = AutoTokenizer.from_pretrained(args.model)
    model = AutoModelForCausalLM.from_pretrained(args.model, torch_dtype=torch.float32).to(dev)
    run(model, tok, dev, pathlib.Path(args.model) / "test_pred.jsonl")


if __name__ == "__main__":
    main()
