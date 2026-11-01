using System.Text;
using WowVR.Formats.Native;

namespace WowVR.Formats.Assets;

/// <summary>Reads game files from a 3.3.5a client's MPQ archives, applying its patches.</summary>
public sealed class MpqAssetSource : IAssetSource
{
    private readonly MpqChainHandle _chain;

    public string Locale { get; }

    private MpqAssetSource(MpqChainHandle chain, string locale)
    {
        _chain = chain;
        Locale = locale;
    }

    public static MpqAssetSource Open(string clientPath)
    {
        var archives = ClientArchives.Find(clientPath);
        var chain = MpqChainHandle.Create();
        try
        {
            for (var i = 0; i < archives.Paths.Count; i++)
            {
                if (NativeMethods.MpqChainAdd(chain, archives.Paths[i], i) != WaStatus.Ok)
                {
                    throw new WowAlchemyException(NativeMethods.LastErrorMessage());
                }
            }

            return new MpqAssetSource(chain, archives.Locale);
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    public bool Exists(string path) => NativeMethods.MpqChainContains(_chain, path) != 0;

    public byte[]? Read(string path)
    {
        return NativeMethods.MpqChainRead(_chain, path, out var buffer) switch
        {
            WaStatus.Ok => NativeMethods.TakeBuffer(buffer),
            WaStatus.NotFound => null,
            _ => throw new WowAlchemyException(NativeMethods.LastErrorMessage()),
        };
    }

    public IReadOnlyList<string> List()
    {
        if (NativeMethods.MpqChainList(_chain, out var buffer) != WaStatus.Ok)
        {
            throw new WowAlchemyException(NativeMethods.LastErrorMessage());
        }

        return Encoding.UTF8.GetString(NativeMethods.TakeBuffer(buffer))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public void Dispose() => _chain.Dispose();
}
