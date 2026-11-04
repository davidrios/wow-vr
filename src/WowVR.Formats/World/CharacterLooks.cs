using WowVR.Formats.Textures;

namespace WowVR.Formats.World;

/// <summary>Where a piece of a character's body texture goes. The names are WoWModelViewer's.</summary>
public enum BodyRegion
{
    Base,
    ArmUpper,
    ArmLower,
    Hand,
    FaceUpper,
    FaceLower,
    TorsoUpper,
    TorsoLower,
    LegUpper,
    LegLower,
    Foot,
}

/// <summary>
/// One piece of a character's body texture: the first of its files that exists, drawn into its
/// region over the pieces of lower layers.
/// </summary>
public sealed record BodyLayer(string[] Candidates, BodyRegion Region, int Layer);

/// <summary>The server's equipment slots, as the character list and player fields number them.</summary>
public enum EquipmentSlot
{
    Head,
    Neck,
    Shoulders,
    Shirt,
    Chest,
    Waist,
    Legs,
    Feet,
    Wrists,
    Hands,
    Finger1,
    Finger2,
    Trinket1,
    Trinket2,
    Back,
    MainHand,
    OffHand,
    Ranged,
    Tabard,
    Count,
}

/// <summary>
/// How a player character looks: race and sex as ChrRaces numbers them, the CharSections
/// variations, and the ItemDisplayInfo row worn in each <see cref="EquipmentSlot"/>, 0 for none.
/// </summary>
public sealed record CharacterAppearance(int Race, int Sex, int Skin, int Face, int HairStyle, int HairColor, int FacialHair, int[] Equipment)
{
    /// <summary>The InventoryType of what's in each slot, which says a chest piece is a robe.</summary>
    public int[] InventoryTypes { get; init; } = new int[(int)EquipmentSlot.Count];

    public bool HideHelm { get; init; }

    public bool HideCloak { get; init; }

    public int Worn(EquipmentSlot slot) => (int)slot < Equipment.Length ? Equipment[(int)slot] : 0;

    /// <summary>What the hands hold, right then left, as <c>CreatureLibrary</c> takes weapons.</summary>
    public int[] Weapons => [Worn(EquipmentSlot.MainHand), Worn(EquipmentSlot.OffHand)];
}

/// <summary>How many of each choice a race and sex have, for making a character.</summary>
public sealed record CharacterChoices(int Skins, int Faces, int HairStyles, int HairColors, int FacialHairs);

public sealed partial class CreatureDisplayTable
{
    private const int FaceSection = 1;
    private const int FacialHairSection = 2;
    private const int UnderwearSection = 4;
    private const int RobeInventoryType = 20;

    /// <summary>CharSections' flag for a choice players can make.</summary>
    private const int PlayerChoiceFlag = 0x1;

    /// <summary>CharSections' flag for a choice only Death Knights get.</summary>
    private const int DeathKnightFlag = 0x4;

    /// <summary>WoWModelViewer's regionPaths, for each <see cref="BodyRegion"/>.</summary>
    private static readonly string?[] RegionFolders =
    [
        null,
        @"Item\TextureComponents\ArmUpperTexture\",
        @"Item\TextureComponents\ArmLowerTexture\",
        @"Item\TextureComponents\HandTexture\",
        null,
        null,
        @"Item\TextureComponents\TorsoUpperTexture\",
        @"Item\TextureComponents\TorsoLowerTexture\",
        @"Item\TextureComponents\LegUpperTexture\",
        @"Item\TextureComponents\LegLowerTexture\",
        @"Item\TextureComponents\FootTexture\",
    ];

    /// <summary>WoWModelViewer's slotOrder and slotOrderWithRobe, of the slots that add to the body texture.</summary>
    private static readonly EquipmentSlot[] LayerOrder =
    [
        EquipmentSlot.Shirt, EquipmentSlot.Head, EquipmentSlot.Neck, EquipmentSlot.Shoulders, EquipmentSlot.Legs,
        EquipmentSlot.Feet, EquipmentSlot.Chest, EquipmentSlot.Tabard, EquipmentSlot.Waist, EquipmentSlot.Wrists,
        EquipmentSlot.Hands, EquipmentSlot.MainHand, EquipmentSlot.OffHand, EquipmentSlot.Back,
    ];

    private static readonly EquipmentSlot[] LayerOrderWithRobe =
    [
        EquipmentSlot.Shirt, EquipmentSlot.Head, EquipmentSlot.Neck, EquipmentSlot.Shoulders, EquipmentSlot.Feet,
        EquipmentSlot.Legs, EquipmentSlot.Wrists, EquipmentSlot.Chest, EquipmentSlot.Hands, EquipmentSlot.Tabard,
        EquipmentSlot.Waist, EquipmentSlot.MainHand, EquipmentSlot.OffHand, EquipmentSlot.Back,
    ];

    /// <summary>
    /// A player character's model, textures, geosets and worn models, following WoWModelViewer's
    /// CharControl::RefreshModel and AddEquipment. Null for a race or sex the client doesn't have.
    /// </summary>
    public CreatureLook? ResolveCharacter(CharacterAppearance a)
    {
        if (!_raceDisplays.TryGetValue(a.Race, out var displays)
            || Resolve(a.Sex == 0 ? displays.Male : displays.Female) is not { } body)
        {
            return null;
        }

        var textures = new Dictionary<int, string>();
        var layers = new List<BodyLayer>();
        void Add(string file, BodyRegion region, int layer)
        {
            if (file.Length > 0)
            {
                layers.Add(new BodyLayer([file], region, layer));
            }
        }

        if (_sections.TryGetValue((a.Race, a.Sex, SkinSection, 0, a.Skin), out var skin))
        {
            Add(skin[0], BodyRegion.Base, 0);
        }

        if (_sections.TryGetValue((a.Race, a.Sex, UnderwearSection, 0, a.Skin), out var underwear))
        {
            Add(underwear[0], BodyRegion.LegUpper, 1);
            Add(underwear[1], BodyRegion.TorsoUpper, 1);
        }

        if (_sections.TryGetValue((a.Race, a.Sex, FaceSection, a.Face, a.Skin), out var face))
        {
            Add(face[0], BodyRegion.FaceLower, 1);
            Add(face[1], BodyRegion.FaceUpper, 1);
        }

        if (_sections.TryGetValue((a.Race, a.Sex, FacialHairSection, a.FacialHair, a.HairColor), out var facial))
        {
            Add(facial[0], BodyRegion.FaceLower, 2);
            Add(facial[1], BodyRegion.FaceUpper, 2);
        }

        var (_, bald) = _hair.GetValueOrDefault((a.Race, a.Sex, a.HairStyle));
        if (_sections.TryGetValue((a.Race, a.Sex, HairSection, a.HairStyle, a.HairColor), out var hair) && !bald)
        {
            Add(hair[1], BodyRegion.FaceLower, 3);
            Add(hair[2], BodyRegion.FaceUpper, 3);
        }

        // Dressing gives the hair, fur and cape textures, the geosets and the worn models.
        var extra = ExtraOf(a);
        var (geosets, worn) = Dress(extra, textures);

        // An orc with no hair but a beard has no hair texture of its own, and takes style 0's.
        if (!textures.ContainsKey(M2TextureTypes.Hair)
            && _sections.TryGetValue((a.Race, a.Sex, HairSection, 0, a.HairColor), out var plainHair) && plainHair[0].Length > 0)
        {
            textures[M2TextureTypes.Hair] = plainHair[0];
        }

        var robe = IsRobe(a, EquipmentSlot.Chest) || IsRobe(a, EquipmentSlot.Legs);
        var order = robe ? LayerOrderWithRobe : LayerOrder;
        for (var i = 0; i < order.Length; i++)
        {
            var slot = order[i];
            if (extra.Items.Length > 0 && Shown(a, slot) is { } shown && _items.TryGetValue(shown, out var item))
            {
                AddItemLayers(slot, item, a, 10 + i, layers);
            }
        }

        textures[M2TextureTypes.Body] = $"character:{a.Race}:{a.Sex}:{string.Join(',', layers.Select(l => $"{l.Layer}{l.Region}{l.Candidates[0]}"))}".GetHashCode().ToString("X8") + ".composed";
        return new CreatureLook(body.Model, body.Scale, textures, geosets, worn, body.Sounds) { BodyLayers = layers };
    }

    /// <summary>
    /// How many skins, faces, hair styles and colors and facial hair styles players of a race and
    /// sex can choose, from CharSections' player rows. Death Knights get more skins.
    /// </summary>
    public CharacterChoices ChoicesFor(int race, int sex, bool deathKnight)
    {
        bool Allowed((int Race, int Sex, int Section, int Variation, int Color) key) =>
            _sectionFlags.TryGetValue(key, out var flags)
            && (flags & PlayerChoiceFlag) != 0
            && (deathKnight || (flags & DeathKnightFlag) == 0);

        int CountWhile(Func<int, bool> exists)
        {
            var n = 0;
            while (n < 64 && exists(n))
            {
                n++;
            }

            return n;
        }

        var skins = CountWhile(s => Allowed((race, sex, SkinSection, 0, s)));
        var faces = CountWhile(f => Allowed((race, sex, FaceSection, f, 0)));
        var hairStyles = CountWhile(h => Allowed((race, sex, HairSection, h, 0)));
        var hairColors = CountWhile(c => Allowed((race, sex, HairSection, 0, c)));
        var facial = CountWhile(f => _beards.ContainsKey((race, sex, f)));
        return new CharacterChoices(Math.Max(skins, 1), Math.Max(faces, 1), Math.Max(hairStyles, 1), Math.Max(hairColors, 1), Math.Max(facial, 1));
    }

    /// <summary>
    /// What a new character of this race, class and sex starts out wearing, from CharStartOutfit, as
    /// a <see cref="CharacterAppearance"/>'s equipment and inventory types. Nothing for a combination
    /// the client doesn't list.
    /// </summary>
    public (int[] Equipment, int[] InventoryTypes) StartOutfit(int race, int cls, int sex)
    {
        var equipment = new int[(int)EquipmentSlot.Count];
        var types = new int[(int)EquipmentSlot.Count];
        if (!_startOutfits.TryGetValue((race, cls, sex), out var items))
        {
            return (equipment, types);
        }

        foreach (var (display, type) in items)
        {
            if (display <= 0 || SlotFor(type, equipment) is not { } slot)
            {
                continue;
            }

            equipment[(int)slot] = display;
            types[(int)slot] = type;
        }

        return (equipment, types);
    }

    /// <summary>Where an InventoryType is worn. A second one-handed weapon goes in the off hand.</summary>
    private static EquipmentSlot? SlotFor(int inventoryType, int[] equipment) => inventoryType switch
    {
        1 => EquipmentSlot.Head,
        2 => EquipmentSlot.Neck,
        3 => EquipmentSlot.Shoulders,
        4 => EquipmentSlot.Shirt,
        5 or RobeInventoryType => EquipmentSlot.Chest,
        6 => EquipmentSlot.Waist,
        7 => EquipmentSlot.Legs,
        8 => EquipmentSlot.Feet,
        9 => EquipmentSlot.Wrists,
        10 => EquipmentSlot.Hands,
        13 => equipment[(int)EquipmentSlot.MainHand] == 0 ? EquipmentSlot.MainHand : EquipmentSlot.OffHand,
        14 or 22 or 23 => EquipmentSlot.OffHand,
        15 or 25 or 26 => EquipmentSlot.Ranged,
        16 => EquipmentSlot.Back,
        17 or 21 => EquipmentSlot.MainHand,
        19 => EquipmentSlot.Tabard,
        _ => null,
    };

    private bool IsRobe(CharacterAppearance a, EquipmentSlot slot) =>
        a.Worn(slot) != 0
        && (a.InventoryTypes[(int)slot] == RobeInventoryType || (_items.TryGetValue(a.Worn(slot), out var item) && item.Groups[2] == 1));

    private static int? Shown(CharacterAppearance a, EquipmentSlot slot) =>
        (slot == EquipmentSlot.Head && a.HideHelm) || (slot == EquipmentSlot.Back && a.HideCloak) || a.Worn(slot) == 0
            ? null
            : a.Worn(slot);

    /// <summary>The same row an NPC's CreatureDisplayInfoExtra would give, for the dressing code they share.</summary>
    private static ExtraRow ExtraOf(CharacterAppearance a)
    {
        var items = new int[(int)Slot.Count];
        void Put(Slot to, EquipmentSlot from)
        {
            items[(int)to] = Shown(a, from) ?? 0;
        }

        Put(Slot.Head, EquipmentSlot.Head);
        Put(Slot.Shoulder, EquipmentSlot.Shoulders);
        Put(Slot.Shirt, EquipmentSlot.Shirt);
        Put(Slot.Chest, EquipmentSlot.Chest);
        Put(Slot.Belt, EquipmentSlot.Waist);
        Put(Slot.Pants, EquipmentSlot.Legs);
        Put(Slot.Boots, EquipmentSlot.Feet);
        Put(Slot.Wrists, EquipmentSlot.Wrists);
        Put(Slot.Gloves, EquipmentSlot.Hands);
        Put(Slot.Tabard, EquipmentSlot.Tabard);
        Put(Slot.Cape, EquipmentSlot.Back);
        return new ExtraRow(a.Race, a.Sex, a.Skin, a.HairStyle, a.HairColor, a.FacialHair, items, "");
    }

    /// <summary>WoWModelViewer's AddEquipment: which body regions each slot's item paints.</summary>
    private static void AddItemLayers(EquipmentSlot slot, ItemRow item, CharacterAppearance a, int layer, List<BodyLayer> layers)
    {
        void Paint(BodyRegion region)
        {
            var index = (int)region - (int)BodyRegion.ArmUpper;
            var name = region switch
            {
                BodyRegion.ArmUpper => item.Regions.ElementAtOrDefault(0),
                BodyRegion.ArmLower => item.Regions.ElementAtOrDefault(1),
                BodyRegion.Hand => item.Regions.ElementAtOrDefault(2),
                BodyRegion.TorsoUpper => item.Regions.ElementAtOrDefault(3),
                BodyRegion.TorsoLower => item.Regions.ElementAtOrDefault(4),
                BodyRegion.LegUpper => item.Regions.ElementAtOrDefault(5),
                BodyRegion.LegLower => item.Regions.ElementAtOrDefault(6),
                BodyRegion.Foot => item.Regions.ElementAtOrDefault(7),
                _ => null,
            };
            if (name is { Length: >= 3 } && RegionFolders[(int)region] is { } folder && index >= 0)
            {
                layers.Add(new BodyLayer(ItemTextureCandidates(folder, name, a.Sex), region, layer));
            }
        }

        switch (slot)
        {
            case EquipmentSlot.Chest or EquipmentSlot.Shirt:
                Paint(BodyRegion.ArmUpper);
                Paint(BodyRegion.ArmLower);
                Paint(BodyRegion.TorsoUpper);
                Paint(BodyRegion.TorsoLower);
                if (a.InventoryTypes[(int)slot] == RobeInventoryType || item.Groups[2] == 1)
                {
                    Paint(BodyRegion.LegUpper);
                    Paint(BodyRegion.LegLower);
                }

                break;
            case EquipmentSlot.Waist:
                Paint(BodyRegion.TorsoLower);
                Paint(BodyRegion.LegUpper);
                break;
            case EquipmentSlot.Wrists:
                Paint(BodyRegion.ArmLower);
                break;
            case EquipmentSlot.Legs:
                Paint(BodyRegion.LegUpper);
                Paint(BodyRegion.LegLower);
                break;
            case EquipmentSlot.Hands:
                Paint(BodyRegion.Hand);
                Paint(BodyRegion.ArmLower);
                break;
            case EquipmentSlot.Feet:
                Paint(BodyRegion.LegLower);
                Paint(BodyRegion.Foot);
                break;
            case EquipmentSlot.Tabard:
                Paint(BodyRegion.TorsoUpper);
                Paint(BodyRegion.TorsoLower);
                break;
        }
    }

    /// <summary>
    /// WoWModelViewer's makeItemTexture: the sex's file, or the unisex one, which leggings named
    /// with an L try first, then the bare name.
    /// </summary>
    private static string[] ItemTextureCandidates(string folder, string name, int sex)
    {
        var leggings = char.ToLowerInvariant(name[^2]) == 'l';
        var sexed = sex == 0 ? "M" : "F";
        return leggings
            ? [$"{folder}{name}_U.blp", $"{folder}{name}_{sexed}.blp", $"{folder}{name}.blp"]
            : [$"{folder}{name}_{sexed}.blp", $"{folder}{name}_U.blp", $"{folder}{name}.blp"];
    }
}

/// <summary>
/// Paints a character's body texture from its pieces, as WoWModelViewer's CharTexture::compose
/// does: 512 square, each piece stretched to its region and drawn over by its alpha.
/// </summary>
public static class CharacterTexture
{
    public const int Size = 512;

    /// <summary>WoWModelViewer's regions, at twice their 256 grid.</summary>
    private static readonly (int X, int Y, int Width, int Height)[] Regions =
    [
        (0, 0, 512, 512),
        (0, 0, 256, 128),
        (0, 128, 256, 128),
        (0, 256, 256, 64),
        (0, 320, 256, 64),
        (0, 384, 256, 128),
        (256, 0, 256, 128),
        (256, 128, 256, 64),
        (256, 192, 256, 128),
        (256, 320, 256, 128),
        (256, 448, 256, 64),
    ];

    /// <summary>
    /// The painted texture as RGBA. <paramref name="read"/> gives a file's bytes or null, and a piece
    /// whose files are all missing is left out.
    /// </summary>
    public static DecodedTexture Compose(IReadOnlyList<BodyLayer> layers, Func<string, byte[]?> read)
    {
        var dest = new byte[Size * Size * 4];
        foreach (var layer in layers.OrderBy(l => l.Layer))
        {
            var data = layer.Candidates.Select(read).FirstOrDefault(d => d is { Length: > 0 });
            if (data is null)
            {
                continue;
            }

            var piece = BlpDecoder.Decode(data);
            var (x0, y0, w, h) = Regions[(int)layer.Region];
            Blit(piece, dest, x0, y0, w, h);
        }

        return new DecodedTexture(dest, Size, Size, 1);
    }

    /// <summary>Stretches the piece bilinearly to its region and draws it there by its alpha, leaving the result opaque.</summary>
    private static void Blit(DecodedTexture piece, byte[] dest, int x0, int y0, int width, int height)
    {
        var src = piece.Rgba8;
        var sx = (float)piece.Width / width;
        var sy = (float)piece.Height / height;
        Span<float> c = stackalloc float[4];
        for (var y = 0; y < height; y++)
        {
            var fy = Math.Clamp((y + 0.5f) * sy - 0.5f, 0, piece.Height - 1);
            var py = (int)fy;
            var py1 = Math.Min(py + 1, piece.Height - 1);
            var ty = fy - py;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp((x + 0.5f) * sx - 0.5f, 0, piece.Width - 1);
                var px = (int)fx;
                var px1 = Math.Min(px + 1, piece.Width - 1);
                var tx = fx - px;
                for (var k = 0; k < 4; k++)
                {
                    var a = src[(py * piece.Width + px) * 4 + k] * (1 - tx) + src[(py * piece.Width + px1) * 4 + k] * tx;
                    var b = src[(py1 * piece.Width + px) * 4 + k] * (1 - tx) + src[(py1 * piece.Width + px1) * 4 + k] * tx;
                    c[k] = a * (1 - ty) + b * ty;
                }

                var d = ((y0 + y) * Size + x0 + x) * 4;
                var alpha = c[3] / 255f;
                dest[d] = (byte)(dest[d] * (1 - alpha) + c[0] * alpha);
                dest[d + 1] = (byte)(dest[d + 1] * (1 - alpha) + c[1] * alpha);
                dest[d + 2] = (byte)(dest[d + 2] * (1 - alpha) + c[2] * alpha);
                dest[d + 3] = 255;
            }
        }
    }
}
