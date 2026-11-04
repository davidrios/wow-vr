using System;
using Godot;

namespace WowVR.World;

/// <summary>
/// Aiming a ground spell, as Blizzard or Rain of Fire: while <see cref="ServerWorld.Aiming"/> holds
/// one, a circle of its radius lies on the ground where the mouse cursor or the VR laser points,
/// green within its range and red beyond, and confirming casts it there. It's a decal, so it lies
/// over terrain, buildings and slopes alike.
/// </summary>
public partial class GroundTargeter : Node3D
{
    /// <summary>How far the pointer reaches for the ground, in yards.</summary>
    private const float Reach = 100f;

    /// <summary>Spells without a radius of their own show this one, in yards.</summary>
    private const float DefaultRadius = 2f;

    /// <summary>How deep the circle projects, above and below where it's pointed, in yards.</summary>
    private const float Depth = 6f;

    private static readonly Color InRange = new(0.35f, 1f, 0.35f, 0.9f);
    private static readonly Color OutOfRange = new(1f, 0.3f, 0.25f, 0.9f);

    public required ServerWorld World { get; init; }
    public required TileManager Tiles { get; init; }

    /// <summary>Where the player points from and toward, in world space, or null when they aren't pointing into the world.</summary>
    public required Func<(Vector3 From, Vector3 Direction)?> Pointer { get; init; }

    /// <summary>The player's feet, in world space, for the range.</summary>
    public required Func<Vector3> Feet { get; init; }

    private Decal _circle = null!;
    private RayCast3D _ray = null!;

    /// <summary>Where the circle lies now, in world space, if it's on the ground.</summary>
    public Vector3? Point { get; private set; }

    public bool Aiming => World.Aiming is not null;

    public override void _Ready()
    {
        // Only what faces up takes it: not walls, sprites or the sides of bodies standing in it.
        _circle = new Decal { Visible = false, TextureAlbedo = RingTexture(), UpperFade = 0.3f, LowerFade = 0.3f, NormalFade = 0.6f };
        AddChild(_circle);
        _ray = new RayCast3D { TopLevel = true, Enabled = false, CollisionMask = TileManager.TerrainLayer };
        AddChild(_ray);
    }

    public override void _Process(double delta)
    {
        if (World.Aiming is not { } spell || Pointer() is not { } pointer || GroundAt(pointer.From, pointer.Direction) is not { } point)
        {
            Point = null;
            _circle.Visible = false;
            return;
        }

        Point = point;
        var radius = spell.Radius > 0 ? spell.Radius : DefaultRadius;
        _circle.Visible = true;
        _circle.GlobalPosition = point;
        _circle.Size = new Vector3(radius * 2, Depth, radius * 2);
        _circle.Modulate = spell.Range <= 0 || point.DistanceTo(Feet()) <= spell.Range ? InRange : OutOfRange;
    }

    /// <summary>The first ground along a ray in world space, or null within reach.</summary>
    public Vector3? GroundAt(Vector3 from, Vector3 direction)
    {
        _ray.GlobalPosition = from;
        _ray.TargetPosition = direction.Normalized() * Reach;
        _ray.ForceRaycastUpdate();
        return _ray.IsColliding() ? _ray.GetCollisionPoint() : null;
    }

    /// <summary>Casts the spell being aimed where the ray meets the ground. False when it meets none.</summary>
    public bool Confirm(Vector3 from, Vector3 direction)
    {
        if (!Aiming || GroundAt(from, direction) is not { } point)
        {
            return false;
        }

        World.CastAt(Tiles.LocalToWow(point));
        return true;
    }

    /// <summary>A ring with a faint fill, as the game's targeting circle, made once.</summary>
    private static ImageTexture RingTexture()
    {
        const int size = 128;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = new Vector2(x + 0.5f - size / 2f, y + 0.5f - size / 2f).Length() / (size / 2f);
                var ring = Mathf.Clamp(1f - Mathf.Abs(r - 0.93f) / 0.06f, 0f, 1f);
                var fill = r < 0.93f ? 0.18f + 0.12f * r : 0f;
                image.SetPixel(x, y, new Color(1, 1, 1, Mathf.Max(ring, fill)));
            }
        }

        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
