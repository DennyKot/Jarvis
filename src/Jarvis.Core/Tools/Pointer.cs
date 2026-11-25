namespace Jarvis.Core.Tools;

/// <summary>A rectangle in physical screen pixels (virtual-screen coordinates).</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static ScreenRect FromPoint(double x, double y) => new(x - 1, y - 1, 2, 2);

    public override string ToString() => $"({X:0},{Y:0} {Width:0}x{Height:0})";
}

public enum PointerAction
{
    Move,
    Click,
    DoubleClick,
    RightClick,
    Type,
    Scroll,
}

/// <summary>Draws the visual "AI pointer". It never moves the real mouse cursor.</summary>
public interface IPointerService
{
    Task PointAsync(ScreenRect target, PointerAction action, string? label, CancellationToken cancellationToken);
}

public sealed class NullPointerService : IPointerService
{
    public Task PointAsync(ScreenRect target, PointerAction action, string? label, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
