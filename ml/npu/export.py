"""Prepares the launcher's two models for the NPU (Intel Core Ultra), as OpenVINO IR.

    ml\\intent\\.venv\\Scripts\\python ml\\npu\\export.py      # needs openvino, numpy, tokenizers

1. Search: models\\embeddinggemma-2-text-q4\\model_npu.xml (+ .bin), from model_q4.onnx.
   OpenVINO's ONNX reader gets com.microsoft RotaryEmbedding wrong for this model (3-D input
   without num_heads, so the head size has to come from the cos table): its embeddings drift far
   from ONNX Runtime's. Here that operation is written out in standard ones instead.

2. Plans: models\\planner-npu\\, from ml\\intent\\runs\\ov\\int4 (optimum-cli export openvino
   --weight-format int4 of runs\\gemma270m-v2). The output layer, a 262k-token vocabulary, is
   taken off: the NPU returns the last hidden state, and palette-plan scores only the tokens a
   plan can contain (every token of a training plan, and the request's own). Beside the model:
   tokenizer.json, and plan.json with those tokens and where the embedding table, which doubles
   as the output layer, sits in the .bin.

Both are checked: the embeddings against ONNX Runtime's scores, the plans by score.py in
ml\\intent\\README.md ("python bench.py npu").
"""
import json, pathlib, shutil, sys
import xml.etree.ElementTree as ET
import numpy as np
import openvino as ov
from openvino import opset13 as ops
from openvino.frontend.onnx import ConversionExtension
from tokenizers import Tokenizer

REPO = pathlib.Path(__file__).resolve().parents[2]
EMBED = REPO / "models" / "embeddinggemma-2-text-q4"
PLAN_SRC = REPO / "ml" / "intent" / "runs" / "ov" / "int4"
PLAN_TOK = REPO / "ml" / "intent" / "runs" / "gemma270m-v2" / "tokenizer.json"
PLAN_DATA = REPO / "ml" / "intent" / "data"
PLAN_OUT = REPO / "models" / "planner-npu"


def rotary(node):
    """com.microsoft RotaryEmbedding, non-interleaved, 3-D input (batch, seq, heads * head)."""
    x, pos, cos, sin = (node.get_input(i) for i in range(4))
    if node.get_attribute("interleaved", 0) or node.get_attribute("rotary_embedding_dim", 0):
        raise ValueError("only the non-interleaved, full-head form is written out")
    if x.get_partial_shape().rank.get_length() != 3:
        raise ValueError("only 3-D input is written out")
    if any(np.asarray(pos.get_node().get_data()).ravel()):
        raise ValueError("only position offset 0 is written out (the cos table is per position)")
    half = ops.gather(ops.shape_of(cos), ops.constant([1], np.int64), ops.constant(0, np.int64))
    head = ops.multiply(half, ops.constant([2], np.int64))
    xr = ops.reshape(x, ops.concat([ops.constant([0, 0, -1], np.int64), head], 0), special_zero=True)
    x1, x2 = ops.split(xr, ops.constant(-1, np.int64), 2).outputs()
    c = ops.unsqueeze(cos, ops.constant([1], np.int64))  # (seq, 1, half): one row per position
    s = ops.unsqueeze(sin, ops.constant([1], np.int64))
    o1 = ops.subtract(ops.multiply(x1, c), ops.multiply(x2, s))
    o2 = ops.add(ops.multiply(x2, c), ops.multiply(x1, s))
    out = ops.reshape(ops.concat([o1, o2], -1), ops.shape_of(x), special_zero=False)
    return [out.output(0)]


def export_embedder(core):
    core.add_extension(ConversionExtension("RotaryEmbedding", "com.microsoft", rotary))
    m = core.read_model(EMBED / "model_q4.onnx")
    ov.save_model(m, EMBED / "model_npu.xml", compress_to_fp16=False)
    print("search model:", EMBED / "model_npu.xml")

    # The scores of `palette-semantic embed-test` under ONNX Runtime (CPU and DirectML agree).
    expected = [0.814, 0.471, 0.493]
    tok = Tokenizer.from_file(str(EMBED / "tokenizer.json"))
    cm = core.compile_model(m, "CPU", {"INFERENCE_PRECISION_HINT": "f32"})
    req = cm.create_infer_request()

    def emb(text):
        ids = np.array([tok.encode(text).ids], np.int64)
        feed = {"input_ids": ids, "attention_mask": np.ones_like(ids)}
        for i in cm.inputs:
            if i.get_any_name() not in feed:
                feed[i.get_any_name()] = np.zeros((0, 512), np.float32)
        out = req.infer(feed)
        v = next(val for k, val in out.items() if "sentence_embedding" in k.get_names())[0, :256]
        return v / np.linalg.norm(v)

    q = emb("task: search result | query: how do I change the screen brightness")
    docs = ["title: brightness | text: Display settings: adjust the brightness and colour of your screen",
            "title: recipe | text: Recipe for chocolate chip cookies: flour, butter, sugar and eggs",
            "title: invoice | text: Invoice number 2043 for consulting services, payable within 30 days"]
    got = [float(q @ emb(d)) for d in docs]
    print("  scores", " ".join(f"{g:.3f}" for g in got), " expected", " ".join(f"{e:.3f}" for e in expected))
    if max(abs(g - e) for g, e in zip(got, expected)) > 0.005:
        sys.exit("search model: embeddings differ from ONNX Runtime's")


def plan_tokens(tok):
    """Every token of every training plan, the end-of-text token and the newline."""
    ids = set()
    for name in ["train.jsonl", "valid.jsonl"]:
        for line in open(PLAN_DATA / name, encoding="utf-8"):
            r = json.loads(line)
            pre = len(tok.encode(r["q"] + "\n").ids)
            ids.update(tok.encode(r["q"] + "\n" + r["plan"]).ids[pre:])
    newline = tok.encode("\n", add_special_tokens=False).ids
    return sorted(ids | {1} | set(newline)), newline


def export_planner(core):
    m = core.read_model(PLAN_SRC / "openvino_model.xml")
    head = next(n for n in m.get_ops() if n.get_friendly_name() == "__module.lm_head/aten::linear/MatMul")
    hidden = head.input(0).get_source_output()
    m.outputs[0].get_node().input(0).replace_source_output(hidden)
    m.validate_nodes_and_infer_types()
    m.outputs[0].get_tensor().set_names({"logits"})  # the name the NPU's LLM pipeline looks for
    PLAN_OUT.mkdir(parents=True, exist_ok=True)
    ov.save_model(m, PLAN_OUT / "openvino_model.xml", compress_to_fp16=False)

    # Where the embedding table (uint8, a zero point and a scale per row) is in the .bin.
    layers = {l.get("name"): l for l in ET.parse(PLAN_OUT / "openvino_model.xml").getroot().iter("layer")}
    def where(name):
        d = layers[name].find("data")
        return {"offset": int(d.get("offset")), "size": int(d.get("size")), "type": d.get("element_type"),
                "shape": [int(x) for x in d.get("shape").split(",")]}
    table = {k: where(f"self.model.embed_tokens.weight{s}") for k, s in [("weight", ""), ("zero_point", "/zero_point"), ("scale", "/scale")]}
    assert table["weight"]["type"] == "u8" and table["zero_point"]["type"] == "u8" and table["scale"]["type"] == "f16", table

    tok = Tokenizer.from_file(str(PLAN_TOK))
    ids, newline = plan_tokens(tok)
    json.dump({"tokens": ids, "eos": 1, "newline": newline, "table": table}, open(PLAN_OUT / "plan.json", "w"))
    shutil.copy(PLAN_TOK, PLAN_OUT / "tokenizer.json")
    print("plan model:", PLAN_OUT, f"({len(ids)} plan tokens)")


if __name__ == "__main__":
    which = sys.argv[1:] or ["search", "plans"]
    if "search" in which:
        export_embedder(ov.Core())
    if "plans" in which:
        export_planner(ov.Core())
