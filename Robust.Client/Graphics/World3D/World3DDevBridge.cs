using System;
using System.IO;
using Robust.Client.Console;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using SixLabors.ImageSharp;

namespace Robust.Client.Graphics.World3D;

/// <summary>
///     Dev-only command bridge for automated testing without driving the keyboard or stealing window focus.
///     When <c>render3d.dev_cmd_file</c> is set, every frame the client runs (then deletes) the console
///     commands in that file, one per line. Off by default.
/// </summary>
internal sealed class World3DDevBridge
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IClientConsoleHost _console = default!;
    [Dependency] private ILogManager _log = default!;

    private string _path = "";
    private bool _initialized;

    public void Poll()
    {
        if (!_initialized)
        {
            _initialized = true;
            _cfg.OnValueChanged(CVars.Render3DDevCmdFile, v => _path = v, true);
        }

        if (_path.Length == 0 || !File.Exists(_path))
            return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(_path);
            File.Delete(_path);
        }
        catch (IOException)
        {
            return; // Still being written; try next frame.
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            _log.GetSawmill("world3d.dev").Info($"exec: {line}");
            _console.ExecuteCommand(line);
        }
    }
}

/// <summary>Saves a screenshot of the final frame to an absolute path (dev testing).</summary>
internal sealed class World3DShotCommand : IConsoleCommand
{
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private ILogManager _log = default!;

    public string Command => "fps3d_shot";
    public string Description => "Save a PNG screenshot of the next frame to the given absolute path.";
    public string Help => "Usage: fps3d_shot <path.png>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteLine(Help);
            return;
        }

        var path = args[0];
        _clyde.Screenshot(ScreenshotType.Final, image =>
        {
            try
            {
                image.SaveAsPng(path);
                _log.GetSawmill("world3d.dev").Info($"shot: {path}");
            }
            catch (Exception e)
            {
                _log.GetSawmill("world3d.dev").Error($"shot failed: {e.Message}");
            }
            finally
            {
                image.Dispose();
            }
        });
    }
}
