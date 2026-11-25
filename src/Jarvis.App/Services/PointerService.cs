using System.Windows;
using System.Windows.Threading;
using Jarvis.App.Views;
using Jarvis.Core.Tools;

namespace Jarvis.App.Services;

/// <summary>Thread-safe bridge from automation tools to the pointer overlay on the UI thread.</summary>
public sealed class PointerService(PointerOverlayWindow overlay) : IPointerService
{
    public event Action<ScreenRect>? Pointing;

    public async Task PointAsync(ScreenRect target, PointerAction action, string? label, CancellationToken cancellationToken)
    {
        Pointing?.Invoke(target);
        var animation = await overlay.Dispatcher.InvokeAsync(
            () => overlay.AnimateAsync(target, action, label), DispatcherPriority.Normal, cancellationToken);
        await animation.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
    }

    public void SetHome(Point physical) => overlay.Dispatcher.BeginInvoke(() => overlay.HomePhysical = physical);
}
