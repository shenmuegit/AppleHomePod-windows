---
version: 1
slug: "desktop-airflash-app-ui-nativesetupwindow-cs"
primary_target: "desktop/AirFlash.App/Ui/NativeSetupWindow.cs"
related_targets: ["desktop/AirFlash.App/Ui/NativeSetupWindow.xaml", "desktop/AirFlash.App/Verification/NativeSetupSmoke.cs"]
---

# Native setup confirmation

Mode: Operate. Platform: Windows native WPF. Scope: setup presentation and mock validation. PRODUCT.md remains product/platform authority.

The user directly approved artifacts/design-minimal-installer-20261020/minimal-installer-v2-final.png and its editable minimal-installer-v2-final.xaml, including refined buttons, then authorized implementation, packaging and opening. That approved concept is visual authority; NativeSetupWindow.xaml and .cs determine exact values and native behavior. The Apple Devices guide image in artifacts/design-reference/apple-devices/overview.png remains a supporting reference. No historical random-roll provenance is asserted.

Preserve automatic driver/output preparation, connection, replay, explicit hearing confirmation, the finite confirmation clip, native daily Windows output/volume controls and the working backend. The reported replay issue is click-to-first-sound. This presentation task generates no sound and replaces no backend.

## Direction contract

THESIS: A refined device-connection task that puts the selected HomePod first and has sound ready before the playback action.

OWN-WORLD: One white field, cool neutral controls, restrained blue, Segoe UI with Microsoft YaHei UI fallback, centered native HomePod mini artwork and real Windows chrome. The artwork's mesh and shading describe the device. Other/unselected models and high contrast use the monochrome speaker. The approved final concept supersedes the earlier silver/grouped layout.

STORY: Identify the selected HomePod, play or replay when ready, then explicitly confirm whether it was heard. Licensing stays in About and actionable failures stay visible. Daily listening uses Windows controls after setup.

FIRST VIEWPORT: Outer (560 × 464 DIP), minimum (440 × 380 DIP). Centered (120 × 106 DIP) artwork, (21 DIP) Semibold device name and (12 DIP) detail. Testing/listening uses a (22 DIP / 30 DIP line-height) message; other steps use an (18 DIP) Semibold task title with (14 DIP / 21 DIP line-height) instruction. Replay minimum (148 × 38 DIP). Pinned footer padding (26 DIP horizontal, 17 DIP vertical), About left, secondary (88 × 38 DIP minimum) and primary (112 × 38 DIP minimum) right, decision gap (12 DIP). Buttons have (8 DIP) corners; focus uses a separate (2 DIP) ring, (−4 DIP) margin and (11 DIP) corners.

FORM: Follow the approved final concept through native XAML drawing and production button resources. Below outer height (430 DIP), artwork height becomes (80 DIP), artwork top margin (16 DIP), device-name gap (8 DIP), and task gap (20 DIP); body scrolls above the footer. Prepared replay shows playing feedback and returns to listening through the existing finite-clip flow. No custom transitions are introduced.

FINISH: Review at artifacts/minimal-ui-implementation-20261020/review/impeccable-finish-review.md found no material UI/source-boundary defect and requested persistence refresh only. DESIGN.md, its native-only schemaVersion 2 sidecar and this brief now record the approved implementation. Artwork is native XAML authored from the approved editable concept; no shipping raster is introduced.

## Evidence and limits

The localized development build reuses Windows preferred UI languages and the shared English/Simplified Chinese/Traditional Chinese catalogs at startup. No language selector or new daily controls are added. Own UI text and About are localized; device names remain untouched. Mock captures under artifacts/setup-localization-20261020 cover all three languages plus system-default startup, with label-width checks and the existing compact/focus/error states. This is presentation verification without hardware or audio activation.

Existing compiled-WPF client captures with a mock platform under artifacts/minimal-ui-implementation-20261020/:

- setup-listening.png — normal hearing-confirmation composition.
- setup-compact-confirmation.png — compact confirmation and pinned actions.
- setup-keyboard-focus.png — distinct focus ring without relayout.
- setup-discovery-unavailable.png — visible failure and supported recovery.
- setup-ready.png — completion feedback.
- setup-system-colors.png — explicit SystemColors mapping and monochrome fallback with current colors.
- setup-long-device-name.png — trimmed identity without displaced controls.

The normal (546 × 427 pixel) and compact (426 × 343 pixel) client images correspond to the stated outer sizes in the review; native whole-window chrome is not captured. The system-colors capture does not establish that Windows contrast mode was switched. No fresh UI or hardware run was performed for documentation.

Supplied logs record 22 passing mock setup checks, 806 passing .NET tests, 125 passing Python tests, passing Ruff, and zero build warnings/errors. The smoke report records audio_sent=false, hardware_used=false and production_state_modified=false. This evidence does not establish current audible playback or fresh-machine installation.

No independent QUALITY BAR card or native Windows reference packet was supplied to the finish review. Fidelity to the directly approved concept and supplied native brief is recorded; no independent ceiling pass is claimed.
