# NET5b — headless test that drives the SHIPPED re-send path (not a re-implementation)

PRIORITY N. Regression protection / test-quality gap surfaced by the NET5 independent review (2026-06-21).

## The gap
NET5's headless suite (`tests/Mmo.Client.Core.Tests/TailLossResendHarnessTests.cs`) validates a hand-rolled
`ResendPolicy` that **re-implements** the re-send rule — it never constructs or calls the SHIPPED
`MmoClient.DriveAckDrivenResend` (`grep DriveAckDrivenResend tests/` = 0 hits). The reviewer confirmed the two
are faithful copies (same gates, constants 350/6/1500), so the *logic* is well-tested, but the **production
wiring is not**: the `Poll` ordering, `_resendLastSentAt` reset on fresh emit, `LastReconciledStepSeq` as the
`conf` source, and `ForceResync` actually being the predictor's. Live verification is currently the only thing
covering the shipped path.

## Task
Add a test that drives the REAL path: construct a `MmoClient` (with the existing test transport/fake used by
other MmoClient tests, if any — otherwise the minimal seam), put it in `UoClientDriven` mode, feed it snapshots
whose `RecipientStepSeq` (conf) stalls below the predicted step-seq (a stranded tail), pump `Poll`, and assert:
1. the real `DriveAckDrivenResend` re-ships a `StepCommitBatch` while the ack is overdue (lead>0, conf stalled),
2. once snapshots advance `conf`, `lead` drains and re-sends stop,
3. clean play (conf keeping up) sends ZERO extra batches,
4. a black uplink (conf never advances) trips the bounded `ForceResync` fallback exactly once per ~K/T, not a
   tight loop.
Reuse the redundant-unreliable send capture if the test transport records sent messages.

## Notes
- If constructing `MmoClient` headlessly is too heavy, extract the re-send decision into a tiny pure helper that
  BOTH `DriveAckDrivenResend` and the test call (so there is one implementation, not two) — that also closes the
  gap and is arguably cleaner than a second integration test.
- No behavior change — this is test-only (plus possibly the pure-helper extraction).

## Gates
`run-checks.cmd` green. One discrete commit referencing this task; delete this file on success.

## Acceptance
The shipped re-send/fallback path is exercised by a headless test (or unified behind one shared helper the tests
call), so a future regression in `DriveAckDrivenResend` is caught without a live run.
