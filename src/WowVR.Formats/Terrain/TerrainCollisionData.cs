using System.Numerics;

namespace WowVR.Formats.Terrain;

/// <summary>
/// A tile's surface, split by chunk so physics can load only what's near the player.
/// Positions match <see cref="TerrainMeshData.Positions"/>, and holes stay open.
/// </summary>
public sealed class TerrainCollisionData(Vector3[] positions, int[] indices, int[] chunkIndexStarts)
{
    /// <summary>Three corners per triangle, wound like the mesh.</summary>
    public Vector3[] ChunkFaces(int chunkX, int chunkY)
    {
        var chunk = chunkY * AdtTerrain.ChunksPerSide + chunkX;
        var start = chunkIndexStarts[chunk];
        var faces = new Vector3[chunkIndexStarts[chunk + 1] - start];
        for (var i = 0; i < faces.Length; i++)
        {
            faces[i] = positions[indices[start + i]];
        }

        return faces;
    }
}
