using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// TELEGRAPH T1 (closes todo/N-iframe-gate-choke-point.md): THE single player-damage choke point. Every path that
// damages a PLAYER — the monster melee (GameServer.ApplyMonsterAttack) and the telegraph resolve today; PvP /
// hazards / DoT tomorrow — routes through TryDamagePlayer, so the dodge-roll i-frame gate (and the dead-guard) sit
// on ONE seam a future damage source cannot silently bypass. The Phase-D review flagged exactly this: the i-frame
// check used to live INLINE in ApplyMonsterAttack only, and the tests re-implemented its order in a local lambda —
// deleting the real gate would still have passed. This class IS the real gate, and the tests call it directly (with
// the real ServerActionExecutor behind it), so removing the check here fails them.
//
// A standalone class (not a private GameServer method) for the same reason ServerActionExecutor/MonsterSeparation
// are: the session/network tail (damage-event broadcast + the death edge) is INJECTED, so the gate is headlessly
// testable against the REAL executor without a live GameServer, and GameServer stays the single wiring point.
public sealed class PlayerDamageGate
{
    // The executor's i-frame oracle (ServerActionExecutor.HasActiveIFrames) — READ-ONLY, server-side only; the wire
    // carries no i-frame claim a client could fake or extend (design §2.7). Injected so the gate is testable.
    public delegate bool HasActiveIFramesDelegate(ulong entityId, uint serverTick);

    // The landed-damage tail, owned by GameServer: broadcast the DamageEventMessage / HP drop to viewers and handle
    // the death edge (MarkDead + respawn scheduling). Called ONLY when ApplyDamage actually changed stored Health.
    public delegate void DamageLandedDelegate(WorldEntity victim, int amount, string source);

    private readonly HasActiveIFramesDelegate _hasActiveIFrames;
    private readonly DamageLandedDelegate _onDamageLanded;

    public PlayerDamageGate(HasActiveIFramesDelegate hasActiveIFrames, DamageLandedDelegate onDamageLanded)
    {
        _hasActiveIFrames = hasActiveIFrames ?? throw new ArgumentNullException(nameof(hasActiveIFrames));
        _onDamageLanded = onDamageLanded ?? throw new ArgumentNullException(nameof(onDamageLanded));
    }

    // Damage `victim` (a PLAYER) by `amount` at `serverTick`, attributed to `source` (a log/display string like
    // "Monster 3" or "Slime slam"). Returns true iff damage actually LANDED (health changed). Gate order — the exact
    // order ApplyMonsterAttack established, now authoritative here for every caller:
    //   1. players only — this is the PLAYER-damage choke point (monster/dummy damage has its own attack path);
    //   2. dead-guard — a downed player awaiting respawn takes no further hits (no re-death the same window);
    //   3. i-frames — a victim inside its dodge-roll's server-side i-frame window takes NOTHING (design §2.7);
    //   4. ApplyDamage — a hit on an already-0-HP victim is a no-op (no number, no spam); a real change runs the
    //      injected landed tail (broadcast + death edge).
    public bool TryDamagePlayer(WorldEntity victim, int amount, uint serverTick, string source)
    {
        if (victim.Kind != EntityKind.Player)
        {
            return false;
        }

        // LIVING-ENEMIES P3: a DEAD (downed) player takes no further hits while waiting to respawn — guard before
        // applying damage so a hit resolved the same tick as death can't keep hammering the 0-HP body or re-trigger
        // death.
        if (victim.OwnerSession is { IsDead: true })
        {
            return false;
        }

        // MOVEMENT-ACTIONS Phase D (i-frame authority, design §2.7): a victim INSIDE its dodge-roll's i-frame window
        // takes NO damage — decided HERE, server-side only, off the executor's OWN action instance + the def's window
        // (anchored at the SERVER-side start tick). Logged so a live feel-test can count negated hits (both current
        // callers are cooldown/windup-paced, so this cannot spam).
        if (_hasActiveIFrames(victim.Id, serverTick))
        {
            Log.Info($"{source} hit {victim.DisplayName} NEGATED by dodge-roll i-frames.");
            return false;
        }

        // Authoritative damage rides the snapshot HP field (the HUD bar falls). A real change floats a number via
        // the injected landed tail; a hit on an already-0-HP player is a no-op.
        if (victim.ApplyDamage(amount))
        {
            _onDamageLanded(victim, amount, source);
            return true;
        }

        return false;
    }
}
