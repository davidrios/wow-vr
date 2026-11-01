namespace WowVR.Formats.Assets;

/// <summary>
/// Read access to game files by their in-game path, such as
/// <c>DBFilesClient\Map.dbc</c>. Implementations must be safe to call from any thread.
/// </summary>
public interface IAssetSource : IDisposable
{
    bool Exists(string path);

    /// <summary>Returns the file's contents, or null if it doesn't exist.</summary>
    byte[]? Read(string path);

    IReadOnlyList<string> List();
}
