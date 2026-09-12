using System;
using GameServer.Input;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Input;

/// <summary>
/// The accepted-attack rate audit (roadmap A4).
/// </summary>
/// <remarks>
/// <para>
/// These tests exist because of a blind spot, not because of a known exploit.
/// <c>CombatLogic.ValidateAttack</c> compares the simulation tick against
/// <c>CooldownUntilTick</c>, which lives on the attacker's ENTITY, so it is exact for one
/// entity and blind to anything that hands an account a different one -- and
/// <c>PlayerState</c> persists <c>UserId, X, Y, Hp, MaxHp, MapId</c>, not the cooldown.
/// </para>
/// <para>
/// <b>The obvious route is closed today.</b> A reconnect inside the hold window reattaches
/// the same entity, cooldown intact; only a map transfer or an absence past the hold TTL
/// yields a fresh one, and both are far slower than the 500 ms cooldown they reset. What
/// makes the audit worth having anyway is that an account exceeding the rate through ANY
/// such route is never refused -- every attack is individually valid -- so the rejection
/// counters (A1) and the anomaly score (A2) stay silent throughout. This is the only
/// counter that would move.
/// </para>
/// <para>
/// The tests are written against the PROPERTY -- more accepted attacks per account than one
/// cooldown allows -- rather than against any named route, so a route nobody has thought of
/// fails them too. <see cref="AttackRateAuditSeamTests"/> demonstrates the blindness itself
/// against a real world and a real <c>InputHandler</c>.
/// </para>
/// </remarks>
public class AttackRateAuditTests
{
    private const int TickRate = 15;
    private static int CooldownTicks => GameConstants.AttackCooldownTicks(TickRate);

    private static AttackRateAudit NewAudit() => new(TickRate, CooldownTicks);

    [Fact]
    public void PermittedRate_IsDerivedFromTheCooldownItAudits()
    {
        var audit = NewAudit();

        Assert.Equal(150, audit.WindowTicks);                 // 10s at 15Hz
        Assert.Equal(8, CooldownTicks);                       // ceil(500ms * 15 / 1000)

        // One free attack, then one per cooldown across the window, plus the slack.
        Assert.Equal(1 + 150 / 8 + AttackRateAudit.DefaultSlack, audit.Permitted);
    }

    [Fact]
    public void APlayerAttackingExactlyOnCooldown_IsNeverFlagged()
    {
        var audit = NewAudit();

        // Ten windows' worth: an honest player cannot drift into a flag over time.
        for (ulong tick = 0; tick < 1500; tick += (ulong)CooldownTicks)
            Assert.False(audit.RecordAccepted("honest", tick), $"flagged at tick {tick}");

        Assert.Equal(0, audit.ViolationsFor("honest"));
        Assert.Equal(0, audit.Violations);
    }

    [Fact]
    public void TwiceTheCooldownRate_IsFlagged()
    {
        var audit = NewAudit();
        bool flagged = false;

        // What any entity-replacing route buys: the same account landing attacks at half
        // the cooldown interval. Every one of these passed ValidateAttack on some entity,
        // so nothing else in the server has anything to say about them.
        for (ulong tick = 0; tick < 300 && !flagged; tick += (ulong)(CooldownTicks / 2))
            flagged = audit.RecordAccepted("cheater", tick);

        Assert.True(flagged, "an account at twice the permitted rate was not flagged");
        Assert.Equal(1, audit.Violations);
    }

    [Fact]
    public void ASustainedViolation_FlagsOnTheCrossing_NotOncePerAttack()
    {
        var audit = NewAudit();
        int flags = 0;

        for (ulong tick = 0; tick < 600; tick += (ulong)(CooldownTicks / 2))
            if (audit.RecordAccepted("cheater", tick)) flags++;

        // Otherwise a single cheating session would emit hundreds of identical warnings and
        // the log would be the thing that breaks, not the cheat.
        Assert.Equal(1, flags);
        Assert.Equal(1, audit.ViolationsFor("cheater"));
    }

    [Fact]
    public void TheAccountIsTheKey_SoTwoHonestPlayersDoNotAddUp()
    {
        var audit = NewAudit();

        // Same ticks, two accounts, each at the legal rate. A per-map or per-connection
        // counter would see one stream at twice the rate and flag both.
        for (ulong tick = 0; tick < 1500; tick += (ulong)CooldownTicks)
        {
            Assert.False(audit.RecordAccepted("player-a", tick));
            Assert.False(audit.RecordAccepted("player-b", tick));
        }

        Assert.Equal(0, audit.Violations);
    }

    [Fact]
    public void TheWindowSlides_SoSeparatedBurstsAreNotAccumulated()
    {
        var audit = NewAudit();
        ulong tick = 0;

        // Three bursts, each just inside the permitted count, separated by more than a
        // window. A tumbling window would also pass this; the next test is the one that
        // separates them.
        for (int burst = 0; burst < 3; burst++)
        {
            for (int i = 0; i < audit.Permitted; i++)
            {
                Assert.False(audit.RecordAccepted("bursty", tick), $"flagged in burst {burst}");
                tick += (ulong)CooldownTicks;
            }
            tick += (ulong)audit.WindowTicks * 2;
        }

        Assert.Equal(0, audit.Violations);
    }

    [Fact]
    public void AViolationStraddlingAWindowBoundary_IsStillCaught()
    {
        var audit = NewAudit();
        var half = (ulong)(audit.WindowTicks / 2);

        // Half a window of over-rate attacks, then half a window more, arranged so that
        // neither half alone exceeds the count but the straddle does. A tumbling window
        // resets at the boundary and calls this compliant -- which is exactly the rate a
        // reconnect-reset produces, so the distinction is not academic.
        bool flagged = false;
        ulong tick = 0;
        int perHalf = audit.Permitted - 1;
        ulong step = half / (ulong)perHalf;

        for (int i = 0; i < perHalf * 2 && !flagged; i++)
        {
            flagged = audit.RecordAccepted("straddler", tick);
            tick += step;
        }

        Assert.True(flagged, "an over-rate run across the window boundary went unflagged");
    }

    [Fact]
    public void TheAuditStopsGrowingAtItsCap_AndSaysSo()
    {
        var audit = new AttackRateAudit(TickRate, CooldownTicks, maxAccounts: 4);

        for (int i = 0; i < 10; i++) audit.RecordAccepted($"user-{i}", 0);

        Assert.Equal(4, audit.TrackedAccounts);

        // A silent cap would make the audit describe a population it stopped watching.
        Assert.Equal(6, audit.DroppedAccounts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonsenseTickRateOrCooldown_ThrowsRatherThanAuditingNothing(int bad)
    {
        // Silently falling back to a default would produce a bound unrelated to the rule in
        // force, and an audit with the wrong bound is worse than no audit: it reports.
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttackRateAudit(bad, CooldownTicks));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttackRateAudit(TickRate, bad));
    }

    [Fact]
    public void AnEmptyUserId_IsIgnoredRatherThanTrackedAsOneAccount()
    {
        var audit = NewAudit();

        for (ulong tick = 0; tick < 600; tick++)
            Assert.False(audit.RecordAccepted("", tick));

        Assert.Equal(0, audit.TrackedAccounts);
    }
}
