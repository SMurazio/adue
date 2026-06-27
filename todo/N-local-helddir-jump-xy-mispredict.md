# N — local held-direction jump mispredicts XY (predictor fights the executor); fold into B2

From the unbiased holistic review of the action feature. A B1 known-gap that B2 (client prediction) resolves.

**Symptom.** If you HOLD a movement direction while jumping, the local avatar's horizontal jitters/rubberbands during the jump. A clean standstill jump (no WASD held) is fine.

**Cause.** `ContinuousPredictor.PredictAndBuffer` integrates every frame's WASD input immediately and has NO concept of an action (`MmoClient.cs:~429`). During a jump the server SUPPRESSES move integration (`HandleMoveIntent` early-returns while `IsActive`, `GameServer.cs:~2862`) but still ACKs those inputs (advances `LastInputSeq`). So the client predicts forward WALK motion while the server arcs the entity via the executor (different path/length/speed); each reconcile snaps the base to the server arc and trims the acked walk inputs → a per-snapshot XY correction smeared by the render-offset decay. So B1 isn't merely "server-confirmed, slightly delayed" — the predictor actively OVER-predicts and corrects for a held-direction jump.

**Fix (B2).** B2's plan already extends the predictor to know about actions (buffer carries action entries; replay calls `Trajectory`). Until then, a cheap B1 mitigation would be: while the local entity is server-reported airborne (`VerticalOffset > 0` or a replicated ActionId), suppress local move prediction so the client follows the server arc via reconcile instead of predicting walk. NOT done now (touches the now-good predictor; B2 owns it). Feed this into the B2 measurement step.
