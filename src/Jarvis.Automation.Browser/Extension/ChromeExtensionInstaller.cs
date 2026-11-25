using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Jarvis.Automation.Desktop.Input;
using Jarvis.Automation.Desktop.UiAutomation;
using Jarvis.Automation.Desktop.Windows;
using Jarvis.Core.Agent;
using Microsoft.Extensions.Logging;

namespace Jarvis.Automation.Browser.Extension;

/// <summary>
/// Installs the unpacked Jarvis Bridge extension into the user's running Chrome by operating chrome://extensions
/// (Developer mode → Load unpacked → pick folder). Chrome offers no silent way to do this for local extensions:
/// --load-extension is ignored by Chrome 137+ and registry-based installs must come from the Chrome Web Store.
/// </summary>
public sealed class ChromeExtensionInstaller(
    ExtensionBridge bridge,
    WindowService windows,
    UiAutomationService uia,
    InputSimulator input,
    AssistantStatus status,
    ILogger<ChromeExtensionInstaller> logger)
{
    private static readonly string[] DevModeNames = ["Developer mode", "Режим розробника", "Режим разработчика"];
    private static readonly string[] LoadUnpackedNames =
        ["Load unpacked", "Завантажити розпаковане розширення", "Завантажити розпаковане", "Загрузить распакованное расширение"];
    private static readonly string[] FolderFieldNames = ["Folder:", "Папка:"];

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string ExtensionDirectory => Path.Combine(ExtensionIdentity.DefaultDirectory, "unpacked");

    public string PrepareFiles() => ExtensionFiles.Write(bridge.Identity, bridge.Port, ExtensionIdentity.DefaultDirectory);

    public string ManualSteps =>
        $"To add it manually: open chrome://extensions, turn on Developer mode, click \"Load unpacked\" and choose {ExtensionDirectory}";

    public async Task<string> InstallAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var directory = PrepareFiles();
            bridge.Start();
            // An installed extension may need up to ~30 s to wake up and reconnect after Jarvis restarts.
            if (await bridge.WaitForConnectionAsync(bridge.WasEverConnected ? TimeSpan.FromSeconds(35) : TimeSpan.FromSeconds(3), cancellationToken))
            {
                return "The Jarvis Chrome extension is already installed and connected.";
            }

            status.ReportActivity("Installing the Jarvis extension in Chrome…");
            var chrome = await OpenExtensionsPageAsync(cancellationToken);
            var root = uia.Automation.FromHandle(chrome.Handle);

            var loadUnpacked = await FindAsync(root, "loadUnpacked", LoadUnpackedNames, TimeSpan.FromSeconds(3), cancellationToken);
            if (loadUnpacked is null)
            {
                var devMode = await FindAsync(root, "devMode", DevModeNames, TimeSpan.FromSeconds(8), cancellationToken)
                    ?? throw new InvalidOperationException($"Could not find the Developer mode switch. {ManualSteps}");
                await ClickAsync(devMode);
                loadUnpacked = await FindAsync(root, "loadUnpacked", LoadUnpackedNames, TimeSpan.FromSeconds(6), cancellationToken)
                    ?? throw new InvalidOperationException($"Could not find the \"Load unpacked\" button. {ManualSteps}");
            }

            await ClickAsync(loadUnpacked);
            await ChooseFolderAsync(chrome.Handle, directory, cancellationToken);

            if (!await bridge.WaitForConnectionAsync(TimeSpan.FromSeconds(20), cancellationToken))
            {
                return $"The extension was loaded but has not connected yet. Check chrome://extensions for errors. {ManualSteps}";
            }

            CloseExtensionsTab(chrome.Handle);
            return "Installed the Jarvis extension in Chrome; it is connected.";
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<WindowInfo> OpenExtensionsPageAsync(CancellationToken cancellationToken)
    {
        var chrome = FindChrome();
        if (chrome is null)
        {
            using (Process.Start(new ProcessStartInfo("chrome") { UseShellExecute = true }))
            {
            }

            for (var i = 0; i < 40 && chrome is null; i++)
            {
                await Task.Delay(250, cancellationToken);
                chrome = FindChrome();
            }

            if (chrome is null)
            {
                throw new InvalidOperationException("Google Chrome did not open.");
            }
        }

        windows.Activate(chrome.Handle);
        await Task.Delay(200, cancellationToken);
        input.SendKeys("ctrl+t");
        await Task.Delay(400, cancellationToken);
        input.TypeText("chrome://extensions");
        input.SendKeys("delete enter");
        await Task.Delay(1500, cancellationToken);
        return chrome;
    }

    private WindowInfo? FindChrome() =>
        windows.ListWindows().FirstOrDefault(w => w.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase));

    private async Task ChooseFolderAsync(nint chromeHandle, string directory, CancellationToken cancellationToken)
    {
        // Chrome shows the picker from a helper process, and UI Automation lists owned dialogs under their owner window,
        // so look both at top-level windows and inside the Chrome window, for any chrome.exe process.
        var chromeRoot = uia.Automation.FromHandle(chromeHandle);
        var dialog = await WaitForAsync(() => uia.Automation.GetDesktop().FindAllChildren(cf => cf.ByClassName("#32770"))
            .Concat(chromeRoot.FindAllChildren(cf => cf.ByClassName("#32770")))
            .FirstOrDefault(w => IsChromeProcess(Safe(() => w.Properties.ProcessId.Value))), TimeSpan.FromSeconds(15), cancellationToken)
            ?? throw new InvalidOperationException($"The folder picker did not appear. {ManualSteps}");

        var field = FindFolderField(dialog);
        if (field is not null && field.Patterns.Value.IsSupported)
        {
            field.Patterns.Value.Pattern.SetValue(directory);
        }
        else
        {
            // The folder box has keyboard focus when the dialog opens.
            input.SendKeys("ctrl+a");
            input.TypeText(directory);
        }

        // "Select Folder" (IDOK). Typing a path may first navigate into it; the second press then selects it.
        for (var attempt = 0; attempt < 3 && !IsClosed(chromeHandle, dialog) && !bridge.IsConnected; attempt++)
        {
            var select = dialog.FindFirstDescendant(cf => cf.ByAutomationId("1").And(cf.ByControlType(ControlType.Button)));
            if (select is not null)
            {
                await ClickAsync(select);
            }
            else
            {
                input.SendKeys("enter");
            }

            await Task.Delay(800, cancellationToken);
        }

        if (!IsClosed(chromeHandle, dialog) && !await bridge.WaitForConnectionAsync(TimeSpan.FromSeconds(3), cancellationToken))
        {
            throw new InvalidOperationException($"Could not choose the extension folder. {ManualSteps}");
        }
    }

    private static bool IsChromeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static AutomationElement? FindFolderField(AutomationElement dialog)
    {
        foreach (var id in new[] { "1152", "1148" })
        {
            var byId = dialog.FindFirstDescendant(cf => cf.ByAutomationId(id).And(cf.ByControlType(ControlType.Edit)));
            if (byId is not null)
            {
                return byId;
            }
        }

        return dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
            .FirstOrDefault(e => FolderFieldNames.Contains(Safe(() => e.Name) ?? string.Empty, StringComparer.OrdinalIgnoreCase));
    }

    private bool IsClosed(nint chromeHandle, AutomationElement dialog)
    {
        var handle = Safe(() => dialog.Properties.NativeWindowHandle.Value);
        return handle == 0 || !uia.Automation.GetDesktop().FindAllChildren(cf => cf.ByClassName("#32770"))
            .Concat(Safe(() => uia.Automation.FromHandle(chromeHandle).FindAllChildren(cf => cf.ByClassName("#32770"))) ?? [])
            .Any(w => Safe(() => w.Properties.NativeWindowHandle.Value) == handle);
    }

    private void CloseExtensionsTab(nint chromeHandle)
    {
        var chrome = windows.ListWindows().FirstOrDefault(w => w.Handle == chromeHandle);
        if (chrome is not null && (chrome.Title.Contains("Extensions", StringComparison.OrdinalIgnoreCase) ||
                                   chrome.Title.Contains("Розширення", StringComparison.OrdinalIgnoreCase)))
        {
            windows.Activate(chromeHandle);
            input.SendKeys("ctrl+w");
        }
    }

    private static async Task<AutomationElement?> FindAsync(
        AutomationElement root, string automationId, string[] names, TimeSpan timeout, CancellationToken cancellationToken) =>
        await WaitForAsync(() =>
        {
            var element = root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            foreach (var name in names)
            {
                element ??= root.FindFirstDescendant(cf => cf.ByName(name));
            }

            return element;
        }, timeout, cancellationToken);

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var value = Safe(probe);
            if (value is not null || DateTime.UtcNow >= deadline)
            {
                return value;
            }

            await Task.Delay(300, cancellationToken);
        }
    }

    private async Task ClickAsync(AutomationElement element)
    {
        // Invoke can block while the modal folder dialog it opens is visible, so never wait on it for long.
        var invoked = Task.Run(() =>
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return true;
            }

            if (element.Patterns.Toggle.IsSupported)
            {
                element.Patterns.Toggle.Pattern.Toggle();
                return true;
            }

            return false;
        });

        try
        {
            if (await invoked.WaitAsync(TimeSpan.FromSeconds(3)))
            {
                await Task.Delay(400);
                return;
            }
        }
        catch (TimeoutException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Pattern click failed, using the mouse");
        }

        var bounds = element.BoundingRectangle;
        input.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        await Task.Delay(400);
    }

    private static T? Safe<T>(Func<T?> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception)
        {
            return default;
        }
    }
}
