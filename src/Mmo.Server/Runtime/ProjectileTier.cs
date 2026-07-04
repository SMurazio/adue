namespace Mmo.Server.Runtime;

// DUO-SKILLSHOT (exp/duo-abilities): the outcome tier of a fusion skillshot, which drives BOTH the damage/pierce and
// the replicated visual (tint + scale). Solo = an un-fused shot (two solo shots when the paths never crossed). Good =
// a loose-window fusion (moderate bonus). Perfect = a tight-window fusion (bonus damage + pierce). A fused projectile
// is always Good or Perfect; a Solo never carries pierce. Ordered by "power" so it also reads as an intensity ramp.
public enum ProjectileTier : byte
{
    Solo = 0,
    Good = 1,
    Perfect = 2,
}
