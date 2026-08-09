---
name: design-decisions-survive-fable-adversarial-review
description: "Consequential design decisions must pass a Fable ADVERSARIAL (red-team) review before being locked into docs/contract — Fable tries to break the decision, not bless it."
metadata:
  node_type: memory
  type: feedback
---

User directive (2026-08-09): **any consequential design decision must survive a Fable adversarial
review before it is adopted / written into a design doc or the contract.** Not a consult that seeks
validation — an ADVERSARIAL pass: the Fable-model agent is tasked to REFUTE the decision — find the
strongest case against it, its unintended consequences, where it fails contact with the kill-test —
and the decision stands only if it survives that or is revised in light of it. Same spirit as the
adversarial-verify pattern (skeptic prompted to break the claim), applied to design.

**Why:** design is the highest-leverage, hardest-to-reverse layer; a decision that only ever heard
agreement hasn't been tested. The tower doctrine (`docs/duo-living-tower.md`) already benefited —
Fable refuted the orchestrator's "reactive tower" thesis and replaced it with the defensible
"authored argument" one. Make that refutation step the rule, not the lucky exception.

**How to apply:**
- Runs on the **Fable model** (per [[session-and-model-economy]]: Fable = design or explicit user
  request — this rule is a standing design trigger). Spawn a Fable agent, give it the decision + the
  canonical docs, and PROMPT IT TO REFUTE (strongest case against, failure modes, cheaper
  alternatives, how it dies at the P2 kill-test) — not "what do you think."
- **Scale to consequence:** directional/architectural/identity calls (what the game IS, the between-
  runs model, whether a system exists) always get the adversarial pass; a small reversible tuning
  tweak does not each need a full consult (same scaling logic as the review-cadence rule,
  [[review-handoff-loop]]).
- The decision + the adversarial verdict + how it was resolved go in the design doc's provenance
  (as `docs/duo-living-tower.md` records its consult).
- 12-Law changes remain a **user** decision on top of this — the adversarial review informs, it does
  not override the user.
