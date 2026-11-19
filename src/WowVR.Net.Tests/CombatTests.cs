using Xunit;

namespace WowVR.Net.Tests;

/// <summary>Auras, spell logs and item queries, built as AzerothCore writes them.</summary>
public class CombatTests
{
    /// <summary>A self-cast buff with a duration, one from another caster without, and a slot cleared.</summary>
    [Fact]
    public void AurasReadTheirCasterTimeAndRemovals()
    {
        var w = new PacketWriter().Packed(7)
            .U8(0).U32(1459).U8(0x08 | 0x10 | 0x20).U8(12).U8(0).U32(1_800_000).U32(1_000_000)
            .U8(1).U32(589).U8(0x80 | 0x01).U8(20).U8(2).Packed(0xF130000000000042)
            .U8(2).U32(0);

        var updates = RealmSession.ParseAuras(w.ToArray(), 100, out var unit);

        Assert.Equal(7ul, unit);
        Assert.Equal(3, updates.Count);
        var buff = updates[0].Aura!;
        Assert.Equal((1459u, 7ul, 1_800_000, false), (buff.Spell, buff.Caster, buff.MaxDurationMs, buff.Harmful));
        Assert.Equal(990, buff.LeftAt(110)!.Value, 3);
        var debuff = updates[1].Aura!;
        Assert.Equal((589u, 0xF130000000000042ul, (byte)2, true), (debuff.Spell, debuff.Caster, debuff.Stacks, debuff.Harmful));
        Assert.Null(debuff.LeftAt(100));
        Assert.Equal(((byte)2, (AuraInfo?)null), updates[2]);
    }

    [Fact]
    public void SpellDamageReadsItsCriticalAndAbsorb()
    {
        var w = new PacketWriter().Packed(2).Packed(1).U32(133).U32(40).U32(0).U8(4).U32(5).U32(3).U8(0).U8(0).U32(0).U32(0x2).U8(0);

        var hit = RealmSession.ParseSpellDamage(w.ToArray());

        Assert.Equal((2ul, 1ul, 133u, SpellEffectKind.Damage, 40u, true), (hit.Target, hit.Caster, hit.Spell, hit.Kind, hit.Amount, hit.Critical));
        Assert.Equal(((byte)4, 5u, 3u), (hit.School, hit.Absorbed, hit.Resisted));
    }

    [Fact]
    public void HealReadsItsOverheal()
    {
        var w = new PacketWriter().Packed(1).Packed(1).U32(2061).U32(200).U32(50).U32(0).U8(1).U8(0);

        var heal = RealmSession.ParseSpellHeal(w.ToArray());

        Assert.Equal((SpellEffectKind.Heal, 200u, 50u, true), (heal.Kind, heal.Amount, heal.Over, heal.Critical));
    }

    [Fact]
    public void PeriodicTicksReadDamageAndHealsAndSkipTheRest()
    {
        var damage = new PacketWriter().Packed(2).Packed(1).U32(980).U32(1).U32(3).U32(7).U32(0).U32(32).U32(0).U32(0).U8(0);
        var heal = new PacketWriter().Packed(1).Packed(1).U32(774).U32(1).U32(8).U32(8).U32(0).U32(0).U8(1);
        var other = new PacketWriter().Packed(1).Packed(1).U32(1).U32(1).U32(999);

        var tick = RealmSession.ParsePeriodic(damage.ToArray())!;
        var mend = RealmSession.ParsePeriodic(heal.ToArray())!;

        Assert.Equal((SpellEffectKind.Damage, 7u, true, (byte)32), (tick.Kind, tick.Amount, tick.Periodic, tick.School));
        Assert.Equal((SpellEffectKind.Heal, 8u, true), (mend.Kind, mend.Amount, mend.Critical));
        Assert.Null(RealmSession.ParsePeriodic(other.ToArray()));
    }

    /// <summary>A sword with a stat, a damage, an on-equip spell and a description, as the server sends it.</summary>
    [Fact]
    public void ItemQueryReadsStatsDamageSpellsAndText()
    {
        var w = new PacketWriter().U32(25).U32(2).U32(7).U32(uint.MaxValue).CString("Worn Shortsword").U8(0).U8(0).U8(0)
            .U32(1542).U32(1).U32(0).U32(0).U32(35).U32(7).U32(21)
            .U32(uint.MaxValue).U32(uint.MaxValue).U32(2).U32(1).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0)
            .U32(0).U32(1).U32(0)
            .U32(1).U32(7).U32(3)
            .U32(0).U32(0)
            .F32(1).F32(3).U32(0).F32(0).F32(0).U32(0)
            .U32(10).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0)
            .U32(1900).U32(0).F32(0);
        for (var s = 0; s < 5; s++)
        {
            w.U32(s == 0 ? 7597u : 0).U32(s == 0 ? 1u : 0).U32(0).U32(0).U32(0).U32(0);
        }

        w.U32(2).CString("Old and worn.").U32(0).U32(0).U32(0).U32(123).U32(0).U32(1).U32(3).U32(0).U32(0).U32(0).U32(0).U32(20)
            .U32(0).U32(0).U32(0).U32(0);
        for (var s = 0; s < 3; s++)
        {
            w.U32(s == 0 ? 2u : 0).U32(0);
        }

        w.U32(0).U32(0).U32(0).F32(0).U32(0).U32(0).U32(0);

        var item = RealmSession.ParseItem(w.ToArray());

        Assert.Equal(("Worn Shortsword", 2u, 7u, 2u, 1u), (item.Name, item.Class, item.Subclass, item.ItemLevel, item.RequiredLevel));
        Assert.Equal([(7u, 3)], item.Stats);
        Assert.Equal([(1f, 3f, 0u)], item.Damage);
        Assert.Equal((10u, 1900u, 2u, "Old and worn.", 20u, 123u), (item.Armor, item.DelayMs, item.Bonding, item.Description, item.MaxDurability, item.StartQuest));
        Assert.Equal([(7597u, 1u)], item.Spells);
        Assert.Equal(2u, item.Sockets[0]);
    }
}

public class ChatMarkupTests
{
    [Fact]
    public void ItemLinksBecomeColoredUrls()
    {
        var bbcode = ChatMarkup.ToBbcode("Selling |cff1eff00|Hitem:25:0:0:0:0:0:0:0:80|h[Worn Shortsword]|h|r cheap");

        Assert.Equal("Selling [color=#1eff00][url=item:25][lb]Worn Shortsword][/url][/color] cheap", bbcode);
    }

    /// <summary>A link's target from another player can't carry tags, and an unknown kind isn't a link.</summary>
    [Fact]
    public void LinkTargetsCantInjectTags()
    {
        Assert.Equal("z", ChatMarkup.ToBbcode("|H]x[font_size=400]:1|hz|h"));
        Assert.Equal("z", ChatMarkup.ToBbcode("|Htext:1|hz|h"));
        Assert.Equal("z", ChatMarkup.ToBbcode("|Hitem:1]x|hz|h"));
    }

    /// <summary>A color ended inside a link closes and reopens around it, so tags never cross.</summary>
    [Fact]
    public void CrossedTagsCloseInOrder()
    {
        Assert.Equal("[color=#ff0000][url=item:5][lb]x][/url][/color][url=item:5][/url]", ChatMarkup.ToBbcode("|cffff0000|Hitem:5|h[x]|r|h"));
    }

    [Fact]
    public void TextCantMakeTagsAndBarsAndTexturesAreHandled()
    {
        Assert.Equal("[lb]b]hi[lb]/b] a|b", ChatMarkup.ToBbcode("[b]hi[/b] a||b"));
        Assert.Equal("go ", ChatMarkup.ToBbcode(@"go |TInterface\Icons\x:16|t"));
        Assert.StartsWith("[color=#ff0000]red", ChatMarkup.ToBbcode("|cffff0000red"));
        Assert.EndsWith("[/color]", ChatMarkup.ToBbcode("|cffff0000red"));
    }
}
