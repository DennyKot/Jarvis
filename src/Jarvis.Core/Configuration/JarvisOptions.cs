namespace Jarvis.Core.Configuration;

public sealed class JarvisOptions
{
    public const string SectionName = "Jarvis";

    public LlmOptions Llm { get; set; } = new();
    public VoiceOptions Voice { get; set; } = new();
    public HotkeyOptions Hotkeys { get; set; } = new();
    public BrowserOptions Browser { get; set; } = new();
    public SafetyOptions Safety { get; set; } = new();
    public UiOptions Ui { get; set; } = new();
    public UserOptions User { get; set; } = new();
}

public sealed class UiOptions
{
    /// <summary>
    /// Keeps the avatar and AI pointer out of screenshots (the model's and screen sharing).
    /// Turn off to show Jarvis in Teams/Zoom shares or screen recordings.
    /// </summary>
    public bool HideFromScreenCapture { get; set; } = true;

    /// <summary>Plays short robot chimes on state changes (listening, done, confirm, error).</summary>
    public bool Sounds { get; set; } = true;
}

public enum LlmProvider
{
    OpenAI,
    AzureOpenAI,
    Anthropic,
    Ollama,
}

public sealed class LlmOptions
{
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;
    public string Model { get; set; } = "gemma4";

    /// <summary>Custom endpoint: Azure resource URL, Ollama URL or an OpenAI-compatible base URL.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Keep this in user-secrets or environment variables, never in appsettings.json.</summary>
    public string? ApiKey { get; set; }

    public int MaxSteps { get; set; } = 30;
    public float Temperature { get; set; } = 0.2f;
    public int MaxOutputTokens { get; set; } = 2048;
    public int HistoryMessageLimit { get; set; } = 40;
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Context window requested from Ollama (num_ctx). Ignored by cloud providers.</summary>
    public int ContextLength { get; set; } = 16384;
}

public sealed class UserOptions
{
    /// <summary>Overrides the auto-discovered name (Windows account / Chrome profiles).</summary>
    public string? Name { get; set; }
}

public enum SpeechEngine
{
    /// <summary>Parakeet first; Cyrillic results are re-transcribed by Whisper restricted to AllowedLanguages (Parakeet confuses uk with ru).</summary>
    Hybrid,
    /// <summary>NVIDIA Parakeet TDT 0.6B v3 via sherpa-onnx (the model Handy uses): fast on CPU, 25 European languages.</summary>
    Parakeet,
    Whisper,
}

public sealed class VoiceOptions
{
    public SpeechEngine Engine { get; set; } = SpeechEngine.Hybrid;

    /// <summary>tiny, base, small, medium, large-v3, large-v3-turbo.</summary>
    public string WhisperModel { get; set; } = "small";

    /// <summary>"auto" or an ISO code such as "en" / "uk".</summary>
    public string Language { get; set; } = "auto";

    public string[] AllowedLanguages { get; set; } = ["en", "uk"];
    public int SilenceTimeoutMs { get; set; } = 1500;
    public int MaxRecordingSeconds { get; set; } = 30;
    public float SilenceThreshold { get; set; } = 0.015f;
    public string? ModelDirectory { get; set; }
}

public sealed class HotkeyOptions
{
    public string Talk { get; set; } = "Ctrl+Space";
    public string Type { get; set; } = "Ctrl+Alt+J";
    public string Stop { get; set; } = "Ctrl+Alt+X";
}

public enum BrowserMode
{
    /// <summary>Jarvis' own Chrome window (separate profile), driven over the DevTools protocol: fast and precise.</summary>
    JarvisProfile,

    /// <summary>The user's already-open Chrome, driven like a desktop app (UI Automation, keyboard, OCR, screenshots).</summary>
    UserWindow,

    /// <summary>The user's already-open Chrome through the Jarvis Bridge extension: fast and precise, works in the user's profile.</summary>
    Extension,
}

public sealed class BrowserOptions
{
    public BrowserMode Mode { get; set; } = BrowserMode.JarvisProfile;

    public string? ChromePath { get; set; }
    public int DebugPort { get; set; } = 9222;

    /// <summary>Local WebSocket port the Jarvis Bridge extension connects to.</summary>
    public int ExtensionPort { get; set; } = 17345;

    /// <summary>Chrome 136+ refuses remote debugging on the default profile, so Jarvis uses its own.</summary>
    public string? ProfileDirectory { get; set; }

    public int SnapshotElementLimit { get; set; } = 100;
    public int ReadTextLimit { get; set; } = 4000;
}

public enum SafetyMode
{
    ConfirmRisky,
    ConfirmAll,
    Autonomous,
}

public sealed class SafetyOptions
{
    public SafetyMode Mode { get; set; } = SafetyMode.ConfirmRisky;
    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public string[] RiskyKeywords { get; set; } =
    [
        "delete", "remove", "erase", "discard", "send", "submit", "post", "publish", "pay", "buy", "purchase",
        "order", "checkout", "transfer", "confirm", "sign out", "log out", "uninstall", "format", "reset",
        "видалити", "вилучити", "надіслати", "відправити", "оплатити", "купити", "замовити", "підтвердити", "опублікувати",
    ];
}
