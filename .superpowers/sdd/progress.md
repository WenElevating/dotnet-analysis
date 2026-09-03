# Diagnostics refactor execution ledger

Baseline: `dotnet test DotnetAnalysis.sln --no-restore` is blocked by `global.json` requiring SDK 10.0.303; the host currently has 10.0.400, 10.0.100-preview, and 8.0.401. Development verification may invoke the solution from outside the repository root so SDK 10.0.400 is selected, without changing the repository pin.

Task 1: complete (commit 0669e1f; Core diagnostics models/state tests verified)
Task 2: complete (stable contracts, attached session, retryable operation, lifecycle tests, and event publication verified; base commit 9f01e0a plus final validation commit)
Task 3: complete (event self-declared delivery policy, LatestOnly/Ordered behavior, bounded queues, and fault isolation verified; base commit 9f01e0a plus final validation commit)
Task 4: complete (Windows process enumeration, identity/runtime guards, EventPipe managed heap sampling, and private working-set reader verified across net8/net9/net10)
Task 5: complete (allocation sampling, verified FastSerialization .gcdump capture, atomic storage, allocation interval persistence, and cleanup verified)
Task 6: complete (reader registry, import catalog, TraceEvent/EventPipe heap graph analysis, object/reference queries, and retryable analysis verified)
Task 7: complete (composition root registration, Application-only ViewModel boundary, lifecycle event subscriptions, and UI fault isolation verified)
Task 8: complete (three-runtime worker and live capture matrix, prerequisite script, task-manager private-working-set cross-check, full gates, and artifacts verified)

Verification 2026-09-03 (SDK 10.0.400): prerequisite script passes; solution build passes with 0 warnings/0 errors; full solution tests pass with 51 unit tests and 11 Windows integration tests; net8/net9/net10 live EventPipe/.gcdump capture, analysis, reopen, objects, reference paths, and cleanup pass; dotnet format --verify-no-changes passes; dependency/layer checks and git diff --check pass. Task-manager cross-check artifact: tests/TestResults/DiagnosticsTaskManagerCrossCheck-20260903/task-manager-cross-check.json, PID 11584, Windows 11 build 26200, 6/6 MB, 6/6 MB, 6/6 MB with 0 MB differences.
