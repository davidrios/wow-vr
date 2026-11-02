namespace WowVR.Formats.Terrain;

public static class TerrainTextures
{
    private const string Extension = ".blp";
    private const string SpecularSuffix = "_s.blp";

    /// <summary>The file beside a terrain texture with the same colors and its shininess in alpha. It may not exist.</summary>
    public static string SpecularPath(string texture)
    {
        if (texture.EndsWith(SpecularSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return texture;
        }

        var stem = texture.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? texture[..^Extension.Length] : texture;
        return stem + SpecularSuffix;
    }
}
