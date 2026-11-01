using AzerothVR.Formats.Textures;
using Godot;

namespace AzerothVR.Converters;

public static class TextureConverter
{
    /// <summary>Safe to call off the main thread.</summary>
    public static Image ToImage(DecodedTexture texture) =>
        Image.CreateFromData(texture.Width, texture.Height, false, Image.Format.Rgba8, texture.Rgba8);
}
