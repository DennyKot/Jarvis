using System.Windows;
using Jarvis.App.Views;
using Jarvis.Core.Safety;

namespace Jarvis.App.Services;

/// <summary>Shows a confirmation card next to the avatar and waits for Allow / Deny.</summary>
public sealed class ConfirmationService(AvatarWindow avatar) : IConfirmationService
{
    public async Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken)
    {
        var window = await avatar.Dispatcher.InvokeAsync(() =>
        {
            var confirm = new ConfirmWindow(request.Action, request.Reason);
            confirm.Loaded += (_, _) =>
            {
                confirm.Left = Math.Max(SystemParameters.WorkArea.Left, avatar.Left + avatar.Width - confirm.ActualWidth - 20);
                confirm.Top = Math.Max(SystemParameters.WorkArea.Top, avatar.Top + avatar.Height - 200 - confirm.ActualHeight);
            };
            confirm.Show();
            return confirm;
        });

        await using var registration = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(() => window.Finish(false)));
        return await window.Result;
    }
}
