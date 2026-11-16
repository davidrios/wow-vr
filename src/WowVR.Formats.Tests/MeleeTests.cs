using WowVR.Formats.Assets;
using WowVR.Formats.World;
using Xunit;

namespace WowVR.Formats.Tests;

/// <summary>Melee's clips, sounds and outcomes. Those reading the client skip without it.</summary>
public class MeleeTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    private MpqAssetSource Source
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

    /// <summary>A Worn Shortsword is a metal one-hander; a Bent Staff a wooden staff, swung loose and heavy.</summary>
    [Fact]
    public void ItemsSayHowTheyreHeldAndWhatTheyreMadeOf()
    {
        var table = CreatureDisplayTable.Read(Source);

        var sword = table.ItemKindOf(25)!;
        var staff = table.ItemKindOf(35)!;

        Assert.Equal((WeaponGrip.OneHand, true, SwingWeight.Medium), (sword.Grip, sword.IsMetal, sword.Weight));
        Assert.Equal((WeaponGrip.TwoHandLoose, false, SwingWeight.Heavy), (staff.Grip, staff.IsMetal, staff.Weight));
    }

    /// <summary>The impact sounds' names in SoundEntries: a sword's on flesh, a critical on chain, a parry with metal, and a fist.</summary>
    [Fact]
    public void ImpactsComeFromTheWeaponAndWhatItHits()
    {
        var sounds = CombatSoundTable.Read(Source);
        var sword = new ItemKind(ItemKind.WeaponClass, 7, -1, 1, 13);

        Assert.Equal(143, sounds.Impact(sword, ImpactSurface.Flesh, critical: false));       // Sword1H_ArmorFlesh
        Assert.Equal(146, sounds.Impact(sword, ImpactSurface.Chain, critical: true));        // Sword1H_ArmorChainCritical
        Assert.Equal(1002, sounds.Impact(sword, ImpactSurface.ParryMetal, critical: false)); // 1hParryMetalHitMetal
        Assert.Equal(1014, sounds.Impact(null, ImpactSurface.Flesh, critical: false));       // Unarmed_Generic
        Assert.Equal(235, sounds.Swing(sword, critical: false));                              // MediumWeaponNormal
        Assert.Equal(234, sounds.Swing(null, critical: true));                                // LightWeaponCritical
    }

    [Theory]
    [InlineData(0x2u, 1, 50u, MeleeOutcome.Hit)]
    [InlineData(0x202u, 1, 90u, MeleeOutcome.Critical)]
    [InlineData(0x12u, 0, 0u, MeleeOutcome.Miss)]
    [InlineData(0x2u, 2, 0u, MeleeOutcome.Dodge)]
    [InlineData(0x2u, 3, 0u, MeleeOutcome.Parry)]
    [InlineData(0x2002u, 1, 0u, MeleeOutcome.Block)]
    [InlineData(0x2002u, 1, 20u, MeleeOutcome.Hit)]
    [InlineData(0x22u, 1, 0u, MeleeOutcome.Absorb)]
    public void OutcomesReadTheServersFlags(uint hitInfo, byte victimState, uint damage, MeleeOutcome expected) =>
        Assert.Equal(expected, MeleeHits.Outcome(hitInfo, victimState, damage));

    [Fact]
    public void OffHandSwingsPlayTheirOwnClipFirst()
    {
        Assert.Equal(MeleeAnimations.AttackOff, MeleeAnimations.Attack(WeaponGrip.OneHand, offHand: true)[0]);
        Assert.Equal(MeleeAnimations.Attack2HL, MeleeAnimations.Attack(WeaponGrip.TwoHandLoose, offHand: false)[0]);
        Assert.Equal(MeleeAnimations.ShieldBlock, MeleeAnimations.Reaction(MeleeOutcome.Block, new MeleeWeapons(null, new ItemKind(ItemKind.ArmorClass, 6, -1, 1, 14)))[0]);
    }
}
