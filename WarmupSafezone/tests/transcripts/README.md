# WarmupSafezone playtest transcripts

The pre-rework 2026-08-17 transcript was removed because it asserted the retired SCP-914 godmode behavior and is not evidence for version 1.0.0.

The 2026-08-30 summary records a passing isolated headless run on port 8015:

```text
ptest run warmup-safezone-914 standard
```

Result: `passed=1 failed=0 skipped=0`, with no monitor violations. The server log reported plugin version 1.0.0 and the scenario exercised the new damage/exit-protection/geometry assertions.

`20260830-action-coverage.md` records the passing native SCP action scenario and the concrete
negative-action oracle gaps in the shared harness for firearm, grenade/throwable, Micro H.I.D.,
Jailbird, and offensive SCP abilities. The gaps are documented instead of treating an expected
pre-action cancellation as a failed successful-action verb.

The corresponding `20260830-052014-actions-standard.summary.json` records
`passed=1 failed=0 skipped=0`, zero violations, and the real SCP-173 Breakneck transition inside
SCP-914.

`20260926-212523-standard.summary.json` and `20260926-throws-classd-cells-8015.log` record the
isolated headless run on port 8015 after the Class-D cells safezone and narrowed throw policy:
`passed=1 failed=0 skipped=0`. The new `outside-to-classd-cells` damage case was blocked, and a native
weak frag throw from inside SCP-914 completed while the thrower and bystander both stayed at 100/100.
Cancelling the configured SCP-018/SCP-2176/flash throws is not covered because the shared harness
throw verb requires a confirmed throw; it still needs a real client or a harness expect-cancelled verb.
