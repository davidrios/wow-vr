using System;
using System.Collections.Generic;
using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Textures;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// The client's icons for spells and items, from <c>Interface\Icons</c>, each decoded once. An icon
/// that's missing or can't be read shows the game's question mark instead.
/// </summary>
public sealed class IconCache(IAssetSource source)
{
    public const string QuestionMark = @"Interface\Icons\INV_Misc_QuestionMark.blp";

    private readonly Dictionary<string, Texture2D?> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon at a path, the question mark for none, or null if even that can't be read.</summary>
    public Texture2D? Get(string? path)
    {
        path ??= QuestionMark;
        if (_icons.TryGetValue(path, out var known))
        {
            return known ?? (path == QuestionMark ? null : Get(QuestionMark));
        }

        Texture2D? texture = null;
        try
        {
            if (source.Read(path) is { Length: > 0 } data)
            {
                var decoded = BlpDecoder.Decode(data);
                var image = Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rgba8, decoded.Rgba8);
                image.GenerateMipmaps();
                texture = ImageTexture.CreateFromImage(image);
            }
        }
        catch (WowAlchemyException e)
        {
            GD.PushWarning($"Could not read the icon {path}: {e.Message}");
        }

        _icons[path] = texture;
        return texture ?? (path == QuestionMark ? null : Get(QuestionMark));
    }
}
