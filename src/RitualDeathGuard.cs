using System;

namespace SharedInventory;

// Owner-issued life identity is invalidated by every ordinary or ritual death,
// even if a game version reuses the character's ZDO when respawning.
internal sealed class RitualDeathGuard
{
    private string character = "", life = "", session = "", attempt = "";
    private double probedAt;
    public string Probe(string characterId, string serverSession, string ritual, double now)
    {
        if (character != characterId || life.Length == 0) { character = characterId; life = Guid.NewGuid().ToString("N"); }
        session = serverSession; attempt = ritual; probedAt = now;
        return life;
    }
    public bool Claim(string characterId, string ownerLife, string serverSession, string ritual, double now, double expires)
    {
        if (life.Length == 0 || life != ownerLife || character != characterId || session != serverSession || attempt != ritual ||
            double.IsNaN(expires) || double.IsInfinity(expires) || expires > probedAt + RitualOutcomes.DeathTimeout + .001 ||
            now > expires || now - probedAt > RitualOutcomes.DeathTimeout || now < probedAt) return false;
        Invalidate(); return true;
    }
    public void Invalidate() { life = ""; }
}
