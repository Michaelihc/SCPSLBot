# Issue 1: 3-X equip disconnect verification

Issue: https://github.com/Michaelihc/scpsl-warmup-sandbox/issues/1

Result: not reproduced in either the current local build or the exact September 8 published DLL. This does not establish the cause or fixed version of the original report, which supplies no game version, plugin inventory, or release identifier. No product code was changed and the GitHub issue was left open.

## Walkthrough

Used SCP:SL 14.2.7, one real offline modded client with Chinese settings, isolated loopback servers, and the cached sanitized OA1 plugin baseline. After plugin readiness, changed the fixture to ClassD, stripped its inventory, granted native item 47 through RA, selected it with the native primary-weapon hotkey, and recorded a 20-second hold. At the end the client process was alive and native RA `god <id>` affected exactly one player. Both recordings visibly show the equip animation and held 3-X without a disconnect screen. Audio was recorded but not separately assessed; this was an equip/connectivity check, not a shooting test.

| Build | Port | Result | Evidence run under `.tests/offline-clients/runtime/host/runs/` |
| --- | --- | --- | --- |
| Local candidate `3e4c356` (product source unchanged from `95d54e1`) | 9162 | PASS | `9dfb34cf928e46a18e3ae6c77ac1fb09` |
| Published `production-2026-09-08` DLL | 9163 | PASS | `dec3e70a25644dc680149378f6b1e7df` |

Candidate DLL SHA-256: `491a25d786e581ef6fc88f5144b28d02c2a5d87acea947c68ec5c73a90325d10`.

Published DLL SHA-256: `8b4163539d8342c4d817ed4af9d835ebe7d0908c338bf2fe01b7183023cd8d26`, matching the GitHub release asset.

Scenario revision: `8910bed5b147639d7b1e99e4b29c33b5f63f1c16`. Harness: `cbacfc4f3d6561d6afb7d85cfa9266016773783f`. Baseline: `63bf1d3e3bf9375d99da9b0e6c6f63433c688a79381ffbbda60d1f3cd9f8cc6d`.

Each evidence directory contains `deployment.json`, `result.json`, `particle-disruptor-before.json`, `particle-disruptor-result.json`, fresh client/server logs, `review.png`, and the `*-equip-particle-disruptor.mp4` recording. Fresh logs were inspected before visual QA. Neither client log contains an equip/disconnect exception. The baseline has unrelated EXILED missing-path startup errors; SCPSLBot loaded and its readiness command succeeded. These runs do not certify every companion plugin or reproduce the reporter's unknown installation.

Totals: 2 passed, 0 failed, 0 skipped walkthroughs. Older v0.1.2 was inspected but not runtime-tested. The SSS grant button and sustained firing/reloading were not exercised. No production deployment was performed.

## Corrected regression check

The previous local scenario used `god <id> enable` as a connectivity check. The fixture already had god mode, so native RA returned zero affected players even while it was connected. The native implementation explicitly skips already-enabled targets in `../.references/Decompiled/DedicatedServer/Assembly-CSharp/CommandSystem/Commands/RemoteAdmin/GodCommand.cs`, `Execute`, Enable case. The corrected check uses toggle, which counts a connected target regardless of its previous flag value. It also changes the fixture from Tutorial to ClassD so warmup participation is exercised.

The earlier run `68f3def98da24702a91b2877adc837a1` remains preserved as evidence of the false failure; its result is not proof of a disconnect.
