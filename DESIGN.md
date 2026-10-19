---
name: AppleHome
description: Apple Devices-inspired clarity for a minimal native Windows setup.
colors:
  setup-accent: "#1769D2"
  setup-accent-hover: "#135AB6"
  setup-accent-pressed: "#0D4899"
  setup-paper: "#FFFFFF"
  setup-canvas: "#F1F2F4"
  setup-ink: "#1D1D1F"
  setup-muted: "#626266"
  setup-button-fill: "#F7F7F9"
  setup-button-border: "#D0D0D5"
  setup-hover: "#EDEEF2"
  setup-pressed: "#E2E3E8"
typography:
  device-title:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "20px"
    fontWeight: 600
  task-title:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "16px"
    fontWeight: 600
  body:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "14px"
    lineHeight: "21px"
  device-detail:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "13px"
  status:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "13px"
    lineHeight: "20px"
  button:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "14px"
  quiet-button:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "12px"
rounded:
  button: "6px"
  grouped-surface: "12px"
spacing:
  gap-10: "10px"
  space-16: "16px"
  space-18: "18px"
  space-20: "20px"
  space-22: "22px"
  space-26: "26px"
components:
  button-primary:
    backgroundColor: "{colors.setup-accent}"
    textColor: "{colors.setup-paper}"
    typography: "{typography.button}"
    rounded: "{rounded.button}"
    padding: "8px 18px"
  button-primary-hover:
    backgroundColor: "{colors.setup-accent-hover}"
  button-primary-pressed:
    backgroundColor: "{colors.setup-accent-pressed}"
    textColor: "{colors.setup-paper}"
  button-secondary:
    backgroundColor: "{colors.setup-button-fill}"
    textColor: "{colors.setup-ink}"
    typography: "{typography.button}"
    rounded: "{rounded.button}"
    padding: "8px 18px"
  button-secondary-hover:
    backgroundColor: "{colors.setup-hover}"
  button-secondary-pressed:
    backgroundColor: "{colors.setup-pressed}"
    textColor: "{colors.setup-ink}"
  button-replay:
    backgroundColor: "{colors.setup-button-fill}"
    textColor: "{colors.setup-accent}"
    typography: "{typography.button}"
    rounded: "{rounded.button}"
    padding: "8px 18px"
  button-about:
    backgroundColor: "transparent"
    textColor: "{colors.setup-muted}"
    typography: "{typography.quiet-button}"
    rounded: "{rounded.button}"
    padding: "0"
  grouped-surface:
    backgroundColor: "{colors.setup-paper}"
    rounded: "{rounded.grouped-surface}"
    padding: "20px 22px 18px"
---

# Design System: AppleHome

## Overview

**Creative North Star: "Apple Devices for Windows"**

AppleHome's setup follows the user's chosen Apple Devices reference: pale silver surroundings, a white grouped work area, precise type and restrained blue actions. The tone is calm, clear and polished. Device identity leads; the current task and its next action remain easy to scan.

This document records the implemented Windows WPF setup language. Native window controls, keyboard behavior and accessible state feedback belong to that language. `desktop/AirFlash.App/Ui/NativeSetupWindow.xaml` and `NativeSetupWindow.cs` are the implementation authority. Frontmatter uses portable `px` notation for the same numeric WPF device-independent pixel (DIP) values; padding uses top/right/bottom/left order. It does not define a separate CSS implementation.

**Key Characteristics:**

- Quiet neutral layers with one blue action family.
- Clear device identity and compact, readable task hierarchy.
- Soft grouped surfaces with smaller control corners.
- Immediate state feedback through text and native control states.
- Native Windows accessibility and window affordances.

## Colors

The palette combines cool pale neutrals with a clear, restrained action blue. The frontmatter owns the exact values; these roles describe their application.

### Primary

`setup-accent` identifies the principal action, the replay affordance and keyboard focus. Its hover and pressed siblings deepen the primary button while preserving its light label.

### Neutral

`setup-canvas` grounds the header and footer; `setup-paper` separates the grouped task area. `setup-ink` carries essential text and the device outline. `setup-muted` carries device detail, secondary status and About. Neutral buttons use `setup-button-fill` with `setup-button-border`, then `setup-hover` and `setup-pressed` for interaction feedback.

**The System Contrast Rule.** When high contrast is active at window creation, use the implemented Windows SystemColors mappings for surfaces, text, actions and interaction states. Do not hold the light palette above the user's contrast colors.

## Typography

Use the native Segoe UI family with Microsoft YaHei UI fallback throughout. The device title and task title use Semibold; ordinary text and actions retain the inherited regular treatment. The observed hierarchy is role-based, not a generated mathematical scale.

- `device-title` gives the selected device its strongest textual identity.
- `device-detail` supplies the quieter second identity line.
- `task-title` identifies the current setup operation.
- `body` carries the main instruction with its explicit line height.
- `status` carries wrapped supporting or actionable error text.
- `button` and `quiet-button` distinguish ordinary actions from About.

The device title and detail use character ellipsis. Task, instruction and status text wrap. Keep meaningful status readable within the scrollable task area rather than forcing a single line.

## Layout

The native setup window opens centered at (520 × 420 DIP) and allows resizing down to (440 × 380 DIP). Its rows are an automatically sized identity header, a flexible grouped task area and an automatically sized action footer. The task area scrolls vertically when its content needs more room; horizontal scrolling is disabled.

The header uses left/top/right/bottom margins of (26, 22, 26, 18 DIP), a square device icon (36 DIP) and an identity gap (16 DIP). Its second text line sits below the title (6 DIP). The grouped area has side margins (20 DIP) and internal left/top/right/bottom spacing of (22, 20, 22, 18 DIP). The footer uses horizontal/vertical padding of (26, 22 DIP), a flexible spacer and a gap between the two decision buttons (10 DIP).

Within the task group, the instruction follows the title (10 DIP). Replay follows the instruction area (22 DIP); supporting status and preparation progress use top gaps (16 DIP). These are desktop layout constraints, not viewport breakpoints.

## Elevation & Depth

Depth comes from the white work area against the pale silver ground, spacing and thin button borders. The setup adds no shadows, gradients or custom elevation effects. Preserve the native title bar and its system window treatment.

**The Quiet Layers Rule.** Use tonal separation and spacing to organize the setup; keep ornamental depth out of the task area.

## Shapes

The grouped work area uses `rounded.grouped-surface`; buttons use the smaller `rounded.button`. Ordinary button borders are thin at rest (1 DIP) and stronger during keyboard focus (2 DIP). About has no resting border. The device outline and play triangle are native XAML `Path` geometry, with no shipping raster required for these controls.

## Components

### Buttons

Buttons are compact and softly rectangular. Standard actions have minimum dimensions (98 × 36 DIP); the frontmatter records their fill, text, corner and padding assignments. `SetupPrimary` uses the accent family. `SetupButton` supplies neutral hover and pressed states. Disabled controls use reduced opacity (0.45).

Keyboard focus changes the border to the action color and strengthens its thickness. Under high contrast, accent and pressed combinations use Windows highlight colors and their paired text colors. About retains the same shared interaction states with a transparent, borderless resting appearance and no minimum width.

### Prepared replay

Replay uses the neutral button surface and blue label with a native filled play triangle (16 × 16 DIP) separated from its label (10 DIP). Its minimum dimensions are larger (182 × 44 DIP). It remains a listening action; hearing confirmation is a separate decision.

Replay is visible during the testing and listening states. The replay, decision and receiver controls disable while an operation is busy or the window is closing. After an operation, keyboard focus returns to the initiating button only when it is visible, enabled and the window remains active with prior keyboard focus.

### Task group and feedback

The white task group holds a task title, instruction, optional progress, receiver choice, replay and supporting status. The receiver picker uses the native WPF list, a bounded height (70–110 DIP) and item padding (10 DIP); unavailable receivers remain disabled. Failures appear as wrapped text in the same group.

The main instruction is a polite live region. The window combines the instruction with visible status for its accessible name, suppresses unchanged announcements and raises `LiveRegionChanged` for listeners. Preserve readable text feedback alongside native state styling.

**The Native Feedback Rule.** Use the implemented text, focus and control states without added transition effects. Preparation uses the native progress indicator; its indeterminate animation is enabled only when Windows client-area animation is enabled and high contrast is off.

### About

About opens a native owned window, centered on the setup, with a fixed width (400 DIP), height sized to content and body margin (24 DIP). It reuses the setup font, foreground, background and shared neutral button style for licensing access.

## Do's and Don'ts

### Do:

- **Do** carry the chosen Apple Devices reference through neutral layering, precise type and restrained blue actions.
- **Do** reuse the existing WPF brushes and button styles, including their keyboard and high-contrast states.
- **Do** preserve native window controls, readable wrapped feedback and polite state announcements.
- **Do** use native Path geometry for the setup's device and play icons.
- **Do** keep replay and explicit hearing confirmation as distinct controls.

### Don't:

- **Don't** reinstate the rejected bare text-and-buttons arrangement as the visual authority.
- **Don't** add decorative shadows or transition effects to the setup's quiet task area.
- **Don't** replace Windows contrast colors with fixed palette values in high-contrast mode.
- **Don't** create a second browser-style implementation as the authority for these native controls.
