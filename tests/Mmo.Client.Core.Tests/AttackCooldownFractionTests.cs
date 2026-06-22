using System;
using Mmo.Client.Core;
using Xunit;

namespace Mmo.Client.Core.Tests;

// COMBAT-TUNING: the radial-cooldown sweep-fraction math feeding the LMB autoattack HUD slot. The function is a
// pure read-out over (last-attack-send-time, cooldown-ms-in-effect-then, now): it returns the remaining fraction in
// [0,1] and the remaining seconds, with no attack / non-positive cooldown / elapsed cooldown all reading 0 (ready).
// Unit-tested directly via the internal static so no live client/socket is needed.
public sealed class AttackCooldownFractionTests
{
    [Fact]
    public void NoAttackIsReady()
    {
        var fraction = MmoClient.ComputeCooldownFraction(null, 600d, TimeSpan.FromSeconds(10), out var remaining);

        Assert.Equal(0d, fraction);
        Assert.Equal(0d, remaining);
    }

    [Fact]
    public void JustFiredIsFull()
    {
        var sentAt = TimeSpan.FromSeconds(5);
        var fraction = MmoClient.ComputeCooldownFraction(sentAt, 600d, sentAt, out var remaining);

        Assert.Equal(1d, fraction, 6);
        Assert.Equal(0.6d, remaining, 6);
    }

    [Fact]
    public void HalfwayThroughIsHalf()
    {
        var sentAt = TimeSpan.FromSeconds(5);
        var now = sentAt + TimeSpan.FromMilliseconds(300); // half of a 600 ms cooldown
        var fraction = MmoClient.ComputeCooldownFraction(sentAt, 600d, now, out var remaining);

        Assert.Equal(0.5d, fraction, 6);
        Assert.Equal(0.3d, remaining, 6);
    }

    [Fact]
    public void ElapsedCooldownIsReady()
    {
        var sentAt = TimeSpan.FromSeconds(5);
        var now = sentAt + TimeSpan.FromMilliseconds(600); // exactly elapsed
        var fraction = MmoClient.ComputeCooldownFraction(sentAt, 600d, now, out var remaining);

        Assert.Equal(0d, fraction);
        Assert.Equal(0d, remaining);

        // Well past elapsed also reads ready.
        var later = sentAt + TimeSpan.FromSeconds(2);
        Assert.Equal(0d, MmoClient.ComputeCooldownFraction(sentAt, 600d, later, out _));
    }

    [Fact]
    public void NonPositiveCooldownIsReady()
    {
        var sentAt = TimeSpan.FromSeconds(5);
        Assert.Equal(0d, MmoClient.ComputeCooldownFraction(sentAt, 0d, sentAt, out _));
        Assert.Equal(0d, MmoClient.ComputeCooldownFraction(sentAt, -10d, sentAt, out _));
    }

    [Fact]
    public void ClockBeforeSendClampsToFull()
    {
        // A `now` earlier than the send time (clock jitter) clamps elapsed to 0 -> full fraction, never > 1.
        var sentAt = TimeSpan.FromSeconds(5);
        var earlier = sentAt - TimeSpan.FromMilliseconds(50);
        var fraction = MmoClient.ComputeCooldownFraction(sentAt, 600d, earlier, out _);

        Assert.Equal(1d, fraction, 6);
        Assert.True(fraction <= 1d);
    }

    // A live combat.attackCooldownMs change takes effect via the cooldown-ms snapshotted at send time: a swing sent
    // under a longer cooldown sweeps over that longer window (the in-flight sweep isn't retroactively rescaled).
    [Fact]
    public void CooldownDurationDrivesTheSweepWindow()
    {
        var sentAt = TimeSpan.FromSeconds(5);
        var now = sentAt + TimeSpan.FromMilliseconds(500);

        // Under a 1000 ms cooldown, 500 ms in is half remaining.
        Assert.Equal(0.5d, MmoClient.ComputeCooldownFraction(sentAt, 1000d, now, out _), 6);
        // Under a 600 ms cooldown, the same 500 ms in is nearly done.
        Assert.Equal(100d / 600d, MmoClient.ComputeCooldownFraction(sentAt, 600d, now, out _), 6);
    }
}
