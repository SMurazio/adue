# N (feel/visual, HIGH) — remote viewers see a jump's height and XY on different timelines

From the unbiased holistic review of the action feature (Phase A + B1). A LIVE bug no headless test covers (no test drives a remote viewer).

**Symptom.** When you watch ANOTHER player (or, later, a monster) jump, the avatar's vertical leads/stair-steps relative to its horizontal: the apex doesn't sit over the XY arc midpoint and the rise/fall stutters while the horizontal glides.

**Cause.** `EntityVisual.UpdateFrom` (`EntityVisual.cs:~154`) lifts the visual by `state.VerticalOffset`, which is the LATEST snapshot value — un-interpolated, un-buffered, stair-stepping at the snapshot rate. But the remote XY comes from `_remoteInterp.Sample(now)` (`MmoClient.cs:~1558`, `RemotePositionInterpolator.cs:~156`), a ~100 ms-delayed playout buffer that lerps smoothly between confirms. So Z runs ~100 ms ahead of XY and on a coarser timeline. (The cosmetic monster `HopHeight` avoided exactly this by deriving its arc from `_remoteInterp.HopArcFactor` — same bracket/alpha as XY.)

**Fix direction.** Carry `VerticalOffset` as a THIRD lerp channel through the same playout timeline as remote XY: store it in the interpolator's sample buffer alongside X/Y and have `Sample(now)` return it lerped on the same delayed clock. Then remote Z and XY share one timeline. (The LOCAL player is a separate, milder case — its XY is predicted/zero-latency while its Z is snapshot-delayed; B2's Z prediction resolves that. This todo is the REMOTE-viewer fix and is independent of B2.)

**Rigor.** Touches `RemotePositionInterpolator` (recently stabilized for continuous movement) — measure/don't destabilize XY interpolation; add a headless interpolator test that a Z channel lerps on the same timeline as XY; independent review. Best done alongside / before Phase C (the slime becomes a real ballistic jump → monsters get replicated Z too, making this very visible).
