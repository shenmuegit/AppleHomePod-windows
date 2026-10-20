---
name: AppleHome
description: Apple Devices-inspired clarity for a minimal native Windows setup.
colors:
  setup-paper: "#FFFFFF"
  setup-footer: "#FFFFFF"
  setup-ink: "#1D1D1F"
  setup-muted: "#626266"
  setup-accent: "#1769D2"
  setup-accent-border: "#1260C5"
  setup-accent-text: "#FFFFFF"
  setup-accent-hover: "#135AB6"
  setup-accent-pressed: "#0D4899"
  setup-button-fill: "#F4F5F7"
  setup-button-border: "#E0E3E8"
  setup-button-ink: "#464C55"
  setup-hover: "#ECEEF2"
  setup-hover-text: "#464C55"
  setup-pressed: "#E3E6EB"
  setup-pressed-text: "#1D1D1F"
  setup-replay-fill: "#F4F7FC"
  setup-replay-border: "#E2E8F1"
  setup-replay-ink: "#1769D2"
  setup-replay-hover: "#E7EFFB"
  setup-replay-pressed: "#DEE8F8"
  setup-replay-pressed-text: "#1769D2"
typography:
  device-title:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "21px"
    fontWeight: 600
  device-detail:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "12px"
  task-title:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "18px"
    fontWeight: 600
  listening:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "22px"
    fontWeight: 600
    lineHeight: "30px"
  body:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "14px"
    lineHeight: "21px"
  status:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "13px"
    lineHeight: "20px"
  button:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "13px"
  primary-button:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "14px"
    fontWeight: 500
  quiet-button:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "12px"
rounded:
  button: "8px"
spacing:
  space-4: "4px"
  space-8: "8px"
  gap-10: "10px"
  space-12: "12px"
  space-16: "16px"
  space-20: "20px"
  space-24: "24px"
  space-26: "26px"
components:
  button-primary:
    backgroundColor: "{colors.setup-accent}"
    textColor: "{colors.setup-accent-text}"
    typography: "{typography.primary-button}"
    rounded: "{rounded.button}"
    padding: "8px 14px"
  button-primary-hover:
    backgroundColor: "{colors.setup-accent-hover}"
    textColor: "{colors.setup-accent-text}"
  button-primary-pressed:
    backgroundColor: "{colors.setup-accent-pressed}"
    textColor: "{colors.setup-accent-text}"
  button-secondary:
    backgroundColor: "{colors.setup-button-fill}"
    textColor: "{colors.setup-button-ink}"
    typography: "{typography.button}"
    rounded: "{rounded.button}"
    padding: "8px 14px"
  button-secondary-hover:
    backgroundColor: "{colors.setup-hover}"
    textColor: "{colors.setup-hover-text}"
  button-secondary-pressed:
    backgroundColor: "{colors.setup-pressed}"
    textColor: "{colors.setup-pressed-text}"
  button-replay:
    backgroundColor: "{colors.setup-replay-fill}"
    textColor: "{colors.setup-replay-ink}"
    typography: "{typography.button}"
    rounded: "{rounded.button}"
    padding: "8px 14px"
  button-replay-hover:
    backgroundColor: "{colors.setup-replay-hover}"
    textColor: "{colors.setup-replay-ink}"
  button-replay-pressed:
    backgroundColor: "{colors.setup-replay-pressed}"
    textColor: "{colors.setup-replay-pressed-text}"
  button-about:
    backgroundColor: "transparent"
    textColor: "{colors.setup-muted}"
    typography: "{typography.quiet-button}"
    rounded: "{rounded.button}"
    padding: "0"
---

# Design System: AppleHome

## Overview

**Creative North Star: "Apple Devices for Windows"**

AppleHome's setup carries the user's chosen Apple Devices reference through one white field, centered device identity, precise native type and restrained blue actions. The directly user-approved final concept establishes the direction; the WPF implementation establishes exact values.

NativeSetupWindow.xaml and NativeSetupWindow.cs are the implementation authority. The approved visual authority is artifacts/design-minimal-installer-20261020/minimal-installer-v2-final.png and its editable .xaml. Portable px notation below preserves numeric WPF device-independent pixel (DIP) values; padding uses top/right/bottom/left order. No HTML/CSS implementation or preview is supplied.

**Key Characteristics:**

- One white field with centered device identity and task feedback.
- Native vector HomePod mini artwork with a monochrome speaker fallback.
- A prominent listening question and a quieter model line.
- Distinct replay, secondary and primary actions with visible keyboard focus.
- Native Windows controls and accessible text feedback.

## Colors

The frontmatter records the actual light-palette WPF brushes. The artwork's one-off mesh, highlight and body-shading colors belong to that drawing and are not interface palette tokens.

### Primary

The accent family supplies primary fill, edge, light text, hover, pressed and focus treatment. Replay has a separate pale blue fill, edge, ink and interaction family.

### Neutral

Paper and footer form a continuous white field. Ink carries essential text and the fallback speaker; muted carries detail, ordinary instructions, status and About. Secondary actions use the button fill, border and ink resources with paired hover/pressed foregrounds.

**The System Contrast Rule.** When high contrast is active at window creation, apply the implemented Windows SystemColors mappings and use the monochrome speaker fallback. The system-colors capture exercises explicit mapping with current colors; it does not verify an OS contrast-mode transition.

## Typography

Segoe UI with Microsoft YaHei UI fallback is inherited throughout. Device and task titles and the testing/listening message use Semibold; primary actions use Medium; remaining text inherits the native regular treatment. The frontmatter records the observed roles rather than a generated scale.

The device title and detail use character ellipsis. Task, instruction and status text wrap. Ordinary steps show task-title above body. Testing/listening hide the separate task title and promote the instruction to listening. Receiver names retain the existing control-character filter and length bound.

Setup text follows the Windows preferred UI language at startup through the existing shared localization resources. English is the source and fallback language; Simplified and Traditional Chinese use their catalogs. The window's accessibility language matches the selected UI culture. Device names are user content and are not translated. Keep the same typography and button roles across locales, allowing longer labels to grow from their minimum width.

## Layout

The native window opens centered at (560 × 464 DIP), with minimum (440 × 380 DIP). A flexible vertically scrollable body sits above an automatically sized pinned footer; horizontal scrolling is disabled. Body margins are left/top/right/bottom (26, 0, 26, 12 DIP). Footer padding is horizontal/vertical (26, 17 DIP).

The centered artwork slot is (120 × 106 DIP), top margin (24 DIP). The device name follows (10 DIP), then the detail line (4 DIP). The task starts below identity (34 DIP). Ordinary instructions follow the task title (10 DIP); testing/listening remove this gap. Replay, preparation, receiver choice and status use top gaps (16 DIP). About is at lower left; decisions are at lower right with a gap (12 DIP).

Below outer height (430 DIP), artwork height becomes (80 DIP), artwork top margin (16 DIP), device-name gap (8 DIP), and task gap (20 DIP). The artwork retains its slot width while the Viewbox scales its content. This is native window adaptation, not a web breakpoint.

## Elevation & Depth

The interface is flat: whitespace, type and thin button edges organize the white field. The approved HomePod artwork contains radial shading, tiled mesh and a small ground ellipse to describe the device. Preserve native title-bar and system window treatment.

**The Quiet Field Rule.** Keep the setup body and footer on one continuous field; separate identity, task and actions with spacing and type.

## Shapes

Buttons use rounded.button, with resting borders (1 DIP); About removes its resting border. Keyboard focus is a separate outer ring (2 DIP stroke, −4 DIP margin, 11 DIP corner radius), without changing layout. Device, fallback speaker and play artwork use native XAML geometry; these controls introduce no shipping raster.

## Components

### Buttons

SetupButton supplies neutral interaction states, minimum dimensions (88 × 38 DIP), reduced disabled opacity (0.45), centered content and the recorded padding. SetupPrimary uses minimum width (112 DIP) with blue treatment and primary-button typography. These dimensions are minimums. The explicit tab order is replay → secondary → primary → About.

The shared non-interactive outer ring appears for keyboard focus. After an operation, focus returns to the initiating button only if visible and enabled, the window is active, and keyboard focus was previously inside the window.

### Prepared replay

SetupReplay uses the replay color family, shared padding, minimum dimensions (148 × 38 DIP) and a filled play triangle (8 × 10 DIP) separated from its label (10 DIP). It is visible in testing/listening. Replay, decisions and receiver controls disable while busy or closing. Replay remains separate from the explicit hearing decision.

### Device identity

The shaded HomePod mini appears only for model AudioAccessory5,1 while the window's high-contrast palette is inactive. Other/unselected models and the high-contrast palette use the monochrome speaker in the same centered slot. Detail identifies HomePod mini for that model, otherwise Windows audio output; the ready state displays the configured output name.

### Task and feedback

The task stack holds instruction, optional native preparation indicator (3 DIP high), receiver choice, replay and supporting status. The native receiver list is bounded (70–110 DIP) with item padding (10 DIP); unavailable or unqualified receivers remain disabled.

The instruction is a polite live region. Its accessible name includes visible status; unchanged announcements are suppressed and LiveRegionChanged is raised when listeners exist.

**The Native Feedback Rule.** Use text, focus and native control states without added transitions. Preparation is indeterminate only when Windows client-area animation is enabled and high contrast is off.

### About

About has minimum dimensions (44 × 32 DIP), a transparent resting fill, no resting border and left-aligned text; shared focus/interaction behavior remains. Its owned native window is centered on setup, fixed width (400 DIP), height sized to content, with body margin (24 DIP). Licensing access reuses the neutral button style.

## Do's and Don'ts

### Do:

- **Do** carry the approved Apple Devices direction through one white field, centered device identity, precise native type and restrained blue actions.
- **Do** reuse the existing WPF brushes and role-specific button styles, including focus and SystemColors mappings.
- **Do** preserve native window controls, wrapped feedback, polite announcements and the authored XAML artwork with its fallback.
- **Do** keep replay separate from explicit hearing confirmation.

### Don't:

- **Don't** restore the superseded silver surround and grouped task card as visual authority.
- **Don't** turn the HomePod drawing's shading into decorative panel elevation.
- **Don't** override Windows contrast colors with fixed light-palette values when high contrast is active.
- **Don't** make a browser implementation authoritative for these native controls.
