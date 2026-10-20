# Chat: from a text box to an assistant

Vision: a back-and-forth with the local model, in the palette, with scrollable history. Answers are not
only text: "what's that file I was working on last week" shows the file inline, to click; "what's the
weather" shows a small weather widget. The model's thinking is shown with an animated brain. The model
remembers, short term (the conversation) and long term (facts about you).

Today (`ChatView.cs`, `chat.rs`): one card under a row; messages are plain text; the model runs with
thinking off; the conversation is lost when the palette closes; the model has no tools.

## Steps (each lands working and is committed on its own)

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
