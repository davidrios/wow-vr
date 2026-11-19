using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using Xunit;

namespace WowVR.Formats.Tests;

/// <summary>Spell descriptions with their codes filled in, as the game's tooltips show them.</summary>
public class SpellTextTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    private static SpellTable? s_spells;

    private SpellTable Spells
    {
        get
        {
            if (client.Source is not { } source)
            {
                Assert.Skip("WOWVR_CLIENT_PATH is not set.");
                return null!;
            }

            return s_spells ??= SpellTable.Read((MpqAssetSource)source);
        }
    }

    private string Text(int id, bool aura = false)
    {
        var spell = Spells.Get(id)!;
        return SpellText.Format(aura ? spell.AuraText : spell.Description, spell, Spells.Get);
    }

    [Theory]
    [InlineData(133, false, "Hurls a fiery ball that causes 14 to 22 Fire damage and an additional 2 Fire damage over 4 sec.")]
    [InlineData(133, true, "1 Fire damage every 2 seconds.")]
    [InlineData(1459, false, "Increases the target's Intellect by 2 for 30 min.")]
    [InlineData(774, false, "Heals the target for 40 over 15 sec.")]
    [InlineData(6673, false, "The warrior shouts, increasing attack power of all raid and party members within 30 yards by 15.  Lasts 2 min.")]
    public void CodesAreFilledIn(int spell, bool aura, string expected) => Assert.Equal(expected, Text(spell, aura));

    /// <summary>Another spell's duration, a multiplied value, and a talent's condition taking the plain branch.</summary>
    [Fact]
    public void OtherSpellsMultipliersAndConditions()
    {
        Assert.EndsWith("cannot be shielded again for 15 sec.", Text(17));
        Assert.Contains("it will cause 990 damage to the dispeller and silence them for 5 sec.", Text(30108));
        Assert.EndsWith("from a friendly Undead target.", Text(47541));
        Assert.DoesNotContain("$", Text(47541));
    }

    [Fact]
    public void RangesAndTimesComeFromTheirTables()
    {
        var fireball = Spells.Get(133)!;
        var rend = Spells.Get(772)!;

        Assert.Equal((35f, 4000), (fireball.Range, fireball.DurationMs));
        Assert.True(rend.MeleeRange);
        Assert.True(Spells.Get(15407)!.IsChanneled); // Mind Flay
        Assert.False(fireball.IsChanneled);
    }

    /// <summary>Without the client: the codes on a spell made up here.</summary>
    [Fact]
    public void MadeUpSpellFormats()
    {
        var spell = new SpellRecord(1, "Test", "", "", null, 0, 0, 0, 0, 0, 0, 0, 0, 0, SpellAim.Enemy)
        {
            DurationMs = 12000,
            Effects = [new SpellEffect(6, 3, 9, 1, 3000, 8, 0, 0, 0, 0), new SpellEffect(2, 0, 99, 21, 0, 0, 3, 0, 0, 0), new SpellEffect(0, 0, 0, 0, 0, 0, 0, 0, 0, 0)],
            ProcChance = 15,
        };

        Assert.Equal("10 every 3 sec, 40 in all, for 12 sec", SpellText.Format("$s1 every $t1 sec, $o1 in all, for $d", spell, _ => null));
        Assert.Equal("100 to 120 at 3 targets, 15% within 8 yards", SpellText.Format("$s2 at $x2 target$l:s;, $h% within $a1 yards", spell, _ => null));
        Assert.Equal("Half: 5. Twice: 20.", SpellText.Format("Half: ${$m1/2}. Twice: $*2;s1.", spell, _ => null));
        Assert.Equal("plain", SpellText.Format("$?s100[talented][plain]", spell, _ => null));

        // A variable naming itself, as broken data might, stops rather than overflowing the stack.
        var looping = spell with { Variables = new Dictionary<string, string> { ["a"] = "${$<a>+1}" } };
        Assert.DoesNotContain("$", SpellText.Format("loop $<a> end", looping, _ => null));
    }
}
