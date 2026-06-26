using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Shared.Tests;

// MOVEMENT-ACTIONS (Phase A): the SHARED ballistic-Z formula (design §1.4.2) — the Z half of the determinism
// contract. These pin the derived constants (g = 8H/T², v0 = 4H/T), the apex-at-midpoint property (z(N/2) == H),
// the return-to-ground at tick N (z(N) == 0), and the per-tick determinism the Phase-B predictor reproduces. Pure
// math, so they live next to the formula in Mmo.Shared.Tests.
public sealed class BallisticArcTests
{
    private const double Eps = 1e-9;

    [Theory]
    [InlineData(2d, 10u, 20)]
    [InlineData(1.5d, 12u, 20)]
    [InlineData(0.4d, 6u, 30)]
    public void ApexEqualsJumpHeight_AtMidpointTick(double height, uint airborneTicks, int tickRate)
    {
        // z peaks at the midpoint tick (N/2) at exactly the def's JumpHeight.
        var apex = BallisticArc.HeightOffsetAtTick(height, airborneTicks, tickRate, airborneTicks / 2);
        Assert.Equal(height, apex, 1e-9);
    }

    [Theory]
    [InlineData(2d, 10u, 20)]
    [InlineData(1.5d, 12u, 20)]
    public void ReturnsToGround_AtTickN(double height, uint airborneTicks, int tickRate)
    {
        // At tick N the arithmetic returns z = 0 (the boundary the executor then snaps explicitly).
        var atN = BallisticArc.HeightOffsetAtTick(height, airborneTicks, tickRate, airborneTicks);
        Assert.Equal(0d, atN, Eps);
    }

    [Fact]
    public void TakeoffIsGround_AtTickZero()
    {
        Assert.Equal(0d, BallisticArc.HeightOffsetAtTick(2d, 10u, 20, 0u), Eps);
    }

    [Fact]
    public void DerivedConstants_MatchClosedForm()
    {
        // g = 8H/T², v0 = 4H/T with T = N/tickRate.
        const double h = 2d;
        const uint n = 10;
        const int tickRate = 20;
        var t = n / (double)tickRate; // 0.5 s

        Assert.Equal(8d * h / (t * t), BallisticArc.Gravity(h, n, tickRate), Eps);
        Assert.Equal(4d * h / t, BallisticArc.LaunchVelocity(h, n, tickRate), Eps);
    }

    [Fact]
    public void Arc_IsSymmetricAndMonotonicUpThenDown()
    {
        const double h = 2d;
        const uint n = 10;
        const int tickRate = 20;

        // Rises to the apex, then falls — and is symmetric about the midpoint (z(i) == z(N-i)).
        for (var i = 0u; i <= n / 2; i++)
        {
            var up = BallisticArc.HeightOffsetAtTick(h, n, tickRate, i);
            var mirrored = BallisticArc.HeightOffsetAtTick(h, n, tickRate, n - i);
            Assert.Equal(up, mirrored, 1e-9);
        }
    }

    [Fact]
    public void Determinism_DerivedAndCachedFormsAgree_BitForBit()
    {
        // The executor caches g/v0 then calls the (g, v0) overload; it must be bit-identical to the (H, N) overload.
        const double h = 2.25d;
        const uint n = 12;
        const int tickRate = 20;
        var g = BallisticArc.Gravity(h, n, tickRate);
        var v0 = BallisticArc.LaunchVelocity(h, n, tickRate);

        for (var i = 0u; i <= n; i++)
        {
            var a = BallisticArc.HeightOffsetAtTick(h, n, tickRate, i);
            var b = BallisticArc.HeightOffsetAtTick(g, v0, tickRate, i);
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(a), System.BitConverter.DoubleToInt64Bits(b));
        }
    }

    [Fact]
    public void DegenerateInputs_AreFlat()
    {
        // No height / no ticks / bad rate => a flat (always-ground) arc, never NaN/Inf.
        Assert.Equal(0d, BallisticArc.Gravity(0d, 10u, 20), Eps);
        Assert.Equal(0d, BallisticArc.LaunchVelocity(2d, 0u, 20), Eps);
        Assert.Equal(0d, BallisticArc.HeightOffsetAtTick(2d, 10u, 0, 5u), Eps);
    }
}
