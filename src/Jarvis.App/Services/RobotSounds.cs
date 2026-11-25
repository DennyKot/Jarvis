using System.IO;
using System.Media;
using Jarvis.Core.Agent;

namespace Jarvis.App.Services;

/// <summary>Cute chimes for avatar state changes. Sounds were generated with earshot (kalimba voice).</summary>
public sealed class RobotSounds
{
    private readonly Dictionary<string, SoundPlayer> _players = new();

    public RobotSounds(bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");
        foreach (var name in new[] { "listen", "thinking", "done", "confirm", "error" })
        {
            var path = Path.Combine(folder, name + ".wav");
            if (File.Exists(path))
            {
                var player = new SoundPlayer(path);
                player.Load();
                _players[name] = player;
            }
        }
    }

    public void OnStateChanged(AssistantState previous, AssistantState next)
    {
        var name = (previous, next) switch
        {
            (_, AssistantState.Listening) => "listen",
            (AssistantState.Listening or AssistantState.Transcribing, AssistantState.Thinking) => "thinking",
            (AssistantState.Thinking or AssistantState.Acting or AssistantState.Speaking, AssistantState.Idle) => "done",
            (_, AssistantState.Confirming) => "confirm",
            (_, AssistantState.Error) => "error",
            _ => null,
        };

        if (name is not null && _players.TryGetValue(name, out var player))
        {
            player.Play();
        }
    }
}
