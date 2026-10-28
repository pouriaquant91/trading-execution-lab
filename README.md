# Trading Execution Lab - C# order state and reserved exposure

[![C# synthetic invariants](https://github.com/pouriaquant91/trading-execution-lab/actions/workflows/ci.yml/badge.svg)](https://github.com/pouriaquant91/trading-execution-lab/actions/workflows/ci.yml)

Portfolio and bilingual reviewer guide: https://pouriajamali.site/work/engineering-evidence/

New AI-assisted portfolio teaching material, created October 2026. Not employer code, a TT adapter, a reconstruction of a private project, or evidence of historic production incidents/performance. All data is synthetic. No SDK, credentials, account data, market data, network calls or order submission.

## Run

Requires a .NET SDK capable of targeting .NET 8, and the .NET 8 runtime. Tests use xUnit v3 4.0.2 from NuGet.

```sh
dotnet run --project ExecutionLab.Tests/ExecutionLab.Tests.csproj --configuration Release
```

The library has no external package dependency. Only tests restore xUnit. The target framework is a reproducible sample target, not a claim that the author's employer used that version. No real TT/C++ SDK version is inferred.

## Decisions worth inspecting

- Immutable state and typed events keep previous decisions inspectable; this is clarity-first, NOT a low-allocation or low-latency design.
- Reserve before submission: filled position plus outstanding quantity must stay within the configured limit. A cancel request does not release exposure; confirmation releases only the unfilled remainder. A fill during cancellation still counts.
- Envelope deduplication and execution-ID deduplication are distinct. Re-delivered fills with a new envelope consume the normalized sequence but do not double-count quantity. Order IDs are not reused during an in-memory session.
- A sequence gap, unknown fill, unsolicited cancel or arithmetic overflow halts new reservations and requires an authoritative snapshot, followed by explicit resume. A snapshot never automatically resumes trading.
- A valid but risk-denied command consumes its normalized envelope without reserving quantity, so rejection does not itself create a false sequence gap. Invalid envelopes/stale duplicates are not consumed. Halting anomalies require a fresh snapshot baseline.
- Disconnect and stop do not pretend to cancel external orders or flatten a position. Those operations require adapters and confirmed responses, which are intentionally absent.

## Assumptions and exclusions

One instrument, one non-negative direction, integer lots, one serialized normalized input stream. Its sequence is NOT a TT/CME feed sequence; a real adapter must resolve independent channels, timestamps, reset boundaries and authoritative snapshots. Caller supplies trusted normalized facts. Execution-ID payload consistency is an adapter responsibility.

Excluded: shorts/netting, pricing, margin, multi-account aggregation, thread safety, persistence/crash recovery, bounded ID-store retention, durable outbox, broker rejection/replacement races, external cancel/close-out, exchange packet decoding and certification/conformance. Tests do not establish those properties. Stop-on-anomaly is a teaching policy, not a universal trading policy.

No benchmark, p99 latency, "zero incidents", CME conformance or production-readiness claim is made. Independent human review and discussion are welcome; the owner should be able to explain every invariant before using this as interview evidence.

## Publication

Owner approved this new public repository. Release 0.1.0 is source-only. The CI badge links to actual workflow results, not an assertion of production correctness. Local Windows validation ran all 24 xUnit cases successfully on .NET 8.0.33 (built with SDK 9.0.300); the workflow independently runs on Ubuntu and Windows with SDK 8. Existing JavaScript portfolio examples remain separately labelled educational material. A passed suite validates only the fixtures and documented assumptions above.
