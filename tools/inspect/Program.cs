using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Models;
using WowVR.Formats.Database;
using WowVR.Formats.Terrain;
using WowVR.Formats.Textures;

const string Usage = """
    Usage: inspect <command> [arguments]

    Reads from the client at WOWVR_CLIENT_PATH.

      list [text]                 List files, optionally only those containing text.
      blp <file> <out.png> [mip]  Export a BLP texture to PNG. Don't commit the output.
      dbc <file> [max rows]       Print rows of a DBC file. Map and AreaTable are typed.
      area <map> <name>           Print the center of an area's terrain, for picking landmarks.
      wmos <map> <tile x> <tile y> Print a tile's WMO placements, with WoW positions.
      extract <file> <out>        Copy a file out of the client. Don't commit the output.
      check-models [text]         Parse every M2 and WMO, optionally only those containing text.
      emitters <m2 or display id>  Print a model's particle emitters.
      anims <m2 or object display id> Print a model's sequences and what moves in its stand.
      sky <map id> <x> <y> <hour> Print the sky colors and fog at a WoW position and time.
      character <race> <sex> <skin> <face> <hair> <hair color> <facial hair> <out.png> [slot:display ...]
                                  Paint a player's body texture to PNG and print its look. Don't commit the output.
    """;

if (args.Length == 0)
{
    Console.WriteLine(Usage);
    return 1;
}

var clientPath = Environment.GetEnvironmentVariable("WOWVR_CLIENT_PATH");
if (string.IsNullOrWhiteSpace(clientPath))
{
    Console.Error.WriteLine("WOWVR_CLIENT_PATH is not set.");
    return 1;
}

try
{
    using var source = MpqAssetSource.Open(clientPath);

    switch (args)
    {
        case ["list"]:
            List(source, "");
            return 0;
        case ["list", var text]:
            List(source, text);
            return 0;
        case ["blp", var file, var output]:
            return ExportBlp(source, file, output, 0);
        case ["blp", var file, var output, var mip]:
            return ExportBlp(source, file, output, int.Parse(mip));
        case ["spellvisual", var spellId]:
            return PrintSpellVisual(source, int.Parse(spellId));
        case ["spellmodel", var text]:
            return FindSpellModel(source, text);
        case ["dbcstrings", var file, .. var fields]:
            return PrintDbcStrings(source, file, [.. fields.Select(int.Parse)]);
        case ["dbc", var file]:
            return PrintDbc(source, file, 50);
        case ["dbc", var file, var maxRows]:
            return PrintDbc(source, file, int.Parse(maxRows));
        case ["wmos", var map, var tileX, var tileY]:
            return PrintWmos(source, map, int.Parse(tileX), int.Parse(tileY));
        case ["sky", var mapId, var skyX, var skyY, var hour]:
            return PrintSky(source, int.Parse(mapId), float.Parse(skyX), float.Parse(skyY), float.Parse(hour));
        case ["extract", var file, var output]:
            File.WriteAllBytes(output, source.Read(file) ?? throw new IOException($"Not in the client: {file}"));
            return 0;
        case ["check-models"]:
            return CheckModels(source, "");
        case ["check-models", var text]:
            return CheckModels(source, text);
        case ["emitters", var file]:
            return PrintEmitters(source, file);
        case ["anims", var file]:
            return PrintAnims(source, file);
        case ["area", var map, var name]:
            return PrintArea(source, map, name);
        case ["character", var race, var sex, var skin, var face, var hair, var hairColor, var facial, var output, .. var gear]:
            return PaintCharacter(source, [int.Parse(race), int.Parse(sex), int.Parse(skin), int.Parse(face), int.Parse(hair), int.Parse(hairColor), int.Parse(facial)], output, gear);
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (Exception e) when (e is IOException or WowVR.Formats.WowAlchemyException or FormatException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static int PrintWmos(MpqAssetSource source, string mapName, int tileX, int tileY)
{
    var map = WdtMap.Read(source, mapName);
    foreach (var wmo in AdtTerrain.Parse(source.Read(map.TilePath(tileX, tileY))!, map.BigAlpha).Wmos)
    {
        var (wowX, wowY) = (WowCoords.MapOrigin - wmo.Position.Z, WowCoords.MapOrigin - wmo.Position.X);
        Console.WriteLine($"{wowX:F0},{wowY:F0},{wmo.Position.Y:F0} yaw {wmo.Rotation.Y:F0} {wmo.Model}");
    }

    return 0;
}

static int CheckModels(MpqAssetSource source, string text)
{
    var files = source.List().Where(f => f.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    var (passed, failed) = (0, 0);

    void Check(string file, Action parse)
    {
        try
        {
            parse();
            passed++;
        }
        catch (Exception e) when (e is WowVR.Formats.WowAlchemyException or InvalidOperationException)
        {
            failed++;
            Console.WriteLine($"{file}: {e.Message}");
        }
    }

    foreach (var file in files.Where(f => f.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)))
    {
        var skin = source.Read(M2Model.SkinPath(file));
        if (skin is not null)
        {
            Check(file, () => M2Model.Parse(source.Read(file)!, skin));
        }
    }

    foreach (var file in files.Where(f => f.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase) && !IsWmoGroup(f)))
    {
        Check(file, () =>
        {
            var root = WmoRoot.Parse(source.Read(file)!);
            for (var i = 0; i < root.GroupCount; i++)
            {
                WmoGroup.Parse(source.Read(WmoRoot.GroupPath(file, i)) ?? throw new InvalidOperationException($"Group {i} is missing."), root);
            }
        });
    }

    Console.WriteLine($"{passed} parsed, {failed} failed");
    return failed == 0 ? 0 : 1;
}

static bool IsWmoGroup(string file)
{
    var name = Path.GetFileNameWithoutExtension(file);
    return name.Length > 4 && name[^4] == '_' && name[^3..].All(char.IsAsciiDigit);
}

static int PrintArea(MpqAssetSource source, string mapName, string areaName)
{
    var areas = AreaTableRecord.ReadAll(source)
        .Where(a => a.Name.Equals(areaName, StringComparison.OrdinalIgnoreCase))
        .Select(a => a.Id)
        .ToHashSet();
    var map = WdtMap.Read(source, mapName);

    var (north, west, count) = (0.0, 0.0, 0);
    for (var y = 0; y < WowCoords.TilesPerSide; y++)
    {
        for (var x = 0; x < WowCoords.TilesPerSide; x++)
        {
            if (!map.HasTile(x, y))
            {
                continue;
            }

            foreach (var chunk in AdtTerrain.Parse(source.Read(map.TilePath(x, y))!, map.BigAlpha).Chunks)
            {
                if (areas.Contains(chunk.AreaId))
                {
                    north += chunk.WorldPosition.X - WowCoords.ChunkSize / 2;
                    west += chunk.WorldPosition.Y - WowCoords.ChunkSize / 2;
                    count++;
                }
            }
        }
    }

    if (count == 0)
    {
        Console.Error.WriteLine($"No terrain in {mapName} belongs to {areaName}.");
        return 1;
    }

    Console.WriteLine($"{areaName}: {north / count:F0}, {west / count:F0} ({count} chunks)");
    return 0;
}

static void List(MpqAssetSource source, string text)
{
    foreach (var file in source.List().Where(f => f.Contains(text, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine(file);
    }
}

static int ExportBlp(MpqAssetSource source, string file, string output, int mip)
{
    var data = source.Read(file);
    if (data is null)
    {
        Console.Error.WriteLine($"Not in the client: {file}");
        return 1;
    }

    var texture = BlpDecoder.Decode(data, mip);
    PngWriter.WriteRgba8(output, texture.Rgba8, texture.Width, texture.Height);
    Console.WriteLine($"{output}: {texture.Width}x{texture.Height}, mip {mip} of {texture.MipCount}");
    return 0;
}

static int PrintSky(MpqAssetSource source, int mapId, float x, float y, float hour)
{
    var sky = new WowVR.Formats.World.SkyState();
    var position = new System.Numerics.Vector3((float)WowCoords.MapOrigin - y, 0, (float)WowCoords.MapOrigin - x);
    WowVR.Formats.World.LightTable.Read(source).Sample(mapId, position, hour * 120f, sky);
    foreach (var band in Enum.GetValues<WowVR.Formats.World.SkyBand>())
    {
        var c = sky[band] * 255f;
        Console.WriteLine($"{band,-16} {c.X,4:F0} {c.Y,4:F0} {c.Z,4:F0}");
    }

    Console.WriteLine($"Fog {sky.FogStart:F2} to {sky.FogEnd:F0} yards");

    Console.WriteLine($"River alpha {sky.RiverShallowAlpha:F2} to {sky.RiverDeepAlpha:F2}, ocean {sky.OceanShallowAlpha:F2} to {sky.OceanDeepAlpha:F2}");
    return 0;
}

static int PrintEmitters(MpqAssetSource source, string file)
{
    if (int.TryParse(file, out var display))
    {
        file = WowVR.Formats.World.CreatureDisplayTable.Read(source).Resolve(display)?.Model ?? throw new IOException($"No display {display}");
        Console.WriteLine(file);
    }

    var model = M2Model.Parse(source.Read(file) ?? throw new IOException($"Not in the client: {file}"), source.Read(M2Model.SkinPath(file)));
    var moving = M2SkinnedModel.Parse(
        source.Read(file)!,
        source.Read(M2Model.SkinPath(file))!,
        s => s.Id == M2SequenceIds.Stand && s.Variation == 0,
        s => source.Read(s.AnimPath(file)));
    foreach (var e in moving.Emitters)
    {
        var path = moving.Clips.Count > 0 ? M2BoneBake.EmitterPath(moving, moving.Clips[0], e) : null;
        Console.Write(path is null ? "still: " : $"moves {path.Points.Max(q => System.Numerics.Vector3.Distance(q, path.Points[0])):F2} over {path.Seconds:F1} s from {path.Points[0]}, height {string.Join(" ", new[] { 0, 0.2, 0.4, 0.6, 0.8, 1.2, 2.0, 3.0 }.Where(t => t < path.Seconds).Select(t => $"{t:F1}s:{path.Points[(int)(t / path.Seconds * path.Points.Length)].Y:F1}"))}: ");
        Console.WriteLine($"bone {e.Bone} at {e.Position}: rate {e.Rate:F2}, life {e.Lifespan:F2} +-{e.LifespanVariation:F2}, speed {e.Speed:F2}, area {e.AreaLength:F2}x{e.AreaWidth:F2}, sizes {string.Join('/', e.Sizes.Select(v => v.ToString("F2")))}, blend {e.Blend}, colors {string.Join(" ", e.Colors.Select(c => $"{c.X * 255:F0},{c.Y * 255:F0},{c.Z * 255:F0},{c.W:F2}"))}, {e.Texture}, {(e.StartsOn ? "on" : "off")}{string.Concat(e.Switches.Select(s => $", {(s.On ? "on" : "off")} at {s.Seconds:F2} s"))}{(e.SwitchLoopSeconds > 0 ? $", looping every {e.SwitchLoopSeconds:F3} s" : "")}");
    }

    Console.WriteLine($"{model.Emitters.Count} emitters");
    return 0;
}

static int PrintAnims(MpqAssetSource source, string file)
{
    if (int.TryParse(file, out var display))
    {
        file = WowVR.Formats.World.GameObjectDisplayTable.Read(source).ModelOf(display) ?? throw new IOException($"No object display {display}");
    }

    Console.WriteLine(file);
    var model = M2SkinnedModel.Parse(source.Read(file) ?? throw new IOException($"Not in the client: {file}"), source.Read(M2Model.SkinPath(file))!, _ => true, q => source.Read(q.AnimPath(file)));
    Console.WriteLine($"{model.Bones.Length} bones, {model.Emitters.Count} emitters, global sequences {string.Join(' ', model.GlobalSequences)}");
    var low = model.Positions.Aggregate(System.Numerics.Vector3.Min);
    var high = model.Positions.Aggregate(System.Numerics.Vector3.Max);
    Console.WriteLine($"mesh bounds {low} to {high}");
    foreach (var e in model.Emitters)
    {
        var posed = model.Clips.Count > 0 && e.Bone >= 0 ? System.Numerics.Vector3.Transform(e.Position, M2BoneBake.Pose(model, model.Clips[0], 0f)[e.Bone]) : e.Position;
        Console.WriteLine($"emitter bone {e.Bone} pivot {model.Bones[e.Bone].Pivot}: file position {e.Position}, posed {posed}");
    }

    foreach (var clip in model.Clips)
    {
        var moving = 0;
        for (var bone = 0; bone < model.Bones.Length; bone++)
        {
            moving += clip.Translations[bone].Times.Length > 1 || clip.Rotations[bone].Times.Length > 1 || clip.Scales[bone].Times.Length > 1 ? 1 : 0;
        }

        for (var bone = 0; bone < model.Bones.Length; bone++)
        {
            var (t, r, c) = (clip.Translations[bone], clip.Rotations[bone], clip.Scales[bone]);
            if (!t.IsEmpty || !r.IsEmpty || !c.IsEmpty)
            {
                Console.WriteLine($"  bone {bone} parent {model.Bones[bone].Parent} billboard {model.Bones[bone].Billboard}: translation {t.Times.Length} keys global {t.GlobalSequence}, rotation {r.Times.Length} keys global {r.GlobalSequence}, scale {c.Times.Length} keys global {c.GlobalSequence}, last rotation {(r.IsEmpty ? "" : r.Values[^1].ToString())}, last scale {(c.IsEmpty ? "" : c.Values[^1].ToString())}");
            }
        }

        Console.WriteLine($"sequence id {clip.Sequence.Id} variation {clip.Sequence.Variation}: {clip.Sequence.DurationSeconds:F2} s, flags {clip.Sequence.Flags:x}, {moving} bones with keys");
    }

    foreach (var batch in model.Batches)
    {
        var e = batch.Effect;
        Console.WriteLine($"  color {string.Join(' ', e?.Color?.Values.Take(6) ?? [])} | alpha {string.Join(' ', e?.ColorAlpha?.Values.Take(6) ?? [])} | weight {string.Join(' ', e?.Weight?.Values.Take(6) ?? [])}");
        Console.WriteLine($"batch geoset {batch.Geoset} blend {batch.Blend} {batch.Texture}: animated {e?.Animated}, uv0 {e?.Uv0Animation?.Changes}, uv1 {e?.Uv1Animation?.Changes}, color {e?.Color?.Changes} alpha {e?.ColorAlpha?.Changes} weight {e?.Weight?.Changes}");
    }

    return 0;
}

// A spell's look: each kit's animations, effect models (with whether the client has them), sound and procs.
static int PrintSpellVisual(MpqAssetSource source, int spellId)
{
    var spells = SpellTable.Read(source);
    if (spells.Get(spellId) is not { } spell)
    {
        Console.Error.WriteLine($"No spell {spellId}");
        return 1;
    }

    Console.WriteLine($"{spell.FullName} ({spell.Id}): visual {spell.Visual}, speed {spell.Speed}, radius {spell.Radius}, range {spell.Range}, aim {spell.Aim}");
    if (spells.VisualOf(spellId) is not { } visual)
    {
        return 0;
    }

    Console.WriteLine($"  missile {visual.Missile ?? "-"}{(visual.Missile is { } m && source.Read(m) is null ? " (missing)" : "")}, sound {visual.MissileSound}");
    foreach (var (name, kit) in new[]
    {
        ("precast", visual.Precast), ("cast", visual.Cast), ("impact", visual.Impact), ("channel", visual.Channel),
        ("target impact", visual.TargetImpact), ("instant area", visual.InstantArea), ("impact area", visual.ImpactArea), ("persistent area", visual.PersistentArea),
    })
    {
        if (kit is null)
        {
            continue;
        }

        Console.WriteLine($"  {name}: animations {kit.StartAnimation} then {kit.Animation}, sound {kit.Sound}, procs {string.Join(' ', kit.Procs)}");
        for (var place = 0; place < kit.Effects.Length; place++)
        {
            if (kit.Effects[place] is { } effect)
            {
                Console.WriteLine($"    {(KitPlace)place}: {effect}, size {kit.EffectSizes[place]}{(source.Read(effect) is null ? " (missing)" : "")}");
            }
        }
    }

    return 0;
}

// The spells whose visuals use a model whose path holds some text, with which kit and place.
static int FindSpellModel(MpqAssetSource source, string text)
{
    var spells = SpellTable.Read(source);
    var shown = 0;
    for (var id = 0; id < 80000 && shown < 60; id++)
    {
        if (spells.VisualOf(id) is not { } visual || spells.Get(id) is not { } spell)
        {
            continue;
        }

        if (visual.Missile?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine($"{spell.FullName} ({id}), visual {spell.Visual}: missile {visual.Missile}");
            shown++;
        }

        foreach (var (name, kit) in new[]
        {
            ("precast", visual.Precast), ("cast", visual.Cast), ("impact", visual.Impact), ("channel", visual.Channel),
            ("target impact", visual.TargetImpact), ("instant area", visual.InstantArea), ("impact area", visual.ImpactArea), ("persistent area", visual.PersistentArea),
        })
        {
            for (var place = 0; kit is not null && place < kit.Effects.Length; place++)
            {
                if (kit.Effects[place]?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                {
                    Console.WriteLine($"{spell.FullName} ({id}), visual {spell.Visual}: {name} {(KitPlace)place} {kit.Effects[place]}, procs {string.Join(' ', kit.Procs)}");
                    shown++;
                }
            }
        }
    }

    return 0;
}

// Each row's id, then the given fields read as strings.
static int PrintDbcStrings(MpqAssetSource source, string file, int[] fields)
{
    var data = source.Read(file);
    if (data is null)
    {
        Console.Error.WriteLine($"Not in the client: {file}");
        return 1;
    }

    using var dbc = DbcFile.Open(data);
    for (var row = 0; row < dbc.RecordCount; row++)
    {
        Console.WriteLine($"{dbc.GetUInt32(row, 0)} {string.Join(" | ", fields.Select(f => dbc.GetString(row, f)))}");
    }

    return 0;
}

static int PrintDbc(MpqAssetSource source, string file, int maxRows)
{
    var data = source.Read(file);
    if (data is null)
    {
        Console.Error.WriteLine($"Not in the client: {file}");
        return 1;
    }

    using var dbc = DbcFile.Open(data);
    Console.WriteLine($"{file}: {dbc.RecordCount} records, {dbc.FieldCount} fields");

    if (file.EndsWith("Map.dbc", StringComparison.OrdinalIgnoreCase))
    {
        Print(MapRecord.ReadAll(dbc, source.Locale), maxRows);
    }
    else if (file.EndsWith("AreaTable.dbc", StringComparison.OrdinalIgnoreCase))
    {
        Print(AreaTableRecord.ReadAll(dbc, source.Locale), maxRows);
    }
    else
    {
        for (var row = 0; row < Math.Min(dbc.RecordCount, maxRows); row++)
        {
            var fields = Enumerable.Range(0, dbc.FieldCount).Select(field => dbc.GetUInt32(row, field));
            Console.WriteLine(string.Join(' ', fields));
        }
    }

    return 0;
}

static void Print<T>(IEnumerable<T> records, int maxRows)
{
    foreach (var record in records.Take(maxRows))
    {
        Console.WriteLine(record);
    }
}

static int PaintCharacter(IAssetSource source, int[] look, string output, string[] gear)
{
    var equipment = new int[(int)WowVR.Formats.World.EquipmentSlot.Count];
    foreach (var item in gear)
    {
        var parts = item.Split(':');
        equipment[int.Parse(parts[0])] = int.Parse(parts[1]);
    }

    var table = WowVR.Formats.World.CreatureDisplayTable.Read(source);
    var appearance = new WowVR.Formats.World.CharacterAppearance(look[0], look[1], look[2], look[3], look[4], look[5], look[6], equipment);
    if (table.ResolveCharacter(appearance) is not { } resolved)
    {
        Console.Error.WriteLine("No model for that race and sex.");
        return 1;
    }

    Console.WriteLine($"Model: {resolved.Model}");
    foreach (var (type, texture) in resolved.Textures)
    {
        Console.WriteLine($"Texture {type}: {texture}");
    }

    foreach (var layer in resolved.BodyLayers!)
    {
        var found = layer.Candidates.FirstOrDefault(c => source.Read(c) is { Length: > 0 });
        Console.WriteLine($"Layer {layer.Layer} {layer.Region}: {found ?? "missing: " + string.Join(" | ", layer.Candidates)}");
    }

    foreach (var item in resolved.Items)
    {
        Console.WriteLine($"Worn at {item.Attachment}: {item.Model}");
    }

    var painted = WowVR.Formats.World.CharacterTexture.Compose(resolved.BodyLayers!, source.Read);
    PngWriter.WriteRgba8(output, painted.Rgba8, painted.Width, painted.Height);
    return 0;
}

