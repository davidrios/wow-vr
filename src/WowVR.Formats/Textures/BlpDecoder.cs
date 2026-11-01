using WowVR.Formats.Native;

namespace WowVR.Formats.Textures;

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

    /// <summary>The width and height of a texture's first mipmap.</summary>
    public static unsafe (int Width, int Height) ReadSize(ReadOnlySpan<byte> blp)
    {
        fixed (byte* data = blp)
        {
            if (NativeMethods.BlpSize(data, (nuint)blp.Length, out var width, out var height) != WaStatus.Ok)
            {
                throw new WowAlchemyException(NativeMethods.LastErrorMessage());
            }

            return (checked((int)width), checked((int)height));
        }
    }

    /// <summary>
    /// A BC1 mipmap chain ready for a GPU, without alpha. <paramref name="size"/> is the
    /// first level's width and height, a power of two, and the levels run down to 1 pixel.
    /// </summary>
    public static unsafe byte[] ConvertToBc1(ReadOnlySpan<byte> blp, int size)
    {
        fixed (byte* data = blp)
        {
            if (NativeMethods.BlpToBc1(data, (nuint)blp.Length, (uint)size, out var buffer) != WaStatus.Ok)
            {
                throw new WowAlchemyException(NativeMethods.LastErrorMessage());
            }

            return NativeMethods.TakeBuffer(buffer);
        }
    }
}
