using System;
using System.IO;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats.Assets;
using WowVR.Formats.Textures;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// Shows a client texture on desktop:
/// godot --path godot res://scenes/debug/TextureViewer.tscn -- --texture=&lt;file&gt;
/// </summary>
public partial class TextureViewer : TextureRect
{
    private const string TextureArgument = "--texture=";
    private const string DefaultTexture = @"Interface\Icons\INV_Misc_QuestionMark.blp";

    public override async void _Ready()
    {
        var file = DefaultTexture;
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(TextureArgument))
            {
                file = argument[TextureArgument.Length..];
            }
        }

        var clientPath = AppSettings.ClientPath;
        try
        {
            var image = await Task.Run(() => Load(clientPath, file));
            Texture = ImageTexture.CreateFromImage(image);
            GD.Print($"Showing {file} ({image.GetWidth()}x{image.GetHeight()})");
        }
        catch (Exception e)
        {
            GD.PushError($"Could not load {file}: {e.Message}");
        }
    }

    private static Image Load(string clientPath, string file)
    {
        using var source = MpqAssetSource.Open(clientPath);
        var data = source.Read(file) ?? throw new FileNotFoundException("Not in the client.", file);
        return TextureConverter.ToImage(BlpDecoder.Decode(data));
    }
}
