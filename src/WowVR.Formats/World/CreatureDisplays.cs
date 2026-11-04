using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.Models;

namespace WowVR.Formats.World;

/// <summary>M2 texture types, which say where a batch's texture comes from.</summary>
public static class M2TextureTypes
{
    public const int Named = 0;
    public const int Body = 1;
    public const int Item = 2;
    public const int Hair = 6;
    public const int Fur = 8;
    public const int Monster1 = 11;
    public const int Monster2 = 12;
    public const int Monster3 = 13;
}

/// <summary>Which geosets a character model shows, from its hair, beard and clothes.</summary>
public sealed class CharacterGeosets
{
    private readonly int[] _groups;
    private readonly int _hair;
    private readonly bool _scalp;

    internal CharacterGeosets(int[] groups, int hair, bool scalp)
    {
        _groups = groups;
        _hair = hair;
        _scalp = scalp;
    }

    /// <summary>WoWModelViewer's rules: each hundred is a group that shows one member.</summary>
    public bool IsVisible(int geoset)
    {
        if (geoset == 0)
        {
            return true;
        }

        if (geoset == 1)
        {
            return _scalp;
        }

        if (geoset < 100)
        {
            return geoset == _hair;
        }

        var group = geoset / 100;
        return group >= _groups.Length || geoset % 100 == 0 || geoset % 100 == _groups[group];
    }
}

/// <summary>A model worn at one of a character's attachment points.</summary>
public sealed record CreatureItem(int Attachment, string Model, string? Texture);

/// <summary>How a creature's display looks.</summary>
public sealed record CreatureLook(
    string Model,
    float Scale,
    IReadOnlyDictionary<int, string> Textures,
    CharacterGeosets? Geosets,
    IReadOnlyList<CreatureItem> Items,
    CreatureSoundSet? Sounds = null)
{
    /// <summary>
    /// A player's body texture, made from these pieces rather than read from a file. Its
    /// <see cref="Textures"/> entry for the body is then only a name for the result.
    /// </summary>
    public IReadOnlyList<BodyLayer>? BodyLayers { get; init; }
}

/// <summary>
/// Turns a display id into a model and how to dress it, through CreatureDisplayInfo,
/// CreatureModelData and, for characters, CreatureDisplayInfoExtra.
/// </summary>
public sealed partial class CreatureDisplayTable
{
    private const int AttachmentRightShoulder = 5;
    private const int AttachmentLeftShoulder = 6;
    private const int AttachmentHelmet = 11;

    private const int HairSection = 3;
    private const int SkinSection = 0;

    private const int GeosetGroups = 20;
    private const int GroupBeard1 = 1;
    private const int GroupBeard2 = 2;
    private const int GroupBeard3 = 3;
    private const int GroupGloves = 4;
    private const int GroupBoots = 5;
    private const int GroupEars = 7;
    private const int GroupWrists = 8;
    private const int GroupKnees = 9;
    private const int GroupTabard = 12;
    private const int GroupRobe = 13;
    private const int GroupCape = 15;

    private enum Slot
    {
        Head,
        Shoulder,
        Shirt,
        Chest,
        Belt,
        Pants,
        Boots,
        Wrists,
        Gloves,
        Tabard,
        Cape,
        Count,
    }

    /// <summary>The order WoWModelViewer dresses a character in, because later items overrule earlier ones.</summary>
    private static readonly Slot[] DressOrder =
    [
        Slot.Shirt, Slot.Head, Slot.Shoulder, Slot.Pants, Slot.Boots, Slot.Chest,
        Slot.Tabard, Slot.Belt, Slot.Wrists, Slot.Gloves, Slot.Cape,
    ];

    private readonly record struct DisplayRow(int Model, int Sound, int Extra, float Scale, string[] Skins);

    private readonly record struct ModelRow(string Path, float Scale, int Sound);

    private sealed record ExtraRow(int Race, int Sex, int Skin, int Hair, int HairColor, int Beard, int[] Items, string Bake);

    /// <summary>HelmetVis holds a HelmetGeosetVisData row for each sex.</summary>
    private sealed record ItemRow(string[] Models, string[] Textures, int[] Groups, int[] HelmetVis)
    {
        /// <summary>Texture pieces for a character's body, by <see cref="BodyRegion"/> from arm upper to foot. Empty for none.</summary>
        public string[] Regions { get; init; } = [];
    }

    /// <summary>What a helmet hides. Each is a mask of the races it hides that on, by bit 1 &lt;&lt; race.</summary>
    private sealed record HelmetVisRow(uint Hair, uint Beard1, uint Beard2, uint Beard3, uint Ears);

    private readonly Dictionary<int, DisplayRow> _displays = [];
    private readonly Dictionary<int, ModelRow> _models = [];
    private readonly Dictionary<int, ExtraRow> _extras = [];
    private readonly Dictionary<int, ItemRow> _items = [];
    private readonly Dictionary<int, HelmetVisRow> _helmetVis = [];
    private readonly Dictionary<int, (int Display, int InventoryType)> _itemDisplays = [];
    private readonly Dictionary<(int Race, int Class, int Sex), (int Display, int Type)[]> _startOutfits = [];
    private readonly Dictionary<int, (int Male, int Female)> _raceDisplays = [];
    private readonly Dictionary<(int Race, int Sex, int Section, int Variation, int Color), int> _sectionFlags = [];
    private readonly Dictionary<int, string> _racePrefixes = [];
    private readonly Dictionary<int, CreatureSoundSet> _sounds = [];
    private readonly Dictionary<(int Race, int Sex, int Style), (int Geoset, bool Scalp)> _hair = [];
    private readonly Dictionary<(int Race, int Sex, int Style), (int G100, int G200, int G300)> _beards = [];
    private readonly Dictionary<(int Race, int Sex, int Section, int Variation, int Color), string[]> _sections = [];

    private CreatureDisplayTable()
    {
    }

    public static CreatureDisplayTable Read(IAssetSource source)
    {
        var table = new CreatureDisplayTable();

        DbcTables.Read<object?>(source, @"DBFilesClient\CreatureDisplayInfo.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._displays[dbc.GetInt32(i, 0)] = new DisplayRow(
                    dbc.GetInt32(i, 1),
                    dbc.GetInt32(i, 2),
                    dbc.GetInt32(i, 3),
                    dbc.GetSingle(i, 4),
                    [dbc.GetString(i, 6), dbc.GetString(i, 7), dbc.GetString(i, 8)]);
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\CreatureModelData.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._models[dbc.GetInt32(i, 0)] = new ModelRow(dbc.GetString(i, 2), dbc.GetSingle(i, 4), dbc.GetInt32(i, 13));
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\CreatureDisplayInfoExtra.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                var items = new int[(int)Slot.Count];
                for (var slot = 0; slot < items.Length; slot++)
                {
                    items[slot] = dbc.GetInt32(i, 8 + slot);
                }

                table._extras[dbc.GetInt32(i, 0)] = new ExtraRow(
                    dbc.GetInt32(i, 1),
                    dbc.GetInt32(i, 2),
                    dbc.GetInt32(i, 3),
                    dbc.GetInt32(i, 5),
                    dbc.GetInt32(i, 6),
                    dbc.GetInt32(i, 7),
                    items,
                    dbc.GetString(i, 20));
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\ItemDisplayInfo.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._items[dbc.GetInt32(i, 0)] = new ItemRow(
                    [dbc.GetString(i, 1), dbc.GetString(i, 2)],
                    [dbc.GetString(i, 3), dbc.GetString(i, 4)],
                    [dbc.GetInt32(i, 7), dbc.GetInt32(i, 8), dbc.GetInt32(i, 9)],
                    [dbc.GetInt32(i, 13), dbc.GetInt32(i, 14)])
                {
                    Regions = [.. Enumerable.Range(15, 8).Select(f => dbc.GetString(i, f))],
                };
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\HelmetGeosetVisData.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._helmetVis[dbc.GetInt32(i, 0)] = new HelmetVisRow(
                    dbc.GetUInt32(i, 1),
                    dbc.GetUInt32(i, 2),
                    dbc.GetUInt32(i, 3),
                    dbc.GetUInt32(i, 4),
                    dbc.GetUInt32(i, 5));
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\CreatureSoundData.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._sounds[dbc.GetInt32(i, 0)] = new CreatureSoundSet(
                    dbc.GetInt32(i, 9),
                    dbc.GetInt32(i, 11),
                    dbc.GetInt32(i, 24),
                    [dbc.GetInt32(i, 14), dbc.GetInt32(i, 15), dbc.GetInt32(i, 16), dbc.GetInt32(i, 17), dbc.GetInt32(i, 18)],
                    dbc.GetSingle(i, 31),
                    dbc.GetSingle(i, 32));
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\ChrRaces.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._racePrefixes[dbc.GetInt32(i, 0)] = dbc.GetString(i, 6);
                table._raceDisplays[dbc.GetInt32(i, 0)] = (dbc.GetInt32(i, 4), dbc.GetInt32(i, 5));
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\CharHairGeosets.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._hair[(dbc.GetInt32(i, 1), dbc.GetInt32(i, 2), dbc.GetInt32(i, 3))] = (dbc.GetInt32(i, 4), dbc.GetInt32(i, 5) != 0);
            }

            return null;
        });

        // This file has no id column. Its fields run 100, 300, 200, as in WoWModelViewer.
        DbcTables.Read<object?>(source, @"DBFilesClient\CharacterFacialHairStyles.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._beards[(dbc.GetInt32(i, 0), dbc.GetInt32(i, 1), dbc.GetInt32(i, 2))] = (dbc.GetInt32(i, 3), dbc.GetInt32(i, 5), dbc.GetInt32(i, 4));
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\CharSections.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                var key = (dbc.GetInt32(i, 1), dbc.GetInt32(i, 2), dbc.GetInt32(i, 3), dbc.GetInt32(i, 8), dbc.GetInt32(i, 9));
                if (table._sections.TryAdd(key, [dbc.GetString(i, 4), dbc.GetString(i, 5), dbc.GetString(i, 6)]))
                {
                    table._sectionFlags[key] = dbc.GetInt32(i, 7);
                }
            }

            return null;
        });

        // A server says what a creature holds by item, and the client's Item table gives the item's look.
        DbcTables.Read<object?>(source, @"DBFilesClient\Item.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._itemDisplays[dbc.GetInt32(i, 0)] = (dbc.GetInt32(i, 5), dbc.GetInt32(i, 6));
            }

            return null;
        });

        // Race, class, sex and outfit are four bytes in one column, then 24 items, their displays and inventory types.
        DbcTables.Read<object?>(source, @"DBFilesClient\CharStartOutfit.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                var packed = dbc.GetUInt32(i, 1);
                var key = ((int)(packed & 0xFF), (int)((packed >> 8) & 0xFF), (int)((packed >> 16) & 0xFF));
                var items = new (int Display, int Type)[StartOutfitItems];
                for (var item = 0; item < items.Length; item++)
                {
                    items[item] = (dbc.GetInt32(i, 2 + StartOutfitItems + item), dbc.GetInt32(i, 2 + 2 * StartOutfitItems + item));
                }

                table._startOutfits.TryAdd(key, items);
            }

            return null;
        });

        return table;
    }

    private const int StartOutfitItems = 24;

    /// <summary>An item's ItemDisplayInfo row, or 0 for an item the client doesn't know.</summary>
    public int ItemDisplayOf(int item) => _itemDisplays.GetValueOrDefault(item).Display;

    /// <summary>Where an item is worn, as Item.dbc's InventoryType, or 0 for an item the client doesn't know.</summary>
    public int ItemInventoryTypeOf(int item) => _itemDisplays.GetValueOrDefault(item).InventoryType;

    /// <summary>The displays whose model path contains some text, for finding examples.</summary>
    public IEnumerable<int> DisplaysOfModel(string text, bool characterOnly = false) =>
        _displays
            .Where(d => _models.TryGetValue(d.Value.Model, out var model)
                && model.Path.Contains(text, StringComparison.OrdinalIgnoreCase)
                && (!characterOnly || _extras.ContainsKey(d.Value.Extra)))
            .Select(d => d.Key)
            .Order();

    /// <summary>Character displays wearing something on the head and shoulders, for finding armored examples.</summary>
    public IEnumerable<int> ArmoredCharacters(string text) =>
        DisplaysOfModel(text, characterOnly: true).Where(id =>
        {
            var items = _extras[_displays[id].Extra].Items;
            return items[(int)Slot.Head] != 0 && items[(int)Slot.Shoulder] != 0 && items[(int)Slot.Tabard] != 0;
        });

    /// <summary>An item's model and texture file names, without folders. Null when it has no model.</summary>
    public (string Model, string Texture)? ItemFiles(int itemDisplay) =>
        _items.TryGetValue(itemDisplay, out var item) && item.Models[0].Length > 0 ? (item.Models[0], item.Textures[0]) : null;

    /// <summary>Null for a display the client doesn't have.</summary>
    public CreatureLook? Resolve(int displayId)
    {
        if (!_displays.TryGetValue(displayId, out var display) || !_models.TryGetValue(display.Model, out var model) || model.Path.Length == 0)
        {
            return null;
        }

        var path = M2Model.NormalizePath(model.Path);
        var textures = new Dictionary<int, string>();
        var directory = Path.GetDirectoryName(path.Replace('/', '\\'))?.Replace('/', '\\') ?? "";

        var monsterTypes = new[] { M2TextureTypes.Monster1, M2TextureTypes.Monster2, M2TextureTypes.Monster3 };
        for (var i = 0; i < monsterTypes.Length; i++)
        {
            if (display.Skins[i].Length > 0)
            {
                textures[monsterTypes[i]] = $@"{directory}\{display.Skins[i]}.blp";
            }
        }

        var scale = PositiveOrOne(display.Scale) * PositiveOrOne(model.Scale);
        var sounds = _sounds.GetValueOrDefault(display.Sound != 0 ? display.Sound : model.Sound);
        if (!_extras.TryGetValue(display.Extra, out var extra))
        {
            return new CreatureLook(path, scale, textures, null, [], sounds);
        }

        var (geosets, items) = Dress(extra, textures);
        return new CreatureLook(path, scale, textures, geosets, items, sounds);
    }

    private static float PositiveOrOne(float value) => value > 0f ? value : 1f;

    private (CharacterGeosets Geosets, IReadOnlyList<CreatureItem> Items) Dress(ExtraRow extra, Dictionary<int, string> textures)
    {
        if (extra.Bake.Length > 0)
        {
            textures[M2TextureTypes.Body] = $@"Textures\BakedNpcTextures\{extra.Bake}";
        }

        if (_sections.TryGetValue((extra.Race, extra.Sex, HairSection, extra.Hair, extra.HairColor), out var hairSection) && hairSection[0].Length > 0)
        {
            textures[M2TextureTypes.Hair] = hairSection[0];
        }

        if (_sections.TryGetValue((extra.Race, extra.Sex, SkinSection, 0, extra.Skin), out var skinSection) && skinSection[1].Length > 0)
        {
            textures[M2TextureTypes.Fur] = skinSection[1];
        }

        var groups = new int[GeosetGroups];
        Array.Fill(groups, 1);
        groups[GroupEars] = 2;

        var (beard1, beard2, beard3) = _beards.GetValueOrDefault((extra.Race, extra.Sex, extra.Beard));
        groups[GroupBeard1] = beard1;
        groups[GroupBeard2] = beard2;
        groups[GroupBeard3] = beard3;

        var worn = new List<CreatureItem>();
        foreach (var slot in DressOrder)
        {
            if (extra.Items[(int)slot] != 0 && _items.TryGetValue(extra.Items[(int)slot], out var item))
            {
                Wear(slot, item, extra, groups, textures, worn);
            }
        }

        var (hair, scalp) = _hair.GetValueOrDefault((extra.Race, extra.Sex, extra.Hair));
        if (HelmetVisOf(extra) is { } helmet)
        {
            var race = 1u << extra.Race;
            if ((helmet.Hair & race) != 0)
            {
                // The scalp covers the head where the hair was.
                (hair, scalp) = (0, true);
            }

            // 1 is each of these groups' plain member, which shows nothing extra.
            groups[GroupBeard1] = (helmet.Beard1 & race) != 0 ? 1 : groups[GroupBeard1];
            groups[GroupBeard2] = (helmet.Beard2 & race) != 0 ? 1 : groups[GroupBeard2];
            groups[GroupBeard3] = (helmet.Beard3 & race) != 0 ? 1 : groups[GroupBeard3];
            groups[GroupEars] = (helmet.Ears & race) != 0 ? 1 : groups[GroupEars];
        }

        return (new CharacterGeosets(groups, hair, scalp), worn);
    }

    private HelmetVisRow? HelmetVisOf(ExtraRow extra) =>
        extra.Items[(int)Slot.Head] != 0
        && _items.TryGetValue(extra.Items[(int)Slot.Head], out var item)
        && extra.Sex is 0 or 1
        && _helmetVis.TryGetValue(item.HelmetVis[extra.Sex], out var row)
            ? row
            : null;

    /// <summary>Follows WoWModelViewer's CharControl::AddEquipment, where its names for the geoset fields are misleading.</summary>
    private void Wear(Slot slot, ItemRow item, ExtraRow extra, int[] groups, Dictionary<int, string> textures, List<CreatureItem> worn)
    {
        switch (slot)
        {
            case Slot.Chest or Slot.Shirt:
                groups[GroupWrists] = 1 + item.Groups[0];
                break;
            case Slot.Pants:
                groups[GroupKnees] = 1 + item.Groups[1];
                break;
            case Slot.Gloves:
                groups[GroupGloves] = 1 + item.Groups[0];
                break;
            case Slot.Boots:
                groups[GroupBoots] = 1 + item.Groups[0];
                break;
            case Slot.Tabard:
                groups[GroupTabard] = 2;
                break;
            case Slot.Cape:
                groups[GroupCape] = 1 + item.Groups[0];
                if (item.Textures[0].Length > 0)
                {
                    textures[M2TextureTypes.Item] = $@"Item\ObjectComponents\Cape\{item.Textures[0]}.blp";
                }

                break;
            case Slot.Head:
                if (item.Models[0].Length > 0 && _racePrefixes.TryGetValue(extra.Race, out var prefix))
                {
                    var model = $@"Item\ObjectComponents\Head\{Path.ChangeExtension(item.Models[0], null)}_{prefix}{(extra.Sex == 0 ? "M" : "F")}.m2";
                    worn.Add(new CreatureItem(AttachmentHelmet, model, ItemTexture("Head", item.Textures[0])));
                }

                break;
            case Slot.Shoulder:
                if (item.Models[0].Length > 0)
                {
                    worn.Add(new CreatureItem(AttachmentLeftShoulder, ShoulderModel(item.Models[0]), ItemTexture("Shoulder", item.Textures[0])));
                }

                if (item.Models[1].Length > 0)
                {
                    worn.Add(new CreatureItem(AttachmentRightShoulder, ShoulderModel(item.Models[1]), ItemTexture("Shoulder", item.Textures[1])));
                }

                break;
        }

        if (groups[GroupRobe] == 1)
        {
            groups[GroupRobe] = 1 + item.Groups[2];
        }

        if (groups[GroupRobe] == 2)
        {
            groups[GroupBoots] = 0;
            groups[GroupTabard] = 0;
        }

        // Gloves take priority over sleeves.
        if (groups[GroupGloves] > 1)
        {
            groups[GroupWrists] = 0;
        }
    }

    private static string ShoulderModel(string file) =>
        $@"Item\ObjectComponents\Shoulder\{M2Model.NormalizePath(file)}";

    private static string? ItemTexture(string folder, string file) =>
        file.Length > 0 ? $@"Item\ObjectComponents\{folder}\{file}.blp" : null;
}
