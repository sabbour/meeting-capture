# Offline reliability regressions

Run on Windows from the repository root with the .NET 10 SDK and `Microsoft.AspNetCore.App` .NET 10 shared framework installed. Windows is required for the real DPAPI cache checks:

```powershell
dotnet restore tests\MeetingCapture.Regression\MeetingCapture.Regression.csproj --source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json
dotnet run --project tests\MeetingCapture.Regression\MeetingCapture.Regression.csproj -c Release --no-restore
```

This standalone console project links actual production pipeline, configuration, storage, detection, WorkIQ MCP/auth/cache, response parsing, transcript, speaker resolver, pure panel/viewport state, and pure audio code. It does not reference the WinUI application or use a NuGet test framework. Its dependencies are the same MSAL, Windows protection, and NAudio versions used by the app. The repository clears package sources for this harness; the restore example supplies Microsoft's public mirror explicitly (or use your approved package source). Once dependencies are cached, an offline restore with `--configfile tests\MeetingCapture.Regression\NuGet.Config` works. Build only after the linked production files are ready; compilation errors are integration failures, not skipped coverage.

Each named behavioral check throws on failure; any failure exits nonzero. Controllable tasks drive lifecycle races and a fake clock drives detection boundaries. A 15-second watchdog detects hangs; it is not a performance assertion. Synthetic capture/transcription/intelligence implementations exercise the actual interfaces without devices or services. Real Markdown storage and configuration merging operate only inside a unique `MeetingCapture.Regression-*` temporary directory, cleaned up at the end of each check. No application settings, user secrets, recordings, logs, or transcripts are read.

## Automated coverage

- Start/stop/finalization/retry exclusivity, immediate stopping state, duplicate stop joining, canceled startup, each partial-start failure, and clean retry.
- Final audio drain before transcription flush; late final recognition; stale final/partial/audio callbacks; immutable snapshots and retained notes with 1,201 segments, beyond the 500-row UI bound.
- Transcript-first durable notes before synthesis, typed model failure, stable retry file, canceled retry, disposal waiting for retry, storage failure recovery, failed flush preserving incomplete transcript, contained push exceptions, and failed device/speech cleanup remaining non-startable even when recognition has stopped.
- Disposal after canceled/failed retry without later success; an old stop failure cannot skip waiting for active retry, and new capture/transcription disposal failures still surface after all cleanup attempts.
- Single-flight Exit after recording; recovery from a historical failed save; waiting for blocked transcript persistence; cancellation of external retry and uncooperative synthesis without late note writes; refusing remaining capture ownership; and a cleanup deadline that also covers synchronous disposal invocation. These run against the production pipeline/cleanup helper, not the native tray menu or real Copilot client.
- Previously successful summary, decisions, actions, and all other notes fields survive AI retry failure, cancellation, baseline-save failure, and replacement-save failure on the same path. Mutating an earlier storage argument cannot corrupt the cached baseline.
- Ten synthetic sessions with independent identities, reset counters, detached handlers, and final facade disposal; preview-to-recording handoff with no preview transcription.
- Synthetic JSON credential preservation, unrelated sections, effective higher-precedence overrides, immutable previous settings snapshots, intelligence/synthesis revisions, restart-required sections, and no success event on persistence or post-persistence application failure.
- Actual Markdown replacement, stable identity despite title/date changes, different-session collisions, cancellation, sharing-violation rollback, raw transcript content, and temporary-file cleanup.
- Valid structured empty insights versus absent/malformed output; summary/notes/model-validation parsing; distinct sanitized provider failure mapping.
- Process PID plus start-time identity, pending replacement, confirmation pinning, process-specific dismissal, cooldown boundaries, snooze/resume, enumeration failures, poll cancellation, and manual/detected capture separation.
- No suggestion for an open Teams process without microphone evidence; 20-second debounce, short signal-loss grace, unknown-interval reset, consecutive calls in one process, and confirmed recording surviving mute until explicit completion. Native capture-session enumeration is not executed by the offline suite.
- WorkIQ JSON/SSE transport with synthetic HTTP responses, verified `fetch` arguments, denial/unsupported/malformed/incomplete versus empty outcomes, metadata/time-zone parsing, and a stalled post-header SSE deadline without caller cancellation. These fixtures alone are not live service evidence; the separate 2026-09-23 integrated-app check also confirmed a successful calendar refresh through the deployed `data.value` parser.
- App-owned MSAL cache encryption/restoration/corruption/clear using real Windows DPAPI in temporary files only. Signed-out background calls open no browser. Bounded sign-in tests cover failure, cancellation, timeout, disposal, and rejection/cleanup of late results.
- Shared WorkIQ action state hides Sign in after authentication and exposes cancellation only while pending; duplicate/older auth revisions cannot replace a connected calendar status. Both idle and recording controls consume this state.
- Calendar display verifies the selected inclusive start-time window of minus/plus one hour, older still-active meetings, current-first/chronological focus, clock-boundary movement, timestamp offsets, and cancelled/all-day/invalid exclusions. An 18-event fixture keeps nine focused meetings, eight later meetings including the last one, and one older ended meeting without caps or duplicates. Production-XAML checks keep the focus list directly scrollable and the later/past groups accessible. Refresh-state checks preserve verified connectivity while cached rows are labelled outdated and settle success/failure/cancellation/expiry.
- Recording view-state checks suppress empty pagination and premature AI retries. Tests load the actual production XAML to verify transcript/speakers/fixed Stop and auth cancellation are outside collapsed secondary details. A production-pipeline test emits repeated synthetic partial captions and confirms they never enter finalized word counts or saved transcript content. These are structural/state checks, not live WinUI rendering measurements.
- Typed recording-startup failures: a retained visualization retry, per-source microphone/loopback device faults, speech credential/configuration/unreachable faults, canceled startup, ineligible calendar items, concurrent starts, and retained capture ownership each produce a specific sanitized message (never provider payloads or endpoints), and every failure still rolls back, reports Failed, and permits a clean retry.
- Pure PCM ring wrap/drain, independent snapshots, saturation, silence handling, and rolling RMS.
- Actual audio-health accounting, concurrent byte/buffer/drop increments, normalized PCM levels, immutable snapshots, persistent degraded/failed states, and per-session reset. This tests the tracker, not hardware queue pressure.
- Pure per-panel single-flight guards, session/configuration invalidation, stale revision rejection, independent outcomes, valid empty success, retained last-success timestamps, and bounded 500-row viewport paging with paused scrollback.

## Explicitly not verified here

The harness does not execute WinUI controls/dispatcher callbacks, optional Graph construction, Copilot client startup/SDK cleanup, Graph authorization, or any cloud requests. Pure panel/viewport tests do not establish that the desktop wires those helpers correctly. Linked parser/error checks are not evidence of live provider correctness. Fake service lifecycle checks are not evidence that WASAPI or Azure Speech resources are actually reclaimed.

The following remain separate manual Windows integration/profiling gates:

- One active request per UI feature, independent panel outcomes, configuration-change invalidation, and stale dispatcher results.
- WorkIQ-only startup and signed-out/signing-in/authenticated control visibility in both window states; real model validation and live retry without interrupting recording. Related search and automatic profile/participant lookup are not available.
- Actual microphone/loopback preview, device and speech shutdown, queue pressure/drop counters, speech write failures, and thread/resource quiescence.
- A 60-minute virtualized transcript with reader-position preservation; p95 input latency under 100 ms and no UI stall over 250 ms.
- Stop acknowledgement under 100 ms, actual capture halt within 1 second, audio push p95 under 20 ms, and zero normal-load drops.
- Ten hardware sessions returning memory within 15% of warmed baseline and sustained growth under 50 MB/hour.

These are proposed, machine-dependent targets, not measured results. No crash-recovery retention or restart transcript recovery is introduced by the harness.
