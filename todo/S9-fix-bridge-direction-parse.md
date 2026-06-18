# S9 — Web bridge drops/misroutes "W" and "E" directions (can't move NW/SE)

Severity: should-fix (functional bug — 2 of 8 movement directions broken in the web client).
**User-prioritized — do this next.** Reported as "cannot move NW or SE."

## Root cause

The tile-stepped client sends `Direction8` **compass-name** strings (`N, NE, E, SE, S, SW, W, NW`).
But the bridge's `WebBridgeSession.TryParseDirection` (`src/Mmo.Client.Web/WebBridgeSession.cs:278`)
is a leftover from the old WASD-key protocol and **conflates key letters with compass letters**:

- `"w" or "up" or "n" => Direction8.N` — so world **`"W"` (West) hits the `"w"` arm and becomes
  North.**
- East arms are `"d" or "right" or "east"` — there is **no `"e"` arm**, so world **`"E"` falls to
  default and `TryReadDirection` returns false → the `moveStep` is never enqueued** (dropped).

The on-screen **NW** and **SE** buttons (and the A+W / S+D keyboard combos) map through the
isometric transform to world `W` and `E` respectively, so those are the two directions that appear
broken: NW moves you the wrong way (North), SE does nothing. The other six directions parse via the
correct/explicit arms.

## Fix

The client now sends only `Direction8` names, so parse them directly and unambiguously instead of
the hand-rolled key/word switch:

- Replace the body of `TryParseDirection` with `Enum.TryParse<Direction8>(direction, ignoreCase:
  true, out parsed)` and reject values that aren't defined (`Enum.IsDefined`).
- Remove the legacy `"w"/"a"/"s"/"d"/"up"/"down"/"left"/"right"/"north"/...` arms — they're dead and
  actively harmful (they shadow `"w"` and lack `"e"`). Nothing in the current client sends raw key
  letters anymore.

## Acceptance

- All eight directions move correctly from the web client: in particular world `"W"` → `Direction8.W`
  and `"E"` → `Direction8.E` (not North / not dropped).
- Manual web check: every keyboard combo and every on-screen direction button moves the avatar in a
  distinct, correct direction; NW and SE work.
- `run-checks.cmd` green.

## Note (separate, cosmetic — do not bundle)

The client's button → `screenInputFromDirection` → world-direction indirection is convoluted and the
on-screen button *labels* may not match the world direction they produce under the iso camera (the
web review flagged this). Once the bridge parses compass names correctly, movement is correct; any
remaining label/visual mismatch is a separate cosmetic cleanup, not this bug.
