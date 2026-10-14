# Requests the rules miss: data for a small model

The rules in `crates/palette-core/src/intent.rs` turn requests like "open notepad and weather
snapped" into plans. For phrasings they miss, a small language model (Gemma 3 270M, fine-tuned)
will write the same plans. This folder holds what it is trained and judged on.

| File | What it is |
|---|---|
| `plan.py` | The one-line plan format, and how two plans are compared |
| `apps.json` | The apps, Settings pages and folders the examples use |
| `gen.py` | Writes training examples: `data/train.jsonl`, `data/valid.jsonl` (not in git) |
| `test.jsonl` | Requests written by hand, with tags; never used for training |
| `score.py` | Scores predicted plans against `test.jsonl` |

The model writes a plan such as `open notepad ; open weather ; snap side`. Names are written as
typed, typos included; the engine finds the app, as it does for the rules. See `plan.py` for
every step.

## Steps

```
python gen.py                       # 30,000 training and 1,000 validation examples
cargo run -q -p palette-core --example intent_rules > rules.jsonl
python score.py rules.jsonl         # the rules, as a baseline
```

`gen.py` leaves out every request in the test set (in any politeness), and the apps listed under
`holdout_apps` in `apps.json` never appear in training, so the test measures phrasings and names
the model has not seen.

Not offered on purpose: shutting down, restarting, signing out and deleting. These stay `none`.

## Speed (step 3), Core Ultra 7 258V

One request at a time, all 227 test requests, median / 95th percentile, after a warm-up:

| Runtime | Where | Model file | Median | p95 | Plans right |
|---|---|---|---|---|---|
| llama.cpp + grammar | CPU, 4 threads | Q8_0, 278 MB | 60 ms | 114 ms | 98.7% |
| llama.cpp + grammar | Arc GPU (Vulkan) | Q8_0 | 48 ms | 94 ms, spikes to 2.9 s | 98.7% |
| OpenVINO GenAI | NPU | int4 | 71 ms | 150 ms (10 s first load) | 98.7% |
| OpenVINO GenAI | CPU | int8 | 56 ms | 133 ms | 98.7% |
| llama.cpp OpenVINO build | NPU | Q4_0 | 258 ms | 522 ms | 97.4% |

Chosen: llama.cpp on the CPU. It is steady (no GPU wake-ups), loads in a second, uses about
125 MB of private memory, and the grammar keeps every plan well formed. OpenVINO's own grammar
support cut plans short, so its rows are without one.

```
python bench.py llama --build cpu        # tools/ holds the llama.cpp builds (not in git)
```
