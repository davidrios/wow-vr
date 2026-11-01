using AzerothVR.Formats.Native;

namespace AzerothVR.Formats.Textures;

/// <summary>A decoded texture: 4 bytes per pixel, RGBA, top row first.</summary>
public sealed record DecodedTexture(byte[] Rgba8, int Width, int Height, int MipCount);

public static class BlpDecoder
{
    public static unsafe DecodedTexture Decode(ReadOnlySpan<byte> blp, int mipLevel = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mipLevel);

        fixed (byte* data = blp)
        {
            if (NativeMethods.BlpDecode(data, (nuint)blp.Length, (uint)mipLevel, out var image) != WaStatus.Ok)
            {
                throw new WowAlchemyException(NativeMethods.LastErrorMessage());
            }

            return new DecodedTexture(
                NativeMethods.TakeBuffer(image.Pixels),
                checked((int)image.Width),
                checked((int)image.Height),
                checked((int)image.MipCount));
        }
    }
}
