using System.Diagnostics;
using System.IO;
using System.Windows;
using Jarvis.App.Logging;
using Jarvis.App.Views;
using Jarvis.Automation.Browser.Extension;
using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Jarvis.Core.Llm;
using Jarvis.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Services;

/// <summary>Glues hotkeys, voice, the agent and the UI together.</summary>
public sealed class JarvisController(
    AvatarWindow avatar,
    PointerService pointer,
    AssistantStatus status,
    VoiceInput voice,
    WakeWordListener wakeWord,
    IAgentRunner agent,
    GlobalHotkeys hotkeys,
    ChromeExtensionInstaller extensionInstaller,
    ILlmWarmup warmup,
    IOptions<JarvisOptions> options,
    ILogger<JarvisController> logger)
{
    private CancellationTokenSource? _run;

    public bool IsBusy => _run is not null;

    public void Start()
    {
        status.StateChanged += state => OnUi(() => avatar.SetState(state));
        status.ActivityChanged += activity => OnUi(() => avatar.ShowActivity(activity));
        status.AudioLevelChanged += level => OnUi(() => avatar.SetAudioLevel(level));
        status.MessagePosted += (text, isUser) => OnUi(() =>
        {
            if (isUser)
            {
                avatar.ShowUserText(text);
            }
            else
            {
                avatar.ShowReply(text);
            }
        });
        status.ReplyStreamed += text => OnUi(() => avatar.ShowPartialReply(text));
        pointer.Pointing += target => OnUi(() => avatar.LookAt(target));

        avatar.TalkRequested += Talk;
        avatar.TypeRequested += ShowCommandWindow;
        avatar.StopRequested += Stop;
        avatar.ResetRequested += ResetConversation;
        avatar.ExtensionInstallRequested += InstallExtension;
        avatar.SettingsRequested += OpenSettings;
        avatar.LogsRequested += () => OpenPath(FileLoggerProvider.DefaultDirectory);
        avatar.ExitRequested += () => Application.Current.Shutdown();
        avatar.Moved += (_, _) => pointer.SetHome(avatar.CharacterCenterPhysical);

        var keys = options.Value.Hotkeys;
        var failed = new List<string>();
        if (!hotkeys.Register(keys.Talk, Talk)) failed.Add(keys.Talk);
        if (!hotkeys.Register(keys.Type, ShowCommandWindow)) failed.Add(keys.Type);
        if (!hotkeys.Register(keys.Stop, Stop)) failed.Add(keys.Stop);

        wakeWord.Detected += OnWakeWord;
        wakeWord.Start();
        var talkHint = wakeWord.IsEnabled ? $"Say \"Hey Jarvis\" or press {keys.Talk}" : $"Press {keys.Talk}";

        avatar.ShowReply(failed.Count == 0
            ? $"Hi! I'm Jarvis. {talkHint} and speak, or {keys.Type} to type."
            : $"Hi! I'm Jarvis. These hotkeys are taken by other apps: {string.Join(", ", failed)}. Change them in appsettings.json.");

        warmup.WarmUp();
    }

    public void Talk()
    {
        if (voice.IsListening)
        {
            voice.StopListening();
            return;
        }

        if (IsBusy)
        {
            avatar.ShowActivity($"I'm still working. Press {options.Value.Hotkeys.Stop} to stop me.");
            return;
        }

        // Load the model while the user is still speaking.
        warmup.WarmUp();
        _ = RunAsync(async ct =>
        {
            var transcript = await voice.ListenAsync(ct);
            if (string.IsNullOrWhiteSpace(transcript.Text))
            {
                return null;
            }

            return transcript.Text;
        });
    }

    private void OnWakeWord(string command)
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            OnUi(Talk);
        }
        else
        {
            OnUi(() => Submit(command));
        }
    }

    public void Submit(string command)
    {
        if (IsBusy)
        {
            avatar.ShowActivity($"I'm still working. Press {options.Value.Hotkeys.Stop} to stop me.");
            return;
        }

        _ = RunAsync(_ => Task.FromResult<string?>(command));
    }

    public void InstallExtension()
    {
        if (IsBusy)
        {
            avatar.ShowActivity($"I'm still working. Press {options.Value.Hotkeys.Stop} to stop me.");
            return;
        }

        _ = InstallExtensionAsync();
    }

    private async Task InstallExtensionAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _run = cts;
        wakeWord.Suspend();
        try
        {
            status.SetState(AssistantState.Acting);
            var message = await Task.Run(() => extensionInstaller.InstallAsync(cts.Token), cts.Token);
            status.SetState(AssistantState.Idle);
            OnUi(() => avatar.ShowReply(message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Extension install failed");
            status.SetState(AssistantState.Error);
            OnUi(() => avatar.ShowReply(ex.GetBaseException().Message, isError: true));
            await Task.Delay(2500);
            status.SetState(AssistantState.Idle);
        }
        finally
        {
            _run = null;
            wakeWord.Resume();
        }
    }

    public void Stop()
    {
        voice.StopListening();
        _run?.Cancel();
    }

    private async Task RunAsync(Func<CancellationToken, Task<string?>> getCommand)
    {
        using var cts = new CancellationTokenSource();
        _run = cts;
        wakeWord.Suspend();
        try
        {
            var command = await getCommand(cts.Token);
            if (command is null)
            {
                status.SetState(AssistantState.Idle);
                OnUi(() => avatar.ShowReply("Sorry, I didn't catch that."));
                return;
            }

            status.PostUserMessage(command);
            await Task.Run(() => agent.RunAsync(command, cts.Token), cts.Token);
            status.SetState(AssistantState.Idle);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            status.SetState(AssistantState.Idle);
            OnUi(() => avatar.ShowReply("Stopped."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command failed");
            var message = ex is OperationCanceledException
                ? "The AI model took too long to respond. Try again, or use a faster model."
                : ex.GetBaseException().Message;
            status.SetState(AssistantState.Error);
            OnUi(() => avatar.ShowReply(message, isError: true));
            await Task.Delay(2500);
            status.SetState(AssistantState.Idle);
        }
        finally
        {
            _run = null;
            wakeWord.Resume();
        }
    }

    private void ShowCommandWindow()
    {
        OnUi(() =>
        {
            var window = new CommandWindow();
            window.Submitted += Submit;
            window.Show();
        });
    }

    private void ResetConversation()
    {
        agent.ResetConversation();
        avatar.ShowReply("Okay, starting a new conversation.");
    }

    private void OpenSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        OpenPath(path);
    }

    private void OpenPath(string path)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            logger.LogWarning(ex, "Could not open {Path}", path);
        }
    }

    private void OnUi(Action action)
    {
        if (avatar.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            avatar.Dispatcher.BeginInvoke(action);
        }
    }
}
