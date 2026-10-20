# Architecture Audit Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Resolve A1–A7 in the reviewed architecture audit while preserving the qualified installed backend and existing deliveries.

**Architecture:** Keep the three .NET projects and the Rust audio sidecar. Separate immutable deliveries from build staging, expose versioned background status, move production setup services out of Verification, and test the production App assembly directly.

**Tech Stack:** WPF/.NET 10, Rust, PowerShell 7, Python 3.12, xUnit and pytest.

**Spec:** `artifacts/architecture-audit-20261020/architecture-review.md`, approved for implementation by the user's “修复他们”.

## Global Constraints

- Work in the visible project checkout `G:\code\applehome`; preserve unrelated changes and the retained worktree.
- All project work data stays inside this project; use `.runtime/architecture-fixes/env.ps1` for local verification.
- Preserve the installed EXE, running backend, device binding, original driver and immutable archives.
- No new runtime dependencies, audio engine, driver installation, short tones, recording or network-disconnection test.
- Daily operation remains Windows output, volume and mute. Software tests must not contact or commandeer the real HomePod.
- The review already supplied the task list and implementation order; the user's request approves that scope. Leave changes reviewable without a commit until the ECC pre-commit gate is explicitly confirmed.

## Review Focus

- Build or packaging failure must leave older deliveries intact; path links and targets outside the project are rejected.
- Source, EXE and qualification metadata must have explicit provenance; no fresh hardware success may be inferred from software tests.
- Status must describe the current receiver and route generation; capture-ready is not transport-ready, and cancellation or a legacy peer must not become success.
- Production service moves retain validation, ownership, calibration and cleanup behavior; existing named-pipe commands remain compatible.
- Tests reference the real App assembly, preserve existing behavior coverage and require no hardware or audible output.

## Task 1 — Delivery safety and reproducible packaging (A1, A2)

Files: `scripts/build.ps1`, a tracked packaging entry under `scripts/`, corresponding Python tests.

- [x] Reproduce deletion/overwrite and packaging failures in disposable project-local fixtures.
- [x] Build in per-run artifacts staging; publish to a new version/commit delivery directory without deleting older outputs.
- [x] Generate source archive, manifest and hashes with explicit source revision, EXE and validation inputs; reject dirty/mismatched provenance and unsafe output paths.
- [x] Keep the original source/vendor notices and distinguish software checks from hardware qualification.
- [x] Verify failed and successful fixture runs preserve historical files; expose the final CLI contract to the documentation task.

## Task 2 — Production and test boundaries (A4, A5)

Files: production setup/calibration/audio classes under `desktop/AirFlash.App`, `AirFlash.Tests.csproj`, App assembly visibility and relevant tests.

- [x] Confirm the existing .NET suite is green, then add an assembly-boundary regression that fails with linked source copies.
- [x] Move runtime setup, calibration and maintenance services out of Verification; keep pure diagnostics there.
- [x] Reference the production App assembly from the Windows test target and remove all linked App Compile items.
- [x] Preserve public behavior, UI flow, qualification and calibration; verify tests exercise production resources and types.

## Task 3 — Background transport status (A3)

Files: a minimal shared status contract, `SingleInstance.cs`, `NativeOutputAgent.cs`, `NativeAutostart.cs`, `NativeSetupPlatform.cs`, and focused tests.

- [x] Pin capture-ready versus current-route transport-ready, stale receiver/generation, malformed or oversized replies, timeout, cancellation and old peers in failing tests.
- [x] Extend the existing current-user named pipe with a bounded versioned status query; retain commands 1/2/3.
- [x] Publish actual current session/controller state and replace the setup's fixed two-second delay with state-based waiting.
- [x] Keep audible confirmation explicit; do not claim status can prove sound from the speaker.

## Task 4 — Directory and documentation contract (A6, A7)

Files: `.gitignore`, `README.md`, `docs/first-run.md`, `UPSTREAM.md`, upstream Chinese guide and `docs/architecture.md`.

- [x] Track the `.worktrees/` ignore rule, preserve all existing work data and mark upstream/legacy documents unmistakably.
- [x] Document current entry points, configuration ownership, data flow and the tracked packaging command.
- [x] Preserve historical qualification evidence and original provenance; avoid bulk moves or renames.

## Task 5 — Integrated verification and review

- [x] Run full .NET solution build/tests, Python suite, relevant lint and local IPC/packaging checks; run Rust tests if its source is affected.
- [x] Obtain independent code and security review for the path and named-pipe changes; fix important findings with regressions.
- [x] Compare installed binary/config/archive hashes and process identities with `before.json` and record remaining hardware limits.
- [x] Deliver the seven-item closure map and reviewable diff; preserve the installed backend and old packages.
