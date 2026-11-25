# AGENTS.md — Jarvis

Voice-driven Windows desktop assistant: .NET 10 + WPF. An LLM agent loop calls tools that drive Chrome and desktop apps;
an animated robot + "AI pointer" overlay visualize it. See README.md for user docs.

## Commands
```powershell
dotnet build Jarvis.sln
dotnet test --project tests/Jarvis.Core.Tests        # MTP runner (global.json); not `dotnet test <proj>`
dotnet run --project src/Jarvis.App [-- --command "..."] [-- --install-extension]
```
- Stop a running `Jarvis.exe` before building (it locks DLLs). Single instance (mutex).
- Quick live test without keys: `$env:Jarvis__Llm__Provider='Ollama'; $env:Jarvis__Llm__Model='gemma4'`, then `Jarvis.exe --command "..."`.
- Logs: `%LOCALAPPDATA%\Jarvis\logs\jarvis-yyyyMMdd.log` (UTF-8; read with `-Encoding UTF8`). Each LLM step logs time + tokens.

## Layout
| Project | Contents |
|---|---|
| `src/Jarvis.Core` | `JarvisOptions` (all config), `ChatClientFactory` (OpenAI/Azure/Anthropic/Ollama via Microsoft.Extensions.AI), `AgentRunner` (loop, history, compaction), `SystemPrompt`, `RiskPolicy`/`ActionGuard`, `JarvisTool`/`IToolProvider`, `IPointerService`, `IWindowActivator`, `AssistantStatus` (UI events) |
| `src/Jarvis.Voice` | NAudio recorder + `SpeechEndDetector`, `ISpeechTranscriber` → `ParakeetTranscriber` (sherpa-onnx, default) or `WhisperTranscriber` (`Voice:Engine`; models auto-download) |
| `src/Jarvis.Automation.Desktop` | Win32 interop (`LibraryImport`), `InputSimulator` (SendInput), `WindowService`, `AppLauncher`, `UiAutomationService` (FlaUI, ids `uN`), screenshot/OCR, `DesktopToolProvider` (+ UserWindow browser tools) |
| `src/Jarvis.Automation.Browser` | `ChromeSession` + `BrowserToolProvider` (CDP/Playwright, own profile), `PageScripts` (snapshot JS, refs `eN`), `SnapshotDiff`, `Extension/` (MV3 extension sources, `ExtensionBridge` WebSocket server, installer, extension tool provider) |
| `src/Jarvis.App` | WPF host: `App` (DI/host, tray, CLI), `JarvisController` (hotkeys→voice→agent), `AvatarWindow` + `JarvisCharacter` (procedural robot), `PointerOverlayWindow`, confirm/command windows |
| `tests/Jarvis.Core.Tests` | xUnit v3 + NSubstitute; covers all projects except App |

## Key conventions
- **Tools**: `JarvisTool.Create(method, name, description, ToolRisk, descriptiveArgs...)`. Every click/type tool takes an `element` description (risk keyword check). Keep descriptions short — they're sent every step.
- **Browser modes** (`Jarvis:Browser:Mode`): `JarvisProfile` (CDP, own profile), `Extension` (user's Chrome via bridge), `UserWindow` (UIA/keyboard). Exactly one provider exposes `browser_*` tools per mode; `SystemPrompt` switches guidance.
- **Token budget**: action tools return snapshot *diffs* (refs are stable per document via `window.__jarvisNext`); full snapshot only on open/back/tab switch/`browser_snapshot`. Older tool results truncated (600 chars), only latest screenshot kept, system prompt has date only (cache-friendly). Tools are grouped (`Agent/ToolGroups.cs`): core set first, web/desktop groups revealed on first use (`Llm:DynamicTools`); `web_lookup` answers facts without the browser. Measure with the token log after changes.
- **Safety**: risky actions go through `IActionGuard` (UI confirm). Real element names are re-checked, not just the model's description. Never log typed text (redacted to length).
- **Undo / dry run**: `AgentRunner` records every non-read-only tool call in `UndoJournal`; a tool calls `undo?.SetUndo(...)` to make its action revertible (or `SkipRecording()` for no-ops like scroll), otherwise the entry is a barrier. `PreviewAsync` = dry run: action tools are simulated and collected as an `AgentPlan`, and the turn isn't kept in history.
- **Coordinates**: app is PerMonitorV2 DPI-aware; everything uses physical pixels. Overlay/avatar are excluded from capture by default (`Ui:HideFromScreenCapture`).
- **Secrets**: API keys only via user-secrets (`Jarvis:Llm:ApiKey`) or env vars. Packages: central (`Directory.Packages.props`), feeds pinned to nuget.org (`nuget.config`).
- **Tests**: name `Method_Result_Condition`, bodies with `// Arrange`, `// Act`, `// Assert`.

## Gotchas
- Windows 11 Notepad/WinUI garble batched `SendInput` unicode → type one char per `SendInput`.
- Chromium/Electron (VS Code, Chrome) build the UIA tree lazily → retry once if (almost) empty.
- Ollama: default context is small and silently truncates → `NumCtx` = `Llm:ContextLength`; HttpClient timeout = `RunTimeout`.
- Ollama turns thinking on by default for models that support it (gemma4: ~3x output tokens per step) → `Llm:Thinking` (default off) sends `think` on the raw `ChatRequest`. Don't use `OllamaOption.Think` (OllamaSharp 5.5.2 converts it to a string).
- H.NotifyIcon: `IconSource` only works for file/pack-URI images (throws for `RenderTargetBitmap`) → set `TaskbarIcon.Icon`; it disposes the previous `Icon`, so hand it a new instance each time.
- Chrome 136+ blocks CDP on the default profile; Chrome 137+ ignores `--load-extension`. Extension install = UIA clicks on chrome://extensions (EN/UK names + ids `devMode`, `loadUnpacked`); folder picker is a `#32770` from a *helper* chrome process, listed under the Chrome window.
- MV3 service worker sleeps; extension reconnects via alarms (≤30 s). `connected.marker` means "installed".
- WPF projects lack `System.IO` implicit using; `LibraryImport` needs blittable types (`ushort` not `char`) and `SetLastError`.
- File-based `dotnet run x.cs` probes need `#:property PublishAot=false` (+ `JsonSerializerIsReflectionEnabledByDefault=true`).
