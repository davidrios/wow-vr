using System;
using System.Linq;
using WowVR.Ui;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--login-check[=Character]</c> logs in with <c>WOWVR_AC_USER</c> and <c>WOWVR_AC_PASS</c> to the
/// saved server or <c>--server=</c>'s, picks the character, or the first, enters the world, and
/// prints PASS once the server's creatures are placed, then quits. With <c>--relog</c> it then
/// presses the game menu's Character selection and passes once the character list is back, which
/// needs a character the server lets out at once, as in an inn. <c>--shots=folder</c> saves the
/// screens and the world, which show game assets, so don't commit them.
/// </summary>
public partial class LoginCheck : Node
{
    private const double GiveUpSeconds = 180;

    public required GlueScreens Screens { get; init; }
    public string? Character { get; init; }

    /// <summary>Survives the reload back to the character list.</summary>
    private static bool _relogging;

    private double _age;
    private string _step = "login";
    private double _stepAge;
    private string? _shots;

    public override void _Ready()
    {
        _shots = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--shots="))?["--shots=".Length..];
    }

    public override void _Process(double delta)
    {
        _age += delta;
        _stepAge += delta;
        if (_age > GiveUpSeconds)
        {
            Finish(false, $"stuck at {_step}");
            return;
        }

        var host = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--server="))?["--server=".Length..] ?? AppSettings.LoginHost;
        if (_relogging)
        {
            if (IsInstanceValid(Screens) && Screens.Panel?.Page == "characters" && _stepAge > 3)
            {
                Shot("relogged");
                Finish(true, "back at the character list from the game menu");
            }

            return;
        }

        switch (_step)
        {
            case "login" when IsInstanceValid(Screens) && Screens.Panel?.Page == "login" && _stepAge > 3:
                Shot("login");
                GD.Print($"Login check: logging in to {host}");
                Screens.Panel.FillLogin(host, System.Environment.GetEnvironmentVariable("WOWVR_AC_USER") ?? "", System.Environment.GetEnvironmentVariable("WOWVR_AC_PASS") ?? "");
                Next("characters");
                break;
            case "characters" when IsInstanceValid(Screens) && Screens.Panel.Page == "login" && Screens.Panel.Status.Length > 0:
                Finish(false, $"login refused: {Screens.Panel.Status}");
                break;
            case "characters" when IsInstanceValid(Screens) && Screens.Panel.Page == "characters":
                if (Character is not null && !Screens.Panel.ChooseCharacter(Character))
                {
                    Finish(false, $"no character named {Character}");
                    return;
                }

                Next("preview");
                break;
            case "preview" when _stepAge > 4:
                Shot("characters");
                Screens.Panel.OpenCreatePage();
                Next("create");
                break;
            case "create" when _stepAge > 4:
                if (Screens.Panel.Page != "create")
                {
                    Finish(false, "the new character page didn't open");
                    return;
                }

                Shot("create");
                Screens.Panel.LeaveCreatePage();
                Next("back");
                break;
            case "back" when _stepAge > 1 && Screens.Panel.Page == "characters":
                if (Character is not null)
                {
                    Screens.Panel.ChooseCharacter(Character);
                }

                GD.Print("Login check: entering the world");
                Screens.Panel.PressEnter();
                Next("world");
                break;
            case "menu" when _stepAge > 1:
                PressCharacterSelection();
                break;
            case "world":
                var server = GetParent().GetNodeOrNull<ServerWorld>("Server");
                var tiles = GetParent().GetNodeOrNull<TileManager>("Tiles");
                var loading = GetParent().GetNodeOrNull<LoadingScreen>("LoadingScreen");
                if (server is not null && tiles is not null && tiles.LoadedTileCount >= 9 && !tiles.IsBusy && server.Creatures > 0
                    && loading is not { Showing: true } && GetParent().GetNodeOrNull<Node3D>("Avatar/Body") is not null && _stepAge > 8)
                {
                    Shot("world");
                    if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--relog") >= 0)
                    {
                        GetParent().GetChildren().OfType<IPlayer>().First().SetMenuOpen(true);
                        Next("menu");
                        return;
                    }

                    Finish(true, $"in the world with {server.Creatures} creatures from the server");
                }

                break;
        }
    }

    private void PressCharacterSelection()
    {
        var player = GetParent().GetChildren().OfType<IPlayer>().First();
        Shot("menu");
        var button = player.Menu.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.Text == "Character selection");
        GD.Print("Login check: pressing Character selection");
        _relogging = true;
        Next("relog");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void Next(string step)
    {
        _step = step;
        _stepAge = 0;
    }

    private void Shot(string name)
    {
        if (_shots is not null)
        {
            var path = System.IO.Path.Combine(_shots, $"login-{name}.png");
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"Login check: saved {path}");
        }
    }

    private void Finish(bool passed, string why)
    {
        GD.Print($"Login check: {(passed ? "PASS" : "FAIL")}, {why}");
        GetTree().Quit(passed ? 0 : 1);
    }
}
