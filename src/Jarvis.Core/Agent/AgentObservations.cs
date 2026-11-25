using System.Collections.Concurrent;

namespace Jarvis.Core.Agent;

/// <summary>Images produced by tools (screenshots) that must be shown to the model after the tool results.</summary>
public sealed class AgentObservations
{
    private readonly ConcurrentQueue<ImageObservation> _images = new();

    public void AddImage(byte[] data, string mediaType, string caption) =>
        _images.Enqueue(new ImageObservation(data, mediaType, caption));

    public IReadOnlyList<ImageObservation> Drain()
    {
        var result = new List<ImageObservation>();
        while (_images.TryDequeue(out var image))
        {
            result.Add(image);
        }

        return result;
    }
}

public sealed record ImageObservation(byte[] Data, string MediaType, string Caption);
