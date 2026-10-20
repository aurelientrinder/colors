# Chat: from a text box to an assistant

Vision: a back-and-forth with the local model, in the palette, with scrollable history. Answers are not
only text: "what's that file I was working on last week" shows the file inline, to click; "what's the
weather" shows a small weather widget. The model's thinking is shown with an animated brain. The model
remembers, short term (the conversation) and long term (facts about you).

Today (`ChatView.cs`, `chat.rs`): one card under a row; messages are plain text; the model runs with
thinking off; the conversation is lost when the palette closes; the model has no tools.

## Steps (each lands working and is committed on its own)

Status (2026-10-20): steps 1 to 7 done and installed. Since: answers come step by step on a timeline rail
(a sentence of intent, a line per tool, the answer), and the model answers from what its tools found, thinking
briefly over the results when the answer must be worked out (see "Grounding").

1. **Message model.** `ChatMsg` holds blocks (text, thinking, widget) instead of one string. Light
   markdown (bold, code, lists, links) in the text view. Scrolling keeps the place when you scroll up
   while the model is writing.
2. **Thinking.** Turn thinking on; stream `reasoning_content` apart from the answer. An animated brain
   while it thinks; afterwards a "Thought for 6 s" line that opens to the reasoning.
3. **Tools.** The model is offered tools (OpenAI-style `tools`, llama-server `--jinja`): `find_files`
   (by name, time, content), `weather`, `calendar`, `system`, `open`. The shell runs the call through the
   engine (`pc_tool`), feeds the result back, and loops until the model answers. Read-only tools only;
   anything that does something asks first.
4. **Inline widgets.** A tool's result becomes a block in the message: file cards (icon, name, folder,
   modified time; click opens, a second click reveals), the weather mini card, a calendar strip, a system
   gauge. Reuse the existing card renderers (`Answers.cs`, `CalendarWidget.cs`, `SystemWidget.cs`).
5. **Short-term memory.** Conversations are saved (`%LOCALAPPDATA%\palette\chats`), the last one is
   resumed when Tab is tapped again, a list of earlier ones, and the model gets a rolling summary when the
   history outgrows its context.
6. **Long-term memory.** Facts about you, kept as text plus an embedding (the NPU embedder already
   exists). The model writes them with a `remember` tool and reads them with `recall`; the best few go
   into every system message. A settings view to see, edit and forget them. Local only.
7. **Polish.** Regenerate / stop / copy, keyboard scrolling, a bigger panel for chat, error states,
   performance of long histories.

Order matters: 3 needs 1; 4 needs 3; 6 needs 5; 2 can run any time after 1.

## Grounding (2026-10-20)

8 questions built to tempt the model into inventing (an answer missing from the documents, an empty file
search, a duration the data does not hold) or needing a step of reasoning over a tool's result (days until
a date, a yearly sum, "should I take an umbrella"), each scored for the facts it must give and the ones it
must not invent:

| variant | right | time for the 8 |
|---|---|---|
| instructions as they were | 6/8 (rent 12,010 € for 12,300; no day count) | 30 s |
| + grounding rules | 6/8 (315 days given as 333) | 39 s |
| + rules + thinking over the results, 120 or 200 tokens | 7/8 (the day count) | 45–47 s |
| **+ rules + thinking over the results, 300 tokens** | **8/8** | 51 s |
| + rules + thinking over the results, 400 tokens | 8/8 | 97 s |

Chosen: the rules always (chat.rs GROUNDING), and a 300-token think after the tools only for questions whose
answer is worked out from the results (ChatView.cs WorkingOut: how many/much/long, totals, comparisons,
"should I", "is it enough"); plain lookups stay instant. The per-request limit is llama-server's
`thinking_budget_tokens`.

## Model choice (2026-10-20)

19 messages of the chat's kinds (greetings, facts, French, rewriting, sums, documents with known answers, a
compiler error on screen), each model run as the app runs it (llama.cpp Vulkan on the Arc 140V, thinking
only where the app turns it on, 400-token thinking budget):

| model | words/s | first word, no thinking | with thinking | answers |
|---|---|---|---|---|
| Qwen3.5 4B Q4_K_S | ~19 | 1–3 s | 23–30 s | good |
| Qwen3.5 4B Q4_0 | ~20 | same | same | same |
| Qwen3.5 2B Q4_0 | ~35 | 0.5 s | 13 s | invents things (dates, a wrong rewrite) |
| Gemma 4 E4B Q4_0 | ~12 | 1–2 s | 14–36 s | best |
| **Gemma 4 E4B Q4_0 + MTP draft** | **~25** | **1–2 s** | **4–23 s** | **best** (chosen) |

Tools: both 4B models picked the right tool for 7 of 7 messages (Gemma once the system message told it to
search documents before asking); Gemma picks in under a second, Qwen in about three. Gemma's costs: ~17 s
to load (Qwen 3 s) and ~4.5 GB. A thinking budget under 400 tokens got the train-time sum wrong.
