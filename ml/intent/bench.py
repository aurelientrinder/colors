"""How fast each runtime writes a plan, one request at a time as typed, and whether it stays right.

    python bench.py llama  --build cpu|vulkan|openvino-2026.4.1 --gguf runs/gguf/plan-Q8_0.gguf [--device NPU]
    python bench.py llama  --build npu --name palette-plan-npu    # target/release/palette-plan.exe
    python bench.py torch  --model runs/gemma270m-v2 --device cpu|xpu
    python bench.py ov     --model runs/ov/int8 --device CPU|GPU|NPU

llama.cpp runs as llama-server with the plan grammar (plan.gbnf); the time is the whole request
as the engine will see it, over localhost. Writes runs/bench/<name>.jsonl and scores it.
"""

import argparse
import json
import pathlib
import statistics
import subprocess
import sys
import time
import urllib.request

HERE = pathlib.Path(__file__).parent
TESTS = [json.loads(l) for l in open(HERE / "test.jsonl", encoding="utf-8") if l.strip()]
GRAMMAR = (HERE / "plan.gbnf").read_text(encoding="utf-8")


def post(url, body):
    req = urllib.request.Request(url, json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read())


def llama(args):
    if args.build == "npu":
        # palette-plan (crates/palette-plan, model from ml/npu/export.py): llama-server's interface.
        cmd = [str(HERE.parents[1] / "target" / "release" / "palette-plan.exe"), "serve", "--port", "8091"]
    else:
        exe = HERE / "tools" / args.build / "llama-server.exe"
        cmd = [str(exe), "-m", args.gguf, "-c", "512", "-np", "1", "--port", "8091", "-t", str(args.threads), "--no-webui"]
        cmd += ["-ngl", "99"] if args.build != "cpu" else ["-ngl", "0"]
    env = None
    if args.device:
        import os

        env = dict(os.environ, GGML_OPENVINO_DEVICE=args.device)
    log = open(HERE / "runs" / "bench" / f"{args.name}.server.log", "w")
    srv = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, env=env)
    try:
        t0 = time.time()
        while True:
            try:
                if json.loads(urllib.request.urlopen("http://127.0.0.1:8091/health", timeout=1).read()).get("status") == "ok":
                    break
            except Exception:
                pass
            if srv.poll() is not None or time.time() - t0 > 120:
                raise SystemExit(f"server did not start, see {log.name}")
            time.sleep(0.2)
        load = time.time() - t0

        def plan(q):
            body = {"prompt": q + "\n", "n_predict": 48, "temperature": 0, "top_k": 1, "cache_prompt": True}
            if not args.free:
                body["grammar"] = GRAMMAR
            r = post("http://127.0.0.1:8091/completion", body)
            return r["content"].strip(), r.get("timings", {})

        return load, plan
    except BaseException:
        srv.kill()
        raise
    finally:
        args._stop = srv


def torch_runtime(args):
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer

    t0 = time.time()
    tok = AutoTokenizer.from_pretrained(args.model)
    model = AutoModelForCausalLM.from_pretrained(args.model, dtype=torch.float32).to(args.device).eval()
    torch.set_num_threads(args.threads)

    def plan(q):
        with torch.no_grad():
            enc = tok(q + "\n", return_tensors="pt").to(args.device)
            g = model.generate(**enc, max_new_tokens=48, do_sample=False, eos_token_id=tok.eos_token_id)
        return tok.decode(g[0, enc["input_ids"].shape[1] :], skip_special_tokens=True).strip(), {}

    return time.time() - t0, plan


def ov(args):
    import openvino_genai as og

    t0 = time.time()
    cfg = {"MAX_PROMPT_LEN": 64, "MIN_RESPONSE_LEN": 48} if args.device == "NPU" else {}
    pipe = og.LLMPipeline(args.model, args.device, **cfg)
    gc = pipe.get_generation_config()
    gc.max_new_tokens = 48
    gc.do_sample = False
    gc.apply_chat_template = False   # trained on the bare request
    if not args.free:
        so = og.StructuredOutputConfig()
        so.grammar = GRAMMAR
        gc.structured_output_config = so

    def plan(q):
        return pipe.generate(q + "\n", gc).strip(), {}

    return time.time() - t0, plan


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runtime", choices=["llama", "torch", "ov"])
    ap.add_argument("--build", default="cpu")
    ap.add_argument("--gguf", default=str(HERE / "runs" / "gguf" / "plan-Q8_0.gguf"))
    ap.add_argument("--model", default=str(HERE / "runs" / "gemma270m-v2"))
    ap.add_argument("--device", default=None)
    ap.add_argument("--threads", type=int, default=4)
    ap.add_argument("--free", action="store_true", help="no grammar")
    ap.add_argument("--name", default=None)
    args = ap.parse_args()
    args.name = args.name or "-".join(
        x for x in [args.runtime, args.build if args.runtime == "llama" else None,
                    pathlib.Path(args.gguf).stem if args.runtime == "llama" else pathlib.Path(args.model).name,
                    args.device, "free" if args.free else None] if x)
    (HERE / "runs" / "bench").mkdir(parents=True, exist_ok=True)
    args._stop = None

    try:
        load, plan = {"llama": llama, "torch": torch_runtime, "ov": ov}[args.runtime](args)
        for q in ["open notepad and weather snapped", "close chrome"]:   # warm up
            plan(q)
        times, preds, server = [], [], []
        for t in TESTS:
            t0 = time.perf_counter()
            p, tm = plan(t["q"])
            times.append((time.perf_counter() - t0) * 1000)
            preds.append({"q": t["q"], "plan": p})
            if tm:
                server.append(tm.get("prompt_ms", 0) + tm.get("predicted_ms", 0))
    finally:
        if args._stop is not None:
            args._stop.kill()

    out = HERE / "runs" / "bench" / f"{args.name}.jsonl"
    with open(out, "w", encoding="utf-8") as f:
        for p in preds:
            f.write(json.dumps(p, ensure_ascii=False) + "\n")
    times.sort()
    q = lambda p: times[min(len(times) - 1, int(p * len(times)))]
    line = f"{args.name:<44} load {load:5.1f}s  median {q(0.5):6.1f} ms  p95 {q(0.95):6.1f} ms  max {times[-1]:6.1f} ms"
    if server:
        line += f"  (in server {statistics.median(server):.1f} ms)"
    print(line)
    r = subprocess.run([sys.executable, str(HERE / "score.py"), str(out), "--show", "0"], capture_output=True, text=True,
                       encoding="utf-8")
    print("   ", "  ".join(r.stdout.splitlines()[:3]))
    with open(HERE / "runs" / "bench" / "summary.txt", "a", encoding="utf-8") as f:
        f.write(line + "\n    " + "  ".join(r.stdout.splitlines()[:3]) + "\n")


if __name__ == "__main__":
    main()
