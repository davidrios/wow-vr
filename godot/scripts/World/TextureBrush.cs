using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats.World;
using Godot;

namespace WowVR.World;

/// <summary>
/// The dev tool's brush, on in the menu: holding the left button on desktop, or the right trigger
/// in VR, tags the textures of what's under the pointer, as <see cref="TextureTags"/> describes:
/// all of a building's, a doodad's or a creature's, or all the layers of the ground's chunk there
/// with the grass they scatter. Doodads without collision, such as most bushes, can't be pointed at.
/// </summary>
public partial class TextureBrush : Node
{
    /// <summary>Further than this isn't painted, in yards.</summary>
    private const float Reach = 300f;

    public required TileManager Tiles { get; init; }
    public required CreatureManager Creatures { get; init; }
    public required Node3D Player { get; init; }

    /// <summary>What was noted this session, so holding the brush on one spot doesn't write its notes every frame.</summary>
    private readonly HashSet<(string Texture, string Where)> _noted = [];
    private readonly HashSet<string> _saving = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<Mesh, SurfaceData[]> _surfaces = new();
    private Godot.Collections.Array<Rid>? _exclude;
    private bool _wasOn;

    /// <summary>The last thing painted, for checks: one of its textures, and what it was.</summary>
    public (string Texture, string Where)? LastPainted { get; private set; }

    /// <summary>How many textures the last stroke found, for checks, and how many of its grass.</summary>
    public int LastStroke { get; private set; }

    public int LastGrass { get; private set; }

    /// <summary>What each terrain layer scatters, so painting the ground tags its grass too.</summary>
    public GroundEffectTable? GroundEffects { get; set; }

    private sealed record SurfaceData(Vector3[] Positions, Vector2[] Uvs, int[] Indices);

    public override void _Ready() => AppSettings.Changed += OnSettingsChanged;

    public override void _ExitTree() => AppSettings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged()
    {
        if (AppSettings.TextureBrush != _wasOn)
        {
            _wasOn = AppSettings.TextureBrush;
            TextureTags.RefreshAll();
        }
    }

    public override void _Process(double delta)
    {
        var (painting, ray) = Player switch
        {
            Desktop.DesktopPlayer desktop => (desktop.Painting, desktop.PointerRay),
            Xr.XrPlayer xr => (xr.Painting, xr.PointerRay),
            _ => (false, null),
        };

        if (AppSettings.TextureBrush && painting && ray is { } r)
        {
            Paint(r.From, r.Direction.Normalized());
        }
    }

    /// <summary>Tags what the ray meets first. Public so checks can aim it themselves.</summary>
    public void Paint(Vector3 from, Vector3 direction)
    {
        var nearest = Reach;
        var creature = NearestCreature(from, direction, ref nearest);
        var found = new Found();

        var query = PhysicsRayQueryParameters3D.Create(from, from + (direction * nearest));
        query.Exclude = _exclude ??= PlayerBodies();
        var hit = Player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0 && hit["collider"].AsGodotObject() is Node collider)
        {
            var at = hit["position"].AsVector3();
            switch (collider)
            {
                // A building: every group's textures, which are the meshes under its node. Its
                // doodads are beside it, not under it.
                case WmoGroupBody wmo when wmo.GetParent() is Node3D building:
                    found.Where = WmoName(wmo.Group.Name);
                    AddMeshesUnder(found, building);
                    break;
                case DoodadBody doodad:
                    AddDoodad(found, doodad);
                    break;
                case var body when TileManager.IsTerrainBody(body):
                    AddTerrain(found, at);
                    break;
                default:
                    // A game object or a transport: whatever meshes it draws with.
                    if (collider.GetParent() is Node3D owner)
                    {
                        found.Where = owner.Name;
                        AddMeshesUnder(found, owner);
                    }

                    break;
            }
        }
        else if (creature is null && GroundAlong(from, direction) is { } ground)
        {
            // Collision is only kept near the player, so further ground is found by its heights.
            AddTerrain(found, ground);
        }
        else if (creature is { } node)
        {
            found.Where = $"creature {node.Name}";
            foreach (var instance in node.Meshes)
            {
                AddMesh(found, instance);
            }
        }

        LastStroke = found.Areas.Count;
        foreach (var part in found.Scattered.Prepend(found))
        {
            foreach (var (texture, (world, uv)) in part.Areas)
            {
                Tag(texture, part.Where, uv > 1e-9 ? world / uv : part.FixedArea);
            }
        }

        LastGrass = found.Scattered.Sum(g => g.Areas.Count);
        if (found.Areas.Count > 0)
        {
            // What was painted, not the grass it carried.
            LastPainted = (found.Areas.Keys.First(), found.Where);
        }
    }

    /// <summary>What a stroke found: each texture with its surfaces' areas in the world and in texture coordinates, summed.</summary>
    private sealed class Found
    {
        public string Where { get; set; } = "";
        public Dictionary<string, (double World, double Uv)> Areas { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>For the ground, whose textures repeat the same everywhere.</summary>
        public double? FixedArea { get; set; }

        /// <summary>The ground's grass, each model with its own textures, noted under its own name.</summary>
        public List<Found> Scattered { get; } = [];

        public void Add(string texture, (double World, double Uv) areas)
        {
            var (world, uv) = Areas.GetValueOrDefault(texture);
            Areas[texture] = (world + areas.World, uv + areas.Uv);
        }
    }

    private static string WmoName(string group) => group.LastIndexOf(" group ", StringComparison.Ordinal) is var cut and > 0 ? group[..cut] : group;

    /// <summary>
    /// All the layers of the ground's chunk there, and the grass, flowers and stones those layers
    /// scatter, which have no collision to be pointed at. The grass is placed at its own size.
    /// </summary>
    private void AddTerrain(Found found, Vector3 at)
    {
        found.Where = "terrain";
        found.FixedArea = TileManager.TerrainAreaPerRepeat;
        foreach (var (texture, effect) in Tiles.TerrainLayersAt(at))
        {
            found.Add(texture, (0, 0));
            if (effect == 0 || GroundEffects?.Find(effect) is not { } scattered)
            {
                continue;
            }

            foreach (var path in scattered.Models)
            {
                if (Tiles.Models.GetM2(path) is not { } model)
                {
                    continue;
                }

                var grass = new Found { Where = path };
                for (var i = 0; i < model.Surfaces.Count && i < model.Materials.Length; i++)
                {
                    var data = FromArrays(model.Surfaces[i].Arrays);
                    grass.Add(model.Materials[i].Texture, TextureTags.Areas(data.Positions, data.Uvs, data.Indices, Transform3D.Identity));
                }

                found.Scattered.Add(grass);
            }
        }
    }

    private void AddMeshesUnder(Found found, Node3D owner)
    {
        foreach (var node in owner.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (node is MeshInstance3D instance)
            {
                AddMesh(found, instance);
            }
        }
    }

    /// <summary>Every surface of a mesh, by the textures its material draws with.</summary>
    private void AddMesh(Found found, MeshInstance3D instance)
    {
        if (instance.Mesh is not { } mesh)
        {
            return;
        }

        var surfaces = SurfacesOf(mesh);
        for (var i = 0; i < surfaces.Length; i++)
        {
            var textures = TextureTags.TexturesOf(instance.GetActiveMaterial(i));
            if (textures.Length == 0)
            {
                continue;
            }

            var areas = TextureTags.Areas(surfaces[i].Positions, surfaces[i].Uvs, surfaces[i].Indices, instance.GlobalTransform);
            foreach (var texture in textures)
            {
                found.Add(texture, areas);
            }
        }
    }

    /// <summary>A doodad's model, with its scale, which the body leaves to its shape: all its textures.</summary>
    private static void AddDoodad(Found found, DoodadBody doodad)
    {
        var toWorld = doodad.GetParent<Node3D>().GlobalTransform * doodad.Placement;
        var model = doodad.Model;
        found.Where = model.Path;
        for (var i = 0; i < model.Surfaces.Count && i < model.Materials.Length; i++)
        {
            var data = FromArrays(model.Surfaces[i].Arrays);
            var areas = TextureTags.Areas(data.Positions, data.Uvs, data.Indices, toWorld);
            var material = model.Materials[i];
            found.Add(material.Texture, areas);
            if (material.Texture2 is { } second && material.Effect?.HasTwoTextures == true)
            {
                found.Add(second, areas);
            }
        }
    }

    private void Tag(string texture, string where, double? area)
    {
        if (!_noted.Add((texture, where)) || !_saving.Add(texture))
        {
            return;
        }

        var source = Tiles.Source;
        Task.Run(() =>
        {
            var added = false;
            try
            {
                added = TextureTags.Tag(texture, source, where, area);
            }
            catch (Exception e)
            {
                GD.PushError($"Could not tag {texture}: {e.Message}");
            }

            Callable.From(() =>
            {
                _saving.Remove(texture);
                TextureTags.Refresh(texture);
                GD.Print($"{(added ? "Tagged" : "Noted")} {texture} on {where}{(area is { } a ? $": one repeat covers {a:F2} square yards" : "")}");
            }).CallDeferred();
        });
    }

    /// <summary>Where the ray first goes below the terrain, a yard at a time, then refined; null if it doesn't.</summary>
    private Vector3? GroundAlong(Vector3 from, Vector3 direction)
    {
        const float step = 1f;
        var previous = 0f;
        for (var t = step; t < Reach; t += step)
        {
            var point = from + (direction * t);
            if (Tiles.TerrainHeightAt(point) is { } height && point.Y < height)
            {
                // Halving between the last point above and this one below.
                var (above, below) = (previous, t);
                for (var i = 0; i < 8; i++)
                {
                    var middle = (above + below) / 2;
                    var at = from + (direction * middle);
                    (above, below) = Tiles.TerrainHeightAt(at) is { } h && at.Y < h ? (above, middle) : (middle, below);
                }

                return from + (direction * below);
            }

            previous = t;
        }

        return null;
    }

    private SurfaceData[] SurfacesOf(Mesh mesh) => _surfaces.GetValue(mesh, m =>
    {
        var result = new SurfaceData[m.GetSurfaceCount()];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = FromArrays(m.SurfaceGetArrays(i));
        }

        return result;
    });

    private static SurfaceData FromArrays(Godot.Collections.Array arrays)
    {
        var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? new Vector2[positions.Length] : arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? SequentialIndices(positions.Length) : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        return new SurfaceData(positions, uvs, indices);
    }

    private static int[] SequentialIndices(int count)
    {
        var indices = new int[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = i;
        }

        return indices;
    }

    /// <summary>The creature nearest along the ray, as an upright capsule of its size, if nearer than <paramref name="nearest"/>.</summary>
    private CreatureNode? NearestCreature(Vector3 from, Vector3 direction, ref float nearest)
    {
        CreatureNode? best = null;
        foreach (var node in AllCreatures())
        {
            var feet = node.GlobalPosition;
            var height = Mathf.Max(node.Height, 1f);

            // Nearest approach to the creature's axis, which is near enough for a capsule.
            var toFeet = feet - from;
            var t = toFeet.Dot(direction);
            if (t <= 0 || t >= nearest)
            {
                continue;
            }

            var point = from + (direction * t);
            var up = Mathf.Clamp(point.Y - feet.Y, 0f, height);
            if (point.DistanceTo(feet + (Vector3.Up * up)) <= node.Radius)
            {
                (best, nearest) = (node, t);
            }
        }

        return best;
    }

    private IEnumerable<CreatureNode> AllCreatures()
    {
        foreach (var node in Creatures.Nodes)
        {
            yield return node;
        }

        foreach (var (_, node) in Creatures.LiveNodes)
        {
            yield return node;
        }
    }

    /// <summary>The player's own bodies, which the ray mustn't stop on.</summary>
    private Godot.Collections.Array<Rid> PlayerBodies()
    {
        var bodies = new Godot.Collections.Array<Rid>();
        if (Player is CollisionObject3D self)
        {
            bodies.Add(self.GetRid());
        }

        foreach (var node in Player.FindChildren("*", nameof(CollisionObject3D), true, false))
        {
            bodies.Add(((CollisionObject3D)node).GetRid());
        }

        return bodies;
    }
}
