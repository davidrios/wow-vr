using WowVR.Formats.Database;
using WowVR.Formats.World;
using Xunit;

namespace WowVR.Formats.Tests;

public class SpellAimTests
{
    [Theory]
    [InlineData(0u, 2, 6, SpellAim.Enemy)] // Fireball: school damage at the selected enemy
    [InlineData(0u, 10, 21, SpellAim.Friend)] // Lesser Heal: heal on an ally
    [InlineData(0u, 6, 1, SpellAim.Self)] // an aura on the caster
    [InlineData(0x40u, 27, 28, SpellAim.Ground)] // Blizzard: an area at a place
    [InlineData(0u, 0, 6, SpellAim.Self)] // an empty effect aims at nothing
    public void TheFirstEffectsSayWhatASpellAimsAt(uint targets, int effect, int target, SpellAim aim) =>
        Assert.Equal(aim, SpellTable.AimOf(targets, [(effect, target), (0, 0), (0, 0)]));
}

public class ClientSpellTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    private Formats.Assets.MpqAssetSource Source
    {
        get
        {
            if (client.Source is null)
            {
                Assert.Skip("WOWVR_CLIENT_PATH is not set.");
            }

            return client.Source;
        }
    }

    /// <summary>Fireball's first rank: a mage's 1.5-second Fire spell at an enemy, with the global cooldown and an icon in the client.</summary>
    [Fact]
    public void FireballReadsAsTheGameShowsIt()
    {
        var spells = SpellTable.Read(Source);

        var fireball = spells.Get(133);

        Assert.NotNull(fireball);
        Assert.Equal("Fireball", fireball.Name);
        Assert.Equal("Rank 1", fireball.Rank);
        Assert.Equal(1500, fireball.CastTimeMs);
        Assert.Equal(1500, fireball.GlobalCooldownMs);
        Assert.Equal(SpellAim.Enemy, fireball.Aim);
        Assert.False(fireball.IsPassive);
        Assert.NotNull(fireball.IconPath);
        Assert.NotNull(Source.Read(fireball.IconPath));
        Assert.Equal("Fire", spells.TabOf(133)?.Name);
    }

    /// <summary>Lesser Heal aims at a friend, and Power Word: Fortitude too; a racial like Shadowmeld is in no class tab.</summary>
    [Fact]
    public void SpellsAimAndSortAsTheirEffectsSay()
    {
        var spells = SpellTable.Read(Source);

        var heal = spells.Get(2050)!;
        Assert.Equal((SpellAim.Friend, false, 1500, "Holy", true), (heal.Aim, heal.NeedsForm, heal.CastTimeMs, spells.TabOf(2050)?.Name, spells.InSpellbook(2050)));

        // Immolation Aura is for Metamorphosis only.
        Assert.True(spells.Get(50589)?.NeedsForm);
        Assert.Equal(SpellAim.Friend, spells.Get(1243)?.Aim);
        Assert.Null(spells.TabOf(58984));
        // Blizzard's first rank lands 8 yards wide up to 30 yards away, as its tooltip says.
        var blizzard = spells.Get(10)!;
        Assert.Equal((SpellAim.Ground, 8f, 30f), (blizzard.Aim, blizzard.Radius, blizzard.Range));
        Assert.Equal(35f, spells.Get(133)?.Range);
    }

    /// <summary>
    /// The book lists what skill lines teach: Fireball, Auto Attack and a racial, but not the
    /// Voidwalker's Sacrifice, which is the pet's, nor a spell no skill line teaches, as Hearthstone's.
    /// </summary>
    [Fact]
    public void TheSpellbookListsWhatSkillLinesTeach()
    {
        var spells = SpellTable.Read(Source);

        Assert.True(spells.InSpellbook(133));
        Assert.True(spells.InSpellbook(6603));
        Assert.True(spells.InSpellbook(58984));
        Assert.False(spells.InSpellbook(8690));
        Assert.False(spells.InSpellbook(47985));
        Assert.Null(spells.TabOf(47985));
    }

    /// <summary>Items show their icon in bags: a one-handed sword's display names one that's in the client.</summary>
    [Fact]
    public void ItemsHaveIcons()
    {
        var table = CreatureDisplayTable.Read(Source);

        var icon = table.ItemIconPath(table.ItemDisplayOf(1899));

        Assert.NotNull(icon);
        Assert.StartsWith(@"Interface\Icons\", icon);
        Assert.NotNull(Source.Read(icon));
    }
}
