using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using System.Text;
using Xunit;

namespace Mmo.Shared.Tests;

public sealed class ProtocolCodecTests
{
    [Fact]
    public void LoginRequestRoundTrips()
    {
        var original = new LoginRequestMessage("account", "display");

        var decoded = ProtocolCodec.Decode(ProtocolCodec.Encode(original));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void WorldSnapshotRoundTrips()
    {
        var original = new WorldSnapshotMessage(
            42,
            77,
            120,
            false,
            2,
            6,
            new[]
            {
                new EntityStateSnapshot(99, WorldVector.FromTile(12, -25), Direction8.NE)
            },
            RecipientStepSeq: 555,
            LastInputSeq: 909);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(original.ServerTick, decoded.ServerTick);
        Assert.Equal(77u, decoded.SnapshotSequence);
        Assert.Equal(555u, decoded.RecipientStepSeq);
        // CONTINUOUS MIGRATION (v36): the LastInputSeq header field round-trips after RecipientStepSeq.
        Assert.Equal(909u, decoded.LastInputSeq);
        Assert.Equal(120, decoded.TotalEntities);
        Assert.False(decoded.IsComplete);
        Assert.Equal(2, decoded.ChunkIndex);
        Assert.Equal(6, decoded.ChunkCount);
        var entity = Assert.Single(decoded.Entities);
        Assert.Equal(99u, entity.NetworkId);
        // CONTINUOUS MIGRATION (v36): the wire now carries the CONTINUOUS fixed-point position. A tile-centre encodes
        // losslessly, so this exact-integer position round-trips byte-for-byte.
        Assert.Equal(WorldVector.FromTile(12, -25), entity.Position);
        Assert.Equal(Direction8.NE, entity.Facing);
    }

    // CONTINUOUS MIGRATION (v36): a genuinely FRACTIONAL position round-trips within the fixed-point quantum (1/16
    // tile = 0.0625 u) — the Q12.4 wire precision the migration locked in.
    [Fact]
    public void WorldSnapshotContinuousPositionRoundTripsWithinOneSixteenth()
    {
        var original = new WorldSnapshotMessage(
            1,
            1,
            new[]
            {
                new EntityStateSnapshot(7, new WorldVector(3.3333, -8.77), Direction8.S)
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        var entity = Assert.Single(decoded.Entities);
        Assert.True(System.Math.Abs(entity.Position.X - 3.3333) <= 1d / 16d);
        Assert.True(System.Math.Abs(entity.Position.Y - (-8.77)) <= 1d / 16d);
    }

    // MOVEMENT-ACTIONS Phase B1 (v38): the new client->server ActionIntentMessage round-trips byte-for-byte (its own
    // ActionSeq, the action id byte, the quantized heading ushort, the authored tick).
    [Fact]
    public void ActionIntentRoundTrips()
    {
        var original = new ActionIntentMessage(
            ActionSeq: 4242u,
            ActionId: (byte)Mmo.Shared.Domain.Actions.ActionId.Jump,
            Heading: AimAngle.Quantize(System.Math.PI / 3d),
            AuthoredTick: 777u);

        var decoded = Assert.IsType<ActionIntentMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(original, decoded);
        Assert.Equal(4242u, decoded.ActionSeq);
        Assert.Equal((byte)Mmo.Shared.Domain.Actions.ActionId.Jump, decoded.ActionId);
        Assert.Equal(original.Heading, decoded.Heading);
        Assert.Equal(777u, decoded.AuthoredTick);
    }

    // Note: the standalone v38 VerticalOffset round-trips (grounded / airborne / mixed) were removed — the v39
    // combined-flags tests below (resting / airborne-no-velocity / all-four-combos) exercise the SAME VerticalOffset
    // wire PLUS velocity in the combined flags byte, so they cover strictly more (see docs/test-audit.md §A).

    // REMOTE-WALK Phase 1 (v39): a RESTING grounded entity (VerticalOffset 0, Velocity Zero) round-trips with the
    // combined flags byte 0 and no trailing height/velocity bytes — the common case stays at +1 byte/entity.
    [Fact]
    public void EntityStateRestingGroundedRoundTripsAsFlagsZero()
    {
        var original = new WorldSnapshotMessage(
            1,
            1,
            new[]
            {
                new EntityStateSnapshot(7, WorldVector.FromTile(3, 4), Direction8.S, Depleted: false, Health: 50, MaxHealth: 100, VerticalOffset: 0d, Velocity: WorldVector.Zero)
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        var entity = Assert.Single(decoded.Entities);
        Assert.Equal(0d, entity.VerticalOffset);
        Assert.Equal(WorldVector.Zero, entity.Velocity);
        Assert.Equal((ushort)50, entity.Health);
        Assert.Equal((ushort)100, entity.MaxHealth);
    }

    // REMOTE-WALK Phase 1 (v39): a MOVING grounded entity (Velocity != 0, VerticalOffset 0) round-trips with the flags
    // byte 2 (moving bit only) + velX,velY signed shorts, within the 1/256-unit/sec velocity quantum.
    [Fact]
    public void EntityStateMovingGroundedVelocityRoundTripsWithinOne256th()
    {
        var velocity = new WorldVector(3.5d, -4.875d); // both exactly representable in 1/256 fixed-point
        var original = new WorldSnapshotMessage(
            1,
            1,
            new[]
            {
                new EntityStateSnapshot(9, WorldVector.FromTile(2, 2), Direction8.N, VerticalOffset: 0d, Velocity: velocity)
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        var entity = Assert.Single(decoded.Entities);
        Assert.Equal(0d, entity.VerticalOffset);
        Assert.True(System.Math.Abs(entity.Velocity.X - velocity.X) <= 1d / 256d, "velX outside the 1/256 quantum");
        Assert.True(System.Math.Abs(entity.Velocity.Y - velocity.Y) <= 1d / 256d, "velY outside the 1/256 quantum");
    }

    // REMOTE-WALK Phase 1 (v39): an AIRBORNE NOT-MOVING entity (a jump — VerticalOffset > 0, Velocity Zero, because the
    // executor drives the arc ballistically, not via Velocity) round-trips with the flags byte 1 (airborne bit only) +
    // the Q12.4 height, and carries NO velocity (the jump uses force-include, not extrapolation).
    [Fact]
    public void EntityStateAirborneNotMovingRoundTripsWithHeightNoVelocity()
    {
        const double height = 1.53125d; // exactly representable in Q12.4
        var original = new WorldSnapshotMessage(
            1,
            1,
            new[]
            {
                new EntityStateSnapshot(11, WorldVector.FromTile(2, 2), Direction8.N, VerticalOffset: height, Velocity: WorldVector.Zero)
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        var entity = Assert.Single(decoded.Entities);
        Assert.True(System.Math.Abs(entity.VerticalOffset - height) <= 1d / 16d);
        Assert.Equal(WorldVector.Zero, entity.Velocity);
    }

    // REMOTE-WALK Phase 1 (v39): an AIRBORNE + MOVING entity (both bits set — flags byte 3) round-trips with the height
    // AND the velocity, in that wire order (height then velX,velY).
    [Fact]
    public void EntityStateAirborneAndMovingRoundTripsWithHeightAndVelocity()
    {
        const double height = 0.75d;
        var velocity = new WorldVector(-2.25d, 5.0d);
        var original = new WorldSnapshotMessage(
            1,
            1,
            new[]
            {
                new EntityStateSnapshot(13, WorldVector.FromTile(2, 2), Direction8.N, VerticalOffset: height, Velocity: velocity)
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        var entity = Assert.Single(decoded.Entities);
        Assert.True(System.Math.Abs(entity.VerticalOffset - height) <= 1d / 16d);
        Assert.True(System.Math.Abs(entity.Velocity.X - velocity.X) <= 1d / 256d, "velX outside the 1/256 quantum");
        Assert.True(System.Math.Abs(entity.Velocity.Y - velocity.Y) <= 1d / 256d, "velY outside the 1/256 quantum");
    }

    // REMOTE-WALK Phase 1 (v39): a SINGLE snapshot mixing ALL FOUR flag combos (resting grounded, moving grounded,
    // airborne not-moving, airborne+moving) round-trips with NO desync — the decoder stays aligned across the
    // variable-length per-entity tails (the cardinal wire-compat risk: a single unmirrored conditional would shift
    // every entity after it). Asserts each entity's id + tail fields independently.
    [Fact]
    public void EntityStateAllFourFlagCombosInOneSnapshotRoundTripAligned()
    {
        var movingVel = new WorldVector(1.5d, -3.25d);
        var airborneMovingVel = new WorldVector(-0.5d, 2.0d);
        var original = new WorldSnapshotMessage(
            5,
            5,
            new[]
            {
                // flags 0: resting grounded
                new EntityStateSnapshot(1, WorldVector.FromTile(0, 0), Direction8.E, VerticalOffset: 0d, Velocity: WorldVector.Zero),
                // flags 2: moving grounded
                new EntityStateSnapshot(2, WorldVector.FromTile(1, 0), Direction8.W, VerticalOffset: 0d, Velocity: movingVel),
                // flags 1: airborne not-moving (a jump)
                new EntityStateSnapshot(3, WorldVector.FromTile(2, 0), Direction8.N, VerticalOffset: 1.25d, Velocity: WorldVector.Zero),
                // flags 3: airborne + moving
                new EntityStateSnapshot(4, WorldVector.FromTile(3, 0), Direction8.S, VerticalOffset: 0.5d, Velocity: airborneMovingVel),
            });

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4, decoded.Entities.Count);

        // No desync: ids stay in order across the variable-length tails.
        Assert.Equal(1u, decoded.Entities[0].NetworkId);
        Assert.Equal(2u, decoded.Entities[1].NetworkId);
        Assert.Equal(3u, decoded.Entities[2].NetworkId);
        Assert.Equal(4u, decoded.Entities[3].NetworkId);

        // flags 0: resting grounded
        Assert.Equal(0d, decoded.Entities[0].VerticalOffset);
        Assert.Equal(WorldVector.Zero, decoded.Entities[0].Velocity);

        // flags 2: moving grounded
        Assert.Equal(0d, decoded.Entities[1].VerticalOffset);
        Assert.True(System.Math.Abs(decoded.Entities[1].Velocity.X - movingVel.X) <= 1d / 256d);
        Assert.True(System.Math.Abs(decoded.Entities[1].Velocity.Y - movingVel.Y) <= 1d / 256d);

        // flags 1: airborne not-moving
        Assert.True(System.Math.Abs(decoded.Entities[2].VerticalOffset - 1.25d) <= 1d / 16d);
        Assert.Equal(WorldVector.Zero, decoded.Entities[2].Velocity);

        // flags 3: airborne + moving
        Assert.True(System.Math.Abs(decoded.Entities[3].VerticalOffset - 0.5d) <= 1d / 16d);
        Assert.True(System.Math.Abs(decoded.Entities[3].Velocity.X - airborneMovingVel.X) <= 1d / 256d);
        Assert.True(System.Math.Abs(decoded.Entities[3].Velocity.Y - airborneMovingVel.Y) <= 1d / 256d);
    }

    // CONTINUOUS MIGRATION (v36): a v35 packet (the old version byte) is no longer decodable — the atomic break is
    // mutually undecodable. The codec rejects any version != ProtocolCodec.Version.
    [Fact]
    public void V35PacketFailsToDecode()
    {
        var bytes = ProtocolCodec.Encode(new MoveIntentMessage(1, 1f, 0f, 0.05f));
        // The version byte rides immediately after the 4-byte magic (little-endian uint).
        bytes[4] = 35;
        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(bytes));
    }

    // LIVING-ENEMIES P2-POLISH (v33; DATA-DRIVEN at v40): the per-monster-TYPE tuning snapshot round-trips — a
    // count-prefixed list of per-type entries, each id + display name + a count-prefixed GENERIC list of fields
    // (Key/Label/Value/Min/Max/IsInteger). Covers multiple types, multiple fields, int + double, and the bounds.
    [Fact]
    public void MonsterTuningRoundTrips()
    {
        var original = new MonsterTuningMessage(new MonsterTuningSnapshot(new[]
        {
            new MonsterTypeSnapshot("slime", "Slime", new[]
            {
                new MonsterTuningField("maxHealth", "hp (max)", 100, 1, 100000, true),
                new MonsterTuningField("moveSpeed", "move speed (x)", 0.8, 0.1, 5, false),
                new MonsterTuningField("hopDistance", "hop distance (tiles)", 1.5, 0.25, 8, false),
            }),
            new MonsterTypeSnapshot("ogre", "Ogre", new[]
            {
                new MonsterTuningField("maxHealth", "hp (max)", 250, 1, 100000, true),
                new MonsterTuningField("hopAirborneMs", "hop airborne (ms)", 300, 50, 2000, true),
            }),
        }));

        var decoded = Assert.IsType<MonsterTuningMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(2, decoded.Tuning.Types.Count);

        var slime = decoded.Tuning.Types[0];
        Assert.Equal("slime", slime.Id);
        Assert.Equal("Slime", slime.DisplayName);
        Assert.Equal(3, slime.Fields.Count);
        Assert.Equal("maxHealth", slime.Fields[0].Key);
        Assert.Equal("hp (max)", slime.Fields[0].Label);
        Assert.Equal(100, slime.Fields[0].Value, 6);
        Assert.True(slime.Fields[0].IsInteger);
        Assert.Equal("moveSpeed", slime.Fields[1].Key);
        Assert.Equal(0.8, slime.Fields[1].Value, 6);
        Assert.False(slime.Fields[1].IsInteger);
        Assert.Equal("hopDistance", slime.Fields[2].Key);
        Assert.Equal(1.5, slime.Fields[2].Value, 6);
        Assert.Equal(0.25, slime.Fields[2].Min, 6);
        Assert.Equal(8, slime.Fields[2].Max, 6);

        var ogre = decoded.Tuning.Types[1];
        Assert.Equal("ogre", ogre.Id);
        Assert.Equal(2, ogre.Fields.Count);
        Assert.Equal("hopAirborneMs", ogre.Fields[1].Key);
        Assert.Equal(300, ogre.Fields[1].Value, 6);
        Assert.Equal(50, ogre.Fields[1].Min, 6);
        Assert.Equal(2000, ogre.Fields[1].Max, 6);
        Assert.True(ogre.Fields[1].IsInteger);
    }

    // LIVING-ENEMIES P3 (v34): the persistent spawner red-tile marker round-trips (spawner id + tile + active flag).
    [Fact]
    public void SpawnerMarkerRoundTrips()
    {
        var original = new SpawnerMarkerMessage(4242, new TileCoord(13, -7), true);

        var decoded = Assert.IsType<SpawnerMarkerMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4242u, decoded.SpawnerId);
        Assert.Equal(new TileCoord(13, -7), decoded.Tile);
        Assert.True(decoded.Active);

        var inactive = new SpawnerMarkerMessage(7, default, false);
        var decodedInactive = Assert.IsType<SpawnerMarkerMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(inactive)));
        Assert.Equal(7u, decodedInactive.SpawnerId);
        Assert.False(decodedInactive.Active);
    }

    [Fact]
    public void InteractRequestRoundTrips()
    {
        var original = new InteractRequestMessage(4242);

        var decoded = Assert.IsType<InteractRequestMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4242u, decoded.TargetNetworkId);
    }

    [Fact]
    public void InteractResultRoundTripsSuccessAndFailure()
    {
        var success = Assert.IsType<InteractResultMessage>(
            ProtocolCodec.Decode(ProtocolCodec.Encode(new InteractResultMessage(true, ""))));
        Assert.True(success.Success);
        Assert.Equal("", success.Reason);

        var failure = Assert.IsType<InteractResultMessage>(
            ProtocolCodec.Decode(ProtocolCodec.Encode(new InteractResultMessage(false, "too_far"))));
        Assert.False(failure.Success);
        Assert.Equal("too_far", failure.Reason);
    }

    [Fact]
    public void InventoryUpdateRoundTrips()
    {
        var original = new InventoryUpdateMessage(
        [
            new ItemStack("wood", 5),
            new ItemStack("stone", 0),
        ]);

        var decoded = Assert.IsType<InventoryUpdateMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(2, decoded.ChangedStacks.Count);
        Assert.Equal(new ItemStack("wood", 5), decoded.ChangedStacks[0]);
        Assert.Equal(new ItemStack("stone", 0), decoded.ChangedStacks[1]);
    }

    [Fact]
    public void WorldSnapshotRoundTripsHealthFields()
    {
        // COMBAT-S2A: public HP (current + max) rides each per-entity state. A stat-bearing entity carries a
        // partial HP (the dummy/player bar); a stat-less entity (resource) carries 0/0 ("no HP").
        var original = new WorldSnapshotMessage(
            21,
            [
                new EntityStateSnapshot(31, WorldVector.FromTile(8, 9), Direction8.S, Depleted: false, Health: 70, MaxHealth: 100),
                new EntityStateSnapshot(32, WorldVector.FromTile(2, 3), Direction8.N, Depleted: false, Health: 0, MaxHealth: 0),
            ]);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal((ushort)70, decoded.Entities[0].Health);
        Assert.Equal((ushort)100, decoded.Entities[0].MaxHealth);
        Assert.True(decoded.Entities[0].MaxHealth > 0);
        Assert.Equal((ushort)0, decoded.Entities[1].Health);
        Assert.Equal((ushort)0, decoded.Entities[1].MaxHealth);
    }

    [Fact]
    public void WorldSnapshotRoundTripsDepletedFlag()
    {
        var original = new WorldSnapshotMessage(
            7,
            [
                new EntityStateSnapshot(11, WorldVector.FromTile(3, 4), Direction8.S, Depleted: true),
                new EntityStateSnapshot(12, WorldVector.FromTile(5, 6), Direction8.N, Depleted: false),
            ]);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.True(decoded.Entities[0].Depleted);
        Assert.False(decoded.Entities[1].Depleted);
    }

    [Fact]
    public void WorldSnapshotRecipientStepSeqRoundTripsEmptyKeepAlive()
    {
        // S76: an empty/keep-alive snapshot (no entity payload, isComplete=false) must still carry the
        // recipient step seq on its header — this is exactly the idle-player case the field must survive.
        var original = new WorldSnapshotMessage(
            serverTick: 9,
            snapshotSequence: 3,
            totalEntities: 4,
            isComplete: false,
            entities: Array.Empty<EntityStateSnapshot>())
        {
            RecipientStepSeq = 4242,
        };

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Empty(decoded.Entities);
        Assert.False(decoded.IsComplete);
        Assert.Equal(4, decoded.TotalEntities);
        Assert.Equal(4242u, decoded.RecipientStepSeq);
    }

    [Fact]
    public void WorldSnapshotRecipientStepSeqRoundTripsChunked()
    {
        // S76: the seq rides a (non-first) chunk header too — every chunk carries it identically.
        var original = new WorldSnapshotMessage(
            5,
            2,
            10,
            false,
            1,
            3,
            new[] { new EntityStateSnapshot(7, WorldVector.FromTile(1, 2), Direction8.W) },
            RecipientStepSeq: 99);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(1, decoded.ChunkIndex);
        Assert.Equal(3, decoded.ChunkCount);
        Assert.Equal(99u, decoded.RecipientStepSeq);
    }

    [Fact]
    public void WorldSnapshotRecipientStepSeqDefaultsToZero()
    {
        // The convenience constructors leave it 0 (no recipient scoping) and that survives the round-trip.
        var original = new WorldSnapshotMessage(
            7,
            [new EntityStateSnapshot(11, WorldVector.FromTile(3, 4), Direction8.S)]);

        var decoded = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(0u, decoded.RecipientStepSeq);
    }

    // CONTINUOUS MIGRATION (v36): the reshaped per-input MoveIntent round-trips — InputSeq + raw DirX/DirY + DtSeconds.
    [Fact]
    public void MoveIntentRoundTrips()
    {
        var original = new MoveIntentMessage(123, 0.7071f, -0.7071f, 0.0166f);

        var decoded = Assert.IsType<MoveIntentMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(123u, decoded.InputSeq);
        Assert.Equal(0.7071f, decoded.DirX);
        Assert.Equal(-0.7071f, decoded.DirY);
        Assert.Equal(0.0166f, decoded.DtSeconds);
    }

    // CONTINUOUS MIGRATION (v36): a (0,0) direction (STOP) round-trips intact.
    [Fact]
    public void MoveIntentStopRoundTrips()
    {
        var original = new MoveIntentMessage(7, 0f, 0f, 0.05f);

        var decoded = Assert.IsType<MoveIntentMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(7u, decoded.InputSeq);
        Assert.Equal(0f, decoded.DirX);
        Assert.Equal(0f, decoded.DirY);
        Assert.Equal(0.05f, decoded.DtSeconds);
    }

    [Fact]
    public void SnapshotAckRoundTrips()
    {
        var original = new SnapshotAckMessage(77);

        var decoded = Assert.IsType<SnapshotAckMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(77u, decoded.LastSnapshotSequence);
    }

    [Fact]
    public void ServerHelloRoundTripsStepCooldownAndInterestRadius()
    {
        // CONTINUOUS MIGRATION (v37): ServerHello now also carries the replicated BodyRadiusUnits (a NON-default value
        // here so a dropped field is caught, not masked by the 0.5 default).
        var original = new ServerHelloMessage("server", ProtocolCodec.Version, 20, 140, 40.5f, 0.375f);

        var decoded = Assert.IsType<ServerHelloMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal("server", decoded.ServerName);
        Assert.Equal(ProtocolCodec.Version, decoded.ProtocolVersion);
        Assert.Equal(20, decoded.TickRate);
        Assert.Equal(140, decoded.StepCooldownMs);
        Assert.Equal(40.5f, decoded.InterestRadiusUnits);
        Assert.Equal(0.375f, decoded.BodyRadiusUnits);
    }

    [Fact]
    public void ProtocolVersionIsFortyFive()
    {
        // ECOLOGY E4 (v45, docs/ecology-v1-design.md): added the server->client RegionEcologyMessage (one authored
        // region's legible state: id + display name + tile rect + per-type {typeId, state}). One additive message +
        // tag; bump on top of v44 (telegraph T2). Pin it so a change is caught.
        Assert.Equal(45, ProtocolCodec.Version);
    }

    // TELEGRAPH T2 (v44): the telegraph announcement round-trips — the ulong id, the shape (kind + Q12.4 origin +
    // Q12.4 ushort radius), and the two absolute ticks. Origin/radius here land on exact sixteenths so the equality
    // is exact (the quantization itself is pinned separately below).
    [Fact]
    public void TelegraphMessageRoundTrips()
    {
        var original = new TelegraphMessage(
            TelegraphId: 987654321098UL,
            new TelegraphShape(TelegraphShapeKind.Circle, new WorldVector(32.5d, -7.25d), 2.5d),
            StartTick: 1000,
            ResolveTick: 1030);

        var decoded = Assert.IsType<TelegraphMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(987654321098UL, decoded.TelegraphId);
        Assert.Equal(TelegraphShapeKind.Circle, decoded.Shape.Kind);
        Assert.Equal(new WorldVector(32.5d, -7.25d), decoded.Shape.Origin);
        Assert.Equal(2.5d, decoded.Shape.Radius, 6);
        Assert.Equal(1000u, decoded.StartTick);
        Assert.Equal(1030u, decoded.ResolveTick);
        Assert.Equal(MessageType.Telegraph, decoded.Type);
    }

    // TELEGRAPH T2 (v44): the shape params are Q12.4 fixed-point on the wire — an off-grid origin/radius decodes to
    // the nearest sixteenth (round-away-from-zero, matching PositionEncoding), NOT the full double. HONEST-TELEGRAPH
    // note: server content should author radii in sixteenth steps so the drawn edge equals the resolve edge exactly.
    [Fact]
    public void TelegraphShapeQuantizesToSixteenths()
    {
        var original = new TelegraphMessage(
            1UL,
            new TelegraphShape(TelegraphShapeKind.Circle, new WorldVector(10.04d, -10.04d), 2.04d),
            StartTick: 5,
            ResolveTick: 35);

        var decoded = Assert.IsType<TelegraphMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(10.0625d, decoded.Shape.Origin.X, 6);   // round(10.04·16 = 160.64) = 161 → 10.0625
        Assert.Equal(-10.0625d, decoded.Shape.Origin.Y, 6);  // symmetric away-from-zero
        Assert.Equal(2.0625d, decoded.Shape.Radius, 6);      // round(2.04·16 = 32.64) = 33 → 2.0625
    }

    // ECOLOGY E4 (v45): a region hosting TWO monster types (mirrors The Verge) round-trips its id, display name,
    // inclusive tile rect, and every per-type {typeId, state} entry in order.
    [Fact]
    public void RegionEcologyMessageRoundTrips()
    {
        var original = new RegionEcologyMessage(
            "the_verge",
            "The Verge",
            100,
            300,
            300,
            370,
            new[]
            {
                new RegionEcologyTypeEntry("slime", EcologyPopulationState.Depleted),
                new RegionEcologyTypeEntry("gnoll", EcologyPopulationState.Overgrown),
            });

        var decoded = Assert.IsType<RegionEcologyMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal("the_verge", decoded.RegionId);
        Assert.Equal("The Verge", decoded.DisplayName);
        Assert.Equal(100, decoded.MinTileX);
        Assert.Equal(300, decoded.MinTileY);
        Assert.Equal(300, decoded.MaxTileX);
        Assert.Equal(370, decoded.MaxTileY);
        Assert.Equal(2, decoded.Types.Count);
        Assert.Equal("slime", decoded.Types[0].TypeId);
        Assert.Equal(EcologyPopulationState.Depleted, decoded.Types[0].State);
        Assert.Equal("gnoll", decoded.Types[1].TypeId);
        Assert.Equal(EcologyPopulationState.Overgrown, decoded.Types[1].State);
        Assert.Equal(MessageType.RegionEcology, decoded.Type);
    }

    // ECOLOGY E4: a region with no hosted types (never authored in practice, but the codec must not crash) ships
    // an empty count-prefixed list, not a null reference.
    [Fact]
    public void RegionEcologyMessageRoundTripsWithNoTypes()
    {
        var original = new RegionEcologyMessage("empty_region", "Empty Region", 0, 0, 10, 10, Array.Empty<RegionEcologyTypeEntry>());

        var decoded = Assert.IsType<RegionEcologyMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Empty(decoded.Types);
    }

    // ECOLOGY E4: a malformed/hostile packet with an out-of-range population-state byte is rejected on decode, so
    // the client's minimap overlay never sees an unknown state (mirrors AttackMessageRejectsOutOfRangeKind).
    [Fact]
    public void RegionEcologyMessageRejectsOutOfRangeState()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(ProtocolCodec.Magic);
        writer.Write(ProtocolCodec.Version);
        writer.Write((ushort)MessageType.RegionEcology);
        WriteTestString(writer, "region");
        WriteTestString(writer, "Region");
        writer.Write((ushort)0);  // minX
        writer.Write((ushort)0);  // minY
        writer.Write((ushort)10); // maxX
        writer.Write((ushort)10); // maxY
        writer.Write((ushort)1);  // one type entry
        WriteTestString(writer, "slime");
        writer.Write((byte)200);  // out-of-range EcologyPopulationState (valid range is 0..4)
        writer.Flush();

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(stream.ToArray()));
    }

    // ECOLOGY E4: the codec bounds the per-region type list against a malformed/hostile over-count so a corrupt
    // packet can't force an unbounded allocation (mirrors the inventory/corpse/monster-tuning list bounds).
    [Fact]
    public void RegionEcologyMessageRejectsOversizedTypeCount()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(ProtocolCodec.Magic);
        writer.Write(ProtocolCodec.Version);
        writer.Write((ushort)MessageType.RegionEcology);
        WriteTestString(writer, "region");
        WriteTestString(writer, "Region");
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)10);
        writer.Write((ushort)10);
        writer.Write((ushort)5000); // over MaxEcologyTypesPerRegion (64) — rejected before any entry is read
        writer.Flush();

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(stream.ToArray()));
    }

    private static void WriteTestString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    // PLAYER-COLLISION-TOGGLE (v43): both the admin toggle and the replication message round-trip their single bool
    // (both true and false, so a dropped/flipped bit is caught).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlayerCollisionMessagesRoundTrip(bool enabled)
    {
        var admin = new AdminSetPlayerCollisionMessage(enabled);
        var decodedAdmin = Assert.IsType<AdminSetPlayerCollisionMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(admin)));
        Assert.Equal(enabled, decodedAdmin.Enabled);
        Assert.Equal(MessageType.AdminSetPlayerCollision, decodedAdmin.Type);

        var setting = new PlayerCollisionSettingMessage(enabled);
        var decodedSetting = Assert.IsType<PlayerCollisionSettingMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(setting)));
        Assert.Equal(enabled, decodedSetting.Enabled);
        Assert.Equal(MessageType.PlayerCollisionSetting, decodedSetting.Type);
    }

    // MONSTER-TUNING-SAVE (v42): the parameterless Save command round-trips through the codec (header-only, no payload).
    [Fact]
    public void SaveMonsterTuningRoundTrips()
    {
        var original = new SaveMonsterTuningMessage();
        var decoded = Assert.IsType<SaveMonsterTuningMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));
        Assert.Equal(original, decoded);
        Assert.Equal(MessageType.SaveMonsterTuning, decoded.Type);
    }

    // LOOT P4c: the corpse loot-window verb round-trips (corpse net id + kind + the template key for TakeItem).
    [Fact]
    public void LootActionRoundTrips()
    {
        var take = new LootActionMessage(4242u, LootActionKind.TakeItem, "slime_gel");
        var decodedTake = Assert.IsType<LootActionMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(take)));
        Assert.Equal(take, decodedTake);

        var all = new LootActionMessage(7u, LootActionKind.LootAll, string.Empty);
        Assert.Equal(all, ProtocolCodec.Decode(ProtocolCodec.Encode(all)));

        var close = new LootActionMessage(7u, LootActionKind.Close, string.Empty);
        Assert.Equal(close, ProtocolCodec.Decode(ProtocolCodec.Encode(close)));
    }

    // LOOT P4c: an OPEN corpse's contents round-trip — the corpse net id, the Open flag, and each item's template key,
    // quantity, and rarity tier (the wire data the rarity-coloured loot window renders).
    [Fact]
    public void CorpseContentsRoundTrips()
    {
        var original = new CorpseContentsMessage(987u, true, new[]
        {
            new CorpseItem("slime_gel", 3, Rarity.Common),
            new CorpseItem("arcane_dust", 1, Rarity.Rare),
            new CorpseItem("slime_core", 2, Rarity.Legendary),
        });

        var decoded = Assert.IsType<CorpseContentsMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(987u, decoded.CorpseNetworkId);
        Assert.True(decoded.Open);
        Assert.Equal(3, decoded.Items.Count);
        Assert.Equal(new CorpseItem("slime_gel", 3, Rarity.Common), decoded.Items[0]);
        Assert.Equal(new CorpseItem("arcane_dust", 1, Rarity.Rare), decoded.Items[1]);
        Assert.Equal(new CorpseItem("slime_core", 2, Rarity.Legendary), decoded.Items[2]);
    }

    // LOOT P4c: a CLOSE (Open=false, empty items) round-trips so the client reliably hides the window.
    [Fact]
    public void CorpseContentsCloseRoundTrips()
    {
        var original = new CorpseContentsMessage(987u, false, System.Array.Empty<CorpseItem>());

        var decoded = Assert.IsType<CorpseContentsMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(987u, decoded.CorpseNetworkId);
        Assert.False(decoded.Open);
        Assert.Empty(decoded.Items);
    }

    [Fact]
    public void DamageEventRoundTrips()
    {
        // COMBAT-QOL: the cosmetic damage event round-trips the victim's NetworkId, the damage amount, and the new HP.
        var original = new DamageEventMessage(4242u, 20, 80);

        var decoded = Assert.IsType<DamageEventMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4242u, decoded.NetworkId);
        Assert.Equal(20, decoded.Amount);
        Assert.Equal((ushort)80, decoded.Health);
    }

    [Fact]
    public void AttackMessageRoundTrips()
    {
        // FREEAIM: the attack request round-trips its own sequence + the attack kind + the quantized aim angle.
        // SWING-COMMIT-FIX: it also round-trips the authored tick (the server roots the swing at this tick).
        var original = new AttackMessage(4242, AttackKind.MeleeCone, 12345, 987654);

        var decoded = Assert.IsType<AttackMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(4242u, decoded.Sequence);
        Assert.Equal(AttackKind.MeleeCone, decoded.Kind);
        Assert.Equal((ushort)12345, decoded.AimAngle);
        Assert.Equal(987654u, decoded.AuthoredTick);
    }

    [Theory]
    // FREEAIM: the aim quantization is lossy (2π/65536 ≈ 0.0055°), so a decoded angle must land within one
    // quantization step (~0.0001 rad) of the original. Cover the cardinal bearings + the 0/2π seam.
    [InlineData(0.0)]
    [InlineData(1.5707963)]   // +π/2 (south)
    [InlineData(3.1415926)]   // π (west)
    [InlineData(4.7123889)]   // 3π/2 (north)
    [InlineData(6.2831)]      // ~2π, just below the seam
    [InlineData(-0.7853981)]  // negative input normalizes into [0,2π)
    public void AimAngleQuantizationRoundTripsWithinTolerance(double radians)
    {
        var quantized = AimAngle.Quantize(radians);
        var decoded = AimAngle.ToRadians(quantized);

        // Compare on the circle: the smallest signed difference reduced to (-π, π], so the seam wrap is handled.
        var twoPi = 2.0 * System.Math.PI;
        var normalizedInput = ((radians % twoPi) + twoPi) % twoPi;
        var delta = decoded - normalizedInput;
        if (delta > System.Math.PI) delta -= twoPi;
        if (delta <= -System.Math.PI) delta += twoPi;

        // One quantization step is 2π/65536 ≈ 9.6e-5 rad; allow a touch over a full step for the rounding boundary.
        Assert.True(System.Math.Abs(delta) <= twoPi / 65536.0 + 1e-6, $"delta {delta} too large for {radians}");
    }

    [Fact]
    public void AttackMessageRejectsOutOfRangeKind()
    {
        // FREEAIM: a malformed/hostile packet with an unknown attack kind byte is rejected on decode, so the server
        // handler never sees an out-of-range kind. Hand-encode a valid header + seq + a bogus kind byte + aim.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(ProtocolCodec.Magic);
        writer.Write(ProtocolCodec.Version);
        writer.Write((ushort)MessageType.Attack);
        writer.Write(7u);            // sequence
        writer.Write((byte)200);     // out-of-range AttackKind (rejected before the aim is read)
        writer.Write((ushort)0);     // aim angle
        writer.Flush();

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(stream.ToArray()));
    }

    [Fact]
    public void PlayerStatsRoundTrips()
    {
        var original = new PlayerStatsMessage(new CharacterStats(73, 100, 41, 120, 5, 80));

        var decoded = Assert.IsType<PlayerStatsMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(new CharacterStats(73, 100, 41, 120, 5, 80), decoded.Stats);
    }

    [Fact]
    public void CombatTuningRoundTrips()
    {
        // COMBAT-TUNING (v31): the five combat feel-knobs replicate intact, including the fractional geometry
        // (half-angle deg, radius units) the panel can nudge.
        var original = new CombatTuningMessage(new CombatTuningSnapshot(
            AttackCooldownMs: 750,
            RootMs: 180,
            HalfAngleDegrees: 37.5,
            RadiusUnits: 2.25,
            Damage: 33));

        var decoded = Assert.IsType<CombatTuningMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(750, decoded.Tuning.AttackCooldownMs);
        Assert.Equal(180, decoded.Tuning.RootMs);
        Assert.Equal(37.5, decoded.Tuning.HalfAngleDegrees);
        Assert.Equal(2.25, decoded.Tuning.RadiusUnits);
        Assert.Equal(33, decoded.Tuning.Damage);
    }

    [Fact]
    public void AdminSetStatRoundTrips()
    {
        var original = new AdminSetStatMessage((byte)StatKind.Stamina, -17);

        var decoded = Assert.IsType<AdminSetStatMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal((byte)StatKind.Stamina, decoded.Stat);
        Assert.Equal(-17, decoded.Value);
    }

    // CONTINUOUS MIGRATION (v36): the tile-step commit/mode message types (StepCommitRequest/MovementMode/MoveInput/
    // StepCommitBatch) are DELETED — their round-trip tests are removed with them.

    [Fact]
    public void EntitySpawnRoundTrips()
    {
        var characterId = Guid.NewGuid();
        var original = new EntitySpawnMessage(
            99,
            characterId,
            EntityKind.Player,
            "PlayerOne",
            new TileCoord(12, 25),
            Direction8.W,
            StepCooldownMs: 70);

        var decoded = Assert.IsType<EntitySpawnMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(99u, decoded.NetworkId);
        Assert.Equal(characterId, decoded.CharacterId);
        Assert.Equal(EntityKind.Player, decoded.Kind);
        Assert.Equal("PlayerOne", decoded.DisplayName);
        Assert.Equal(new TileCoord(12, 25), decoded.Tile);
        Assert.Equal(Direction8.W, decoded.Facing);
        Assert.Equal((ushort)70, decoded.StepCooldownMs);
        // MONSTER-BEHAVIOR P6 (v41): an omitted tint/scale defaults to white + 1000 (the no-op the client renders unchanged).
        Assert.Equal(0xFFFFFFu, decoded.TintRgb);
        Assert.Equal((ushort)1000, decoded.ScaleMilli);
    }

    // MONSTER-BEHAVIOR P6 (protocol v41): an EntitySpawn carrying a NON-default placeholder per-type visual (a monster's
    // authored tint + scale×1000) round-trips its TintRgb + ScaleMilli identically through encode→decode.
    [Fact]
    public void EntitySpawnRoundTripsPlaceholderVisual()
    {
        var characterId = Guid.NewGuid();
        var original = new EntitySpawnMessage(
            7,
            characterId,
            EntityKind.Monster,
            "Gnoll",
            new TileCoord(4, 9),
            Direction8.S,
            StepCooldownMs: 250,
            TintRgb: 0xB5651Du,
            ScaleMilli: 1400);

        var decoded = Assert.IsType<EntitySpawnMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(0xB5651Du, decoded.TintRgb);
        Assert.Equal((ushort)1400, decoded.ScaleMilli);
        // The pre-existing fields are unaffected by the new tail.
        Assert.Equal(EntityKind.Monster, decoded.Kind);
        Assert.Equal("Gnoll", decoded.DisplayName);
        Assert.Equal((ushort)250, decoded.StepCooldownMs);
    }

    [Fact]
    public void MovementSpeedChangedRoundTrips()
    {
        var original = new MovementSpeedChangedMessage(99, 70);

        var decoded = Assert.IsType<MovementSpeedChangedMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(99u, decoded.NetworkId);
        Assert.Equal((ushort)70, decoded.StepCooldownMs);
    }

    [Fact]
    public void AdminSetTuningRoundTrips()
    {
        var original = new AdminSetTuningMessage("move.stepCooldownMs", 123.5d);

        var decoded = Assert.IsType<AdminSetTuningMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal("move.stepCooldownMs", decoded.Key);
        Assert.Equal(123.5d, decoded.Value);
    }

    [Fact]
    public void EntityDespawnRoundTrips()
    {
        var original = new EntityDespawnMessage(123, 99);

        var decoded = Assert.IsType<EntityDespawnMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(123u, decoded.ServerTick);
        Assert.Equal(99u, decoded.NetworkId);
    }

    [Fact]
    public void DirectServerEncodersMatchProtocolDecoder()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        ProtocolCodec.EncodeWorldSnapshot(
            writer,
            42,
            77,
            555,
            909, // CONTINUOUS MIGRATION (v36): lastInputSeq header arg
            120,
            false,
            2,
            6,
            [new EntityStateSnapshot(99, WorldVector.FromTile(12, -25), Direction8.NE)]);
        writer.Flush();
        var snapshot = Assert.IsType<WorldSnapshotMessage>(ProtocolCodec.Decode(stream.ToArray()));
        Assert.Equal(42u, snapshot.ServerTick);
        Assert.Equal(77u, snapshot.SnapshotSequence);
        Assert.Equal(555u, snapshot.RecipientStepSeq);
        Assert.Equal(909u, snapshot.LastInputSeq);
        Assert.Equal(120, snapshot.TotalEntities);
        Assert.False(snapshot.IsComplete);
        Assert.Equal(2, snapshot.ChunkIndex);
        Assert.Equal(6, snapshot.ChunkCount);

        stream.Position = 0;
        stream.SetLength(0);
        ProtocolCodec.EncodeEntityDespawn(writer, 123, 99);
        writer.Flush();
        var despawn = Assert.IsType<EntityDespawnMessage>(ProtocolCodec.Decode(stream.ToArray()));
        Assert.Equal(123u, despawn.ServerTick);
        Assert.Equal(99u, despawn.NetworkId);

        var characterId = Guid.NewGuid();
        stream.Position = 0;
        stream.SetLength(0);
        ProtocolCodec.EncodeEntitySpawn(
            writer,
            7,
            characterId,
            EntityKind.Player,
            "Direct",
            new TileCoord(3, 4),
            Direction8.SW,
            stepCooldownMs: 140);
        writer.Flush();
        var spawn = Assert.IsType<EntitySpawnMessage>(ProtocolCodec.Decode(stream.ToArray()));
        Assert.Equal(7u, spawn.NetworkId);
        Assert.Equal(characterId, spawn.CharacterId);
        Assert.Equal("Direct", spawn.DisplayName);
        Assert.Equal(new TileCoord(3, 4), spawn.Tile);
        Assert.Equal(Direction8.SW, spawn.Facing);
        Assert.Equal((ushort)140, spawn.StepCooldownMs);
    }

    [Fact]
    public void ZoneInfoRoundTrips()
    {
        var original = new ZoneInfoMessage(
            "sandbox",
            16,
            12,
            Seed: 1234,
            GenVersion: 1,
            ContentHash: 0xDEADBEEFCAFEF00DUL);

        var decoded = Assert.IsType<ZoneInfoMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal("sandbox", decoded.ZoneId);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(12, decoded.Height);
        Assert.Equal(1234, decoded.Seed);
        Assert.Equal(1, decoded.GenVersion);
        Assert.Equal(0xDEADBEEFCAFEF00DUL, decoded.ContentHash);
    }

    [Fact]
    public void LoginResultRoundTripsRole()
    {
        var characterId = Guid.NewGuid();
        var original = new LoginResultMessage(
            true,
            characterId,
            "Admin",
            ClientRole.Admin,
            new TileCoord(3, 4),
            "");

        var decoded = Assert.IsType<LoginResultMessage>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(characterId, decoded.CharacterId);
        Assert.Equal("Admin", decoded.DisplayName);
        Assert.Equal(ClientRole.Admin, decoded.Role);
        Assert.Equal(new TileCoord(3, 4), decoded.Tile);
    }

    [Fact]
    public void InvalidMagicThrows()
    {
        var packet = ProtocolCodec.Encode(new ClientHelloMessage("client"));
        packet[0] = 0;

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(packet));
    }

    [Fact]
    public void EncodeCanReuseWriterBuffer()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        ProtocolCodec.Encode(new ChatSendMessage("first payload"), writer);
        writer.Flush();

        stream.Position = 0;
        stream.SetLength(0);
        ProtocolCodec.Encode(new MoveIntentMessage(42, -0.7071f, -0.7071f, 0.05f), writer);
        writer.Flush();

        var packet = stream.ToArray();
        var decoded = Assert.IsType<MoveIntentMessage>(ProtocolCodec.Decode(packet));
        Assert.Equal(42u, decoded.InputSeq);
        Assert.Equal(-0.7071f, decoded.DirX);
        Assert.Equal(-0.7071f, decoded.DirY);
        Assert.Equal(0.05f, decoded.DtSeconds);
    }
}
