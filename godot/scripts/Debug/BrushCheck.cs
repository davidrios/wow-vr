using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--brush-check --textures=folder --settings=file.cfg</c>, in Goldshire with
/// <c>--sample-spawns</c>: turns the texture brush on, paints the ground, the inn, a doodad and a
/// creature, and prints PASS or FAIL for each saving a PNG and notes in the folder laid out as the
/// MPQs are, and for what uses it being tinted, then for the tint going with the brush. Run it
/// again on the same folder to check the PNGs load back as tagged. Don't commit the folder.
/// </summary>
public partial class BrushCheck : Node
{
    public required TextureBrush Brush { get; init; }
    public required TileManager Tiles { get; init; }
    public required IPlayer Player { get; init; }
    public required CreatureManager Creatures { get; init; }

    private int _failures;

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        var pngsAtStart = Directory.Exists(TextureTags.Folder) ? Directory.EnumerateFiles(TextureTags.Folder, "*.png", SearchOption.AllDirectories).Count() : 0;
        GD.Print($"Brush check: folder {TextureTags.Folder}, {pngsAtStart} PNGs in it");

        var started = Time.GetTicksMsec();
        while ((Tiles.PlacingPlayer || !Player.OnGround || !Tiles.CollisionSettled) && Time.GetTicksMsec() - started < 30000)
        {
            await Seconds(0.2);
        }

        await Seconds(3);

        // What the world loaded from the folder, before anything is painted.
        var taggedAtStart = TextureTags.TaggedCount;
        var wasOn = AppSettings.TextureBrush;
        AppSettings.TextureBrush = true;

        // The ground under the player.
        var feet = Player.Feet;
        await PaintAndCheck("the ground", feet + (Vector3.Up * 3), Vector3.Down, where => where == "terrain", minimumTextures: 2);

        // The inn's wall, east of Goldshire's middle, as the movement check walks into it.
        var center = WowCoords.ToGodot(new System.Numerics.Vector3(-9464f, 62f, 57f));
        var goldshire = Tiles.WorldToLocal(center.X, center.Y, center.Z);
        var wallHeight = (Tiles.TerrainHeightAt(goldshire + new Vector3(20, 0, 8)) ?? feet.Y) + 2f;
        await PaintAndCheck("the inn", goldshire with { Y = wallHeight } + new Vector3(12, 0, 8), Vector3.Right, where => where.EndsWith(".wmo", System.StringComparison.OrdinalIgnoreCase), minimumTextures: 5);

        // A doodad with collision, from above.
        if (GetTree().Root.FindChildren("*", nameof(StaticBody3D), true, false).OfType<DoodadBody>().MinBy(b => b.GlobalPosition.DistanceTo(Player.Feet)) is { } doodad)
        {
            var top = doodad.GlobalPosition + (Vector3.Up * 30);
            await PaintAndCheck($"a doodad ({doodad.Model.Path})", top, Vector3.Down, where => where.EndsWith(".m2", System.StringComparison.OrdinalIgnoreCase), aimAt: doodad.GlobalPosition);
        }
        else
        {
            Check("A doodad with collision nearby", false, "none");
        }

        // A creature, at its chest.
        if (Creatures.Nodes.MinBy(n => n.GlobalPosition.DistanceTo(Player.Feet)) is { } creature)
        {
            var chest = creature.GlobalPosition + (Vector3.Up * creature.Height * 0.6f);
            var from = chest + new Vector3(0, 0.5f, 4);
            await PaintAndCheck("a creature", from, (chest - from).Normalized(), where => where.StartsWith("creature"));
        }
        else
        {
            Check("A creature nearby", false, "none; run with --sample-spawns");
        }

        AppSettings.TextureBrush = false;
        await Seconds(0.2);
        Check("The tint goes with the brush", TintedMaterials() == 0, $"{TintedMaterials()} materials still tinted");

        if (pngsAtStart > 0)
        {
            Check("The folder's PNGs load as tagged", taggedAtStart > 0, $"{taggedAtStart} tagged as the world loaded, from {pngsAtStart} PNGs");
        }
        else
        {
            GD.Print("SKIP The folder's PNGs load as tagged: the folder was empty; run again with the same one");
        }

        AppSettings.TextureBrush = wasOn;
        GD.Print(_failures == 0 ? "Brush check passed." : $"Brush check FAILED: {_failures}.");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task PaintAndCheck(string what, Vector3 from, Vector3 direction, System.Func<string, bool> expected, Vector3? aimAt = null, int minimumTextures = 1)
    {
        if (aimAt is { } target)
        {
            direction = (target - from).Normalized();
        }

        var before = Brush.LastPainted;
        Brush.Paint(from, direction);
        await Seconds(1.5);
        if (Brush.LastPainted is not { } painted || painted == before)
        {
            Check($"Painting {what} tags a texture", false, "nothing painted");
            return;
        }

        Check($"Painting {what} tags a texture", expected(painted.Where), $"{painted.Texture} on {painted.Where}");
        var png = TextureTags.PngPath(painted.Texture);
        var notes = TextureTags.NotesPath(painted.Texture);
        var laidOut = png.Replace('\\', '/').EndsWith(Path.ChangeExtension(painted.Texture.Replace('\\', '/'), ".png"), System.StringComparison.OrdinalIgnoreCase);
        Check($"{what}: the PNG is saved where the MPQ has it", File.Exists(png) && laidOut, png);
        var text = File.Exists(notes) ? File.ReadAllText(notes) : "";
        Check($"{what}: the notes say how much one repeat covers", text.Contains(painted.Where) && text.Contains("largest"), text.Split('\n').Skip(3).FirstOrDefault()?.Trim() ?? "no notes");
        Check($"{what}: it takes all of its textures", Brush.LastStroke >= minimumTextures, $"{Brush.LastStroke} textures");
        if (painted.Where == "terrain" || what == "the ground")
        {
            Check($"{what}: it takes its grass too", Brush.LastGrass > 0, $"{Brush.LastGrass} grass textures");
        }
        Check($"{what}: what uses it is tinted", TextureTags.IsTagged(painted.Texture) && TintedMaterials() > 0, $"{TintedMaterials()} materials tinted");
    }

    /// <summary>Materials drawn now with the tint on: on tiles, buildings, doodads and creatures.</summary>
    private int TintedMaterials()
    {
        var count = 0;
        foreach (var node in GetTree().Root.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
        {
            for (var i = 0; node.Mesh is { } mesh && i < mesh.GetSurfaceCount(); i++)
            {
                switch (node.GetActiveMaterial(i))
                {
                    case ShaderMaterial shader when shader.GetShaderParameter("dev_tint").VariantType == Variant.Type.Vector4 && shader.GetShaderParameter("dev_tint").AsVector4().W > 0:
                    case StandardMaterial3D standard when standard.AlbedoColor != Colors.White:
                        count++;
                        break;
                }
            }
        }

        return count;
    }

    private void Check(string name, bool passed, string detail)
    {
        GD.Print($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        _failures += passed ? 0 : 1;
    }

    private async Task Seconds(double seconds) =>
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
}
