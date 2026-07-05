# N — Controller support follow-ups (polish, post feel-test)

Source: the controller-support implementation review (exp/duo-abilities). None block the feel-test;
work them only after the user has played with a pad and the mapping survives contact.

- **Second joypad invisible.** Only the first connected pad is read (`GetFirstJoyAxis`). Two pads on
  one machine — the natural way to solo-test the duo abilities locally — silently ignores pad 2.
  Fix: per-player device selection, or at minimum read "any pad" for buttons.
- **Per-frame `Input.GetConnectedJoypads()` allocs.** Called ~8–10×/frame (each axis read re-queries
  to keep hot-plug free). Cache the first device id once per `_Process` (or refresh on Godot's
  `JoyConnectionChanged` signal) — micro-GC, measure-first culture says confirm it even registers
  before bothering.
- **No aim-ownership cue.** `_aimSourceIsController` flips silently (right stick claims, mouse motion
  reclaims); a player alternating devices has no HUD indicator of which owns aim. Small icon or
  crosshair-style change when controller aim is live.
- **Facing-octant flicker at boundaries.** Analog stick heading → `NearestDirection8` facing has no
  hysteresis (mouse `CursorHeading` has boundary hysteresis); dwelling on a 45° boundary can flicker
  the sprite facing. Reuse the mouse hysteresis if it shows up in play.
- **Feel knobs are constants.** `ControllerStickDeadzone` (0.25), `ControllerTriggerThreshold` (0.5),
  `ControllerAimProjectDistance` (8u) are compile-time. If tuning is needed live, expose on the F1
  Movement tab (live-toggle discipline) rather than recompiling per trial.
- **LT + chat edge (accepted design, re-check in play).** A controller-held skillshot aim survives
  chat focus (deliberate: triggers can't type); keyboard Q still cancels-without-firing on chat
  focus. If the asymmetry confuses anyone in practice, revisit.
