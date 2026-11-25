# Jarvis — voice-driven AI assistant for Windows (.NET)

Jarvis is an animated desktop helper that listens to voice commands (English and Ukrainian) and operates your computer for you:
it launches programs, drives **Google Chrome** (navigate, click, type, read pages), works with desktop apps such as
**VS Code, Word, Excel and Notepad** through Windows UI Automation, and reads the screen with screenshots + OCR.

While it works you see a second, **AI pointer** fly across the screen to what Jarvis is about to click or type into,
and an animated **robot character** that shows what it is doing (listening, thinking, acting, asking for permission...).

## Technology

| Concern | Choice |
|---|---|
| Runtime / UI | .NET 10, WPF (per-monitor DPI aware) |
| AI "brain" | `Microsoft.Extensions.AI` `IChatClient` with tool calling — OpenAI, Azure OpenAI, Anthropic or local Ollama |
| Chrome | `Microsoft.Playwright` attached to a real Chrome over the DevTools protocol (CDP) |
| Desktop apps | FlaUI (UI Automation 3) + Win32 `SendInput` |
| Screen reading | Screenshots for vision models + built-in `Windows.Media.Ocr` |
| Voice | NAudio microphone capture + Parakeet TDT 0.6B v3 via sherpa-onnx (Handy's model, local/offline); `Whisper.net` optional |
| Character | Built-in vector robot (WPF); optional Lottie clips via `SkiaSharp.Skottie` |

```
 Hotkey ─► Voice (NAudio → Parakeet/Whisper) ─► text ─► Agent loop (LLM + tools) ─► Safety policy ─► Confirm card
                                                     │
                     ┌───────────────────────────────┼─────────────────────────────┐
              Browser tools (Playwright/CDP)   Desktop tools (FlaUI, SendInput)   Screen tools (screenshot, OCR)
                                                     │
                                  AI pointer overlay + animated character
```

## Projects

| Path | Purpose |
|---|---|
| `src/Jarvis.App` | WPF host: character window, AI pointer overlay, confirmation card, command box, tray icon, hotkeys |
| `src/Jarvis.Core` | Options, LLM provider factory, tool contracts, risk policy, agent loop |
| `src/Jarvis.Voice` | Microphone recording with end-of-speech detection, Parakeet/Whisper transcription |
| `src/Jarvis.Automation.Browser` | Chrome connection and `browser_*` tools |
| `src/Jarvis.Automation.Desktop` | App launcher, windows, UI Automation, keyboard/mouse input, screenshots, OCR |
| `tests/Jarvis.Core.Tests` | xUnit v3 unit tests |

## Requirements

- Windows 10 2004+ / Windows 11, .NET 10 SDK
- Google Chrome (for web tasks)
- A microphone (for voice)
- One LLM provider (a model with tool calling; vision is recommended for screenshots)

## Setup

By default Jarvis uses local Ollama (`gemma4` at `http://localhost:11434`) — no API key needed.
On first start Jarvis shows a setup window with **Install Ollama and the model** checked: it installs Ollama
(winget, or the official installer as fallback), starts it and downloads the model. Rerun it anytime from the tray menu
(**Install Ollama + model**). To use a cloud provider:

1. Choose the model in [`src/Jarvis.App/appsettings.json`](src/Jarvis.App/appsettings.json) (`Jarvis:Llm`).
2. Store the API key outside of source control:

   ```powershell
   cd src/Jarvis.App
   dotnet user-secrets set "Jarvis:Llm:ApiKey" "<your key>"
   ```

   Or use environment variables: `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `AZURE_OPENAI_API_KEY`,
   or any setting as `Jarvis__Llm__ApiKey`, `Jarvis__Llm__Provider`, etc.

| Provider | `Provider` | `Model` example | `Endpoint` |
|---|---|---|---|
| OpenAI | `OpenAI` | `gpt-4.1` | optional (OpenAI-compatible URL) |
| Azure OpenAI | `AzureOpenAI` | your deployment name | `https://<resource>.openai.azure.com/` |
| Anthropic | `Anthropic` | a current Claude model id | – |
| Ollama (local, free) | `Ollama` | `gemma4`, `qwen3` | default `http://localhost:11434` |

3. Run:

   ```powershell
   dotnet run --project src/Jarvis.App
   ```

The first voice command downloads the Parakeet model (≈ 670 MB; or the Whisper model when `Voice:Engine` = `Whisper`) to `%LOCALAPPDATA%\Jarvis\models`.
The first web command starts Chrome with a dedicated profile in `%LOCALAPPDATA%\Jarvis\ChromeProfile`
(Chrome 136+ does not allow automation of your default profile) — sign in there once if a site needs your account.

## Using Jarvis

| Action | How |
|---|---|
| Talk | `Ctrl+Space` (press again to finish early), double-click the robot, or click the tray icon |
| Wake word (opt-in) | Say *"Hey Jarvis"* and then your command, or *"Hey Jarvis, open notepad"* in one go (`Voice:WakeWordEnabled`) |
| Type a command | `Ctrl+Alt+J` |
| Stop the current task | `Ctrl+Alt+X` |
| Undo Jarvis' last action | `Ctrl+Alt+Z`, *"undo that"*, or right-click → *Undo last action* (repeat to go further back) |
| Dry run (plan only, nothing is changed) | Start the command with *"dry run"*, e.g. *"dry run: send the report to Anna"*, or right-click → *Dry run a command* |
| Menu (new conversation, undo, dry run, settings, logs, exit) | Right-click the robot |
| Move the robot | Drag it |
| Script / shortcut | `Jarvis.exe --command "open notepad and type hello"` (or `--dry-run "..."` to only see the plan) |

Examples: *"Open Wikipedia and tell me the population of Kyiv"*, *"Відкрий Excel"*,
*"Open VS Code and open the command palette"*, *"Read what's on the screen"*.

### Safety

- `Safety:Mode` = `ConfirmRisky` (default) asks before clicks/typing on targets that look risky
  (delete, send, submit, pay, buy, publish, sign out... in English and Ukrainian), before typing into password fields,
  and for tools marked risky. `ConfirmAll` asks before every action; `Autonomous` never asks.
- The model's description of a target is double-checked against the element's real label.
- Unanswered confirmations are denied after `Safety:ConfirmationTimeout`.
- `Ctrl+Alt+X` cancels everything immediately.
- **Plan preview**: with `Safety:PreviewPlan` = `true` (in `ConfirmRisky`/`ConfirmAll`), every command is first planned
  with a dry run (read-only tools only; actions are recorded, not executed) and the planned steps are shown for approval.
  Nothing happens until you allow the plan; steps marked ⚠ still ask again before they run. Questions that need no
  actions are answered directly by the dry run.
- **Undo** reverts Jarvis' actions newest-first: closes tabs/windows it opened, returns to the previous page or tab,
  restores the previous text of fields it typed into (or presses `Ctrl+Z` in desktop apps) and the previous `<select>`
  choice. Clicks that don't navigate and key presses can't be undone automatically — undo then says so instead of
  reverting an older step. Password values are never kept for undo.

### Privacy

- Speech recognition runs locally (Parakeet or Whisper). Commands, page snapshots, UI trees and screenshots are sent to the
  configured LLM provider — use Ollama to keep everything on your machine.
- The robot and AI pointer are excluded from screen capture by default, so they never appear in screenshots sent to the
  model. Set `Jarvis:Ui:HideFromScreenCapture` to `false` to show them in screen sharing or recordings.
- Logs (tool names and arguments, with typed text redacted to its length) are in `%LOCALAPPDATA%\Jarvis\logs`.

## Configuration reference (`appsettings.json` → `Jarvis`)

| Key | Default | Notes |
|---|---|---|
| `Llm:MaxSteps` | 30 | Max tool-calling steps per command |
| `Llm:RunTimeout` | 5 min | Per command |
| `Llm:ContextLength` | 16384 | Context window requested from Ollama (it otherwise truncates long prompts) |
| `Llm:KeepAlive` | 30m | How long Ollama keeps the model loaded (`-1` = forever). Jarvis also preloads it at startup and when you press Talk |
| `Llm:HistoryMessageLimit` | 40 | Conversation memory between commands |
| `Llm:AllowMultipleToolCalls` | true | Lets the model batch independent actions (e.g. filling several form fields) in one step |
| `Llm:DynamicTools` | true | Sends a small core tool set first and adds the web/desktop tools once the model uses `browser_open` / `launch_app` / `focus_window` (saves tokens every step) |
| `User:Name` | auto | Your name. If not set, it is detected once at first start from the Windows account and Chrome/Edge Google profiles (by majority vote), cached in `%LOCALAPPDATA%\Jarvis\user-profile.json`; only the name goes into the prompt |
| `Voice:Engine` | `Hybrid` | `Hybrid` (Parakeet; Cyrillic results re-done by Whisper limited to `AllowedLanguages`, since Parakeet mistakes Ukrainian for Russian), `Parakeet` or `Whisper` |
| `Voice:WhisperModel` | `small` | `tiny`, `base`, `small`, `medium`, `large-v3`, `large-v3-turbo` (`medium`+ is better for Ukrainian) |
| `Voice:Language` / `AllowedLanguages` | `auto` / `en, uk` | Detection is restricted to the allowed languages |
| `Voice:SilenceTimeoutMs` | 1500 | Recording stops after this much silence |
| `Voice:WakeWordEnabled` / `WakeWords` | `false` / `hey jarvis, jarvis, джарвіс...` | Listen in the background (locally, paused while Jarvis works) and react to utterances that start with a wake phrase. Costs some CPU: every speech segment is transcribed |
| `Hotkeys:Talk/Type/Stop/Undo` | see above | Any `Ctrl/Alt/Shift/Win + Key` |
| `Safety:PreviewPlan` | false | Plan every command with a dry run and ask before running it (confirm modes only) |
| `Browser:Mode` | `JarvisProfile` | `JarvisProfile`: Jarvis' own Chrome window (DevTools). `Extension`: your Chrome via the Jarvis Bridge extension (recommended). `UserWindow`: your Chrome driven like a desktop app |
| `Browser:ExtensionPort` | 17345 | Local port the extension connects to |
| `Browser:DebugPort` | 9222 | Chrome remote debugging port |
| `Browser:SnapshotElementLimit` | 60 | Interactive elements per page snapshot |
| `Browser:ReadTextLimit` | 2500 | Max characters returned by `browser_read_text` / `web_lookup` |
| `Ui:HideFromScreenCapture` | true | See Privacy |
| `Ui:Sounds` | true | Cute robot chimes on listen / done / confirm / error |

## Working in your already-open Chrome

### Recommended: the Jarvis Bridge extension (`Browser:Mode = Extension`)

A small Manifest V3 extension lets Jarvis work in your normal Chrome (your tabs and logins) as fast and precisely as in
its own browser: same tools, same compact snapshots, and no need to bring Chrome to the front or type into it.

Install it (one time) in any of these ways:

- **Automatically**: the first browser command in `Extension` mode asks for permission and installs it.
- Right-click the robot (or the tray icon) → **Install Chrome extension**.
- From a terminal: `scripts\install-chrome-extension.ps1` or `Jarvis.exe --install-extension`.
- **Manually**: open `chrome://extensions`, turn on **Developer mode**, click **Load unpacked** and choose
  `%LOCALAPPDATA%\Jarvis\ChromeExtension\unpacked`.

The automatic installer clicks through exactly those manual steps (English and Ukrainian Chrome UI). Chrome offers no
silent way to add a local extension: `--load-extension` is ignored since Chrome 137 and registry-based installs on Windows
must come from the Chrome Web Store. Chrome may show a "developer mode extensions" notice on startup; that is expected.

Security: the extension connects only to `ws://localhost:17345`, and Jarvis accepts only connections whose origin is
this extension's fixed id and that present a random per-install token (stored in `%LOCALAPPDATA%\Jarvis\ChromeExtension`).
Limitations: clicks are dispatched from the page, so a few sites that require "trusted" input may ignore them;
extensions cannot run on `chrome://` pages or the Chrome Web Store. After Jarvis restarts, Chrome may take up to ~30 s
to wake the extension so it reconnects.

### Alternative: without an extension (`Browser:Mode = UserWindow`)

Set `Jarvis:Browser:Mode` to `UserWindow` to let Jarvis drive your Chrome like a desktop app:

- `browser_open` opens the URL in a **new tab** (your current tab is left alone) through the address bar.
- `browser_snapshot` lists the page's elements via Windows UI Automation (ids `[uN]`), used with `ui_click` / `ui_type`.
- `browser_read_text` reads the page text (UI Automation, falling back to OCR); with `query` it returns only matching paragraphs.
- When an element is not exposed, the model falls back to `ocr` + `click_at`, and to `screenshot` + `click_at` last.

Trade-offs: the Chrome window comes to the front, keyboard input goes to it (don't type while Jarvis works), and the
mouse is moved briefly only for coordinate clicks. It is a little less precise than `JarvisProfile` mode.

## Custom character animation

The built-in robot is drawn and animated in code. To use designer-made animations instead, drop Lottie JSON files
into [`src/Jarvis.App/Assets/Avatar`](src/Jarvis.App/Assets/Avatar) named after the states:
`idle.json`, `listening.json`, `transcribing.json`, `thinking.json`, `acting.json`, `confirming.json`, `speaking.json`, `error.json`.
Only `idle.json` is required; missing states fall back to it.

## Tools available to the model

- **Web**: `web_lookup` (searches Wikipedia + DuckDuckGo over HTTP and returns readable text; no browser needed)
- **Browser**: `browser_open`, `browser_snapshot`, `browser_click`, `browser_type`, `browser_select_option`,
  `browser_press`, `browser_scroll`, `browser_back`, `browser_read_text`, `browser_tabs`
- **Desktop**: `launch_app`, `list_windows`, `focus_window`, `ui_tree`, `ui_find`, `ui_click`, `ui_type`,
  `type_text`, `send_keys`, `screenshot`, `ocr`, `click_at`
- **Clipboard & files**: `clipboard_read`, `clipboard_write` (paste long text with ctrl+v instead of typing), `file_find` (name/wildcard search in Desktop, Documents, Downloads, profile)

## Speed and token usage

- Most of the time goes to the model: a cloud model (e.g. GPT-4.1-mini) answers each step in 1–2 s and needs fewer steps
  than a small local one. With Ollama, choose a model that fits in your GPU memory (`ollama ps` should show 100% GPU),
  and set the `OLLAMA_KEEP_ALIVE=30m` environment variable so the model is not reloaded after idle periods.
- Lowering `Llm:ContextLength` reduces Ollama's memory use (more of the model stays on the GPU), at the cost of shorter memory.
- Browser actions return only what changed on the page; full snapshots are sent only after navigation or `browser_snapshot`.
  Only the latest screenshot is kept, and older tool results are truncated.
- Factual questions take ~2 steps with `web_lookup`. `browser_type` with `submit=true` that lands on a new page returns
  its snapshot plus the start of its main text, and `browser_read_text(query=…)` returns only matching paragraphs.
- Tools are sent in groups (`Llm:DynamicTools`): desktop tools are not sent on web tasks and vice versa.
- Each step's token usage and duration are logged (`LLM step N: …, tokens in=… out=…`).

## Limitations

- Apps running as administrator cannot be controlled unless Jarvis also runs elevated.
- Browser pointer positions assume 100% page zoom.
- Small local models (≈8B) handle short tasks well but lose track on long multi-step ones; GPT-4.1/Claude-class models are much more reliable.
- Windows OCR needs the language pack of the text (e.g. Ukrainian) installed.

## Roadmap

1. Office deep integration (Excel ranges/formulas, Word formatting) via COM automation.
2. VS Code-specific tools (`code` CLI, command palette, active editor content).
3. Spoken replies (Windows TTS, Piper for Ukrainian) and an optional "Jarvis" wake word.
4. ~~Chrome extension bridge to work in your main Chrome profile.~~ Done (`Browser:Mode = Extension`).

## Development

```powershell
dotnet build Jarvis.sln
dotnet test --project tests/Jarvis.Core.Tests
```

## License

[PolyForm Noncommercial 1.0.0](LICENSE) — free for personal and other noncommercial use. Commercial use requires a separate license; contact the author.

