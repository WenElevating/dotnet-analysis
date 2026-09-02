# Diagnostics refactor execution ledger

Baseline: `dotnet test DotnetAnalysis.sln --no-restore` is blocked by `global.json` requiring SDK 10.0.303; the host currently has 10.0.400, 10.0.100-preview, and 8.0.401. Development verification may invoke the solution from outside the repository root so SDK 10.0.400 is selected, without changing the repository pin.

Task 1: complete (commit 8cd696c; Core diagnostics models/state tests verified)
Task 2: partial (contracts, attached session, retryable operation, focused tests; event publication and full lifecycle still need hardening)
Task 3: partial (event self-declared delivery policy and LatestOnly/Ordered tests verified)
Task 4: partial (Windows process enumeration, identity/runtime guards, sampler/session scaffolding added; EventPipe managed heap and real capture remain)
Task 5: partial (allocation profile builder and temporary/visible store added; real EventPipe allocation and gcdump collector remain)
Task 6: partial (reader registry, import catalog, gcdump/dmp adapters and analysis service added; TraceEvent heap graph implementation remains)
Task 7: partial (composition root, Desktop reference, shell event subscriptions and boundary tests added; full UI lifecycle wiring remains)
Task 8: partial (three-target worker, integration project, host, and prerequisite script added; SDK 10.0.303/.NET 9 host prerequisite is missing and real capture matrix is not yet complete)

Verification 2026-09-02 (SDK 10.0.400 from C:\): solution build passes; non-integration tests 47/47 pass; prerequisite script reports only missing SDK 10.0.303 and Microsoft.NETCore.App 9.x; integration worker startup/cleanup passes for net8.0 and net10.0, while net9.0 is blocked by the missing runtime. dotnet format remains blocked by global.json SDK resolution.
