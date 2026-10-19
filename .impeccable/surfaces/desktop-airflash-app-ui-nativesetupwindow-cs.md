---
version: 1
slug: "desktop-airflash-app-ui-nativesetupwindow-cs"
primary_target: "desktop/AirFlash.App/Ui/NativeSetupWindow.cs"
related_targets: ["desktop/AirFlash.App/Verification/NativeSetupSmoke.cs"]
---

# Native setup confirmation

Mode: Operate. Platform: Windows native WPF. Scope: desktop/AirFlash.App/Ui/NativeSetupWindow.cs and its simulated validation.

The user confirms the performance issue is click-to-first-sound, not clip duration. Keep automatic driver/output preparation, explicit hearing confirmation, manual replay, native daily volume/output controls and the working backend. After the canon choice in decision169273cc, the user explicitly pinned Apple Devices as more elegant and preferred. This later reference overrides the earlier visual world. The code-first task arrangement remains; no generated comp exists or is owed. Official reference: artifacts/design-reference/apple-devices/overview.png, sourced from Apple's Windows user guide.

## Direction contract

THESIS: A refined device-connection task that puts the selected HomePod first and has sound ready before the playback action.

OWN-WORLD: Apple Devices-inspired pale silver #F1F2F4 ground, a single white grouped work area, #1D1D1F text and #1769D2 primary action. Segoe UI with CJK fallback, compact controls, small monochrome speaker icon; no invented navigation for a one-task window.

STORY: Identify the selected HomePod, play or replay immediately when ready, then confirm whether it was heard. Licensing stays in About; actionable failures stay visible.

FIRST VIEWPORT: A520x420 native window. A36px monochrome speaker icon and two-line device identity lead at26px inset. A white grouped work area carries a16px task label, listening question and44px replay action. About and two hearing actions remain in the neutral footer. Preparation and playback update inline without moving controls.

FORM: User-pinned Apple Devices reference supersedes the initial canon choice, seed451fe8d9 retained for traceability. One signature interaction: the prepared replay control gives immediate pressed/playing feedback, then returns to ready after the finite clip; reduced motion uses static states.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
