# N — Node-field review followups (N1 e527444 + N2 4859a3b: APPROVE-WITH-FOLLOWUPS)

Findings 3-4 (client mirror bounds-check; dead Depleted-bit client logic) were folded into the in-flight
N3 task. Remaining here:

1. **Batch cap vs catalogue cap (LOW, latent).** MaxCatalogEntries = 65535 but MaxNodeStateBatchIndices
   = 8192: a future class-table retune past 8192 nodes + mass depletion would make the login batch THROW
   at encode; TrySend swallows it, so the joiner silently renders everything available. Unreachable at
   the shipped ~5,002 entries. Fix when touched: chunk the login batch into multiple messages, or derive
   both caps from one constant with a static assert-style test tying them (batch cap >= catalogue cap).
2. **Validation-order reply delta (LOW, accepted).** Harvest answers "depleted" before "too_far" (old
   entity path checked range first). No information leak (depletion is globally broadcast by design) and
   no exploit — recorded here so the next person diffing reply vocabularies knows it was seen and
   accepted, not missed.
