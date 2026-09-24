---
name: NetHog
description: A cross-platform desktop tool for discovering devices on the active Ethernet or Wi-Fi network.
colors:
  primary: "#126B5A"
  accent-soft: "#E7F3EF"
  canvas: "#F4F6F8"
  surface: "#FFFFFF"
  sidebar: "#F8F9FA"
  ink: "#18212B"
  muted: "#626D7A"
  line: "#E2E7EC"
  warning: "#946200"
  warning-soft: "#FFF4D6"
  danger: "#B33D43"
  danger-soft: "#FBECEE"
typography:
  body:
    fontFamily: "Inter, Segoe UI Variable Text, Segoe UI"
    fontSize: "13px"
    fontWeight: 400
  title:
    fontFamily: "Inter, Segoe UI Variable Text, Segoe UI"
    fontSize: "25px"
    fontWeight: 600
  label:
    fontFamily: "Inter, Segoe UI Variable Text, Segoe UI"
    fontSize: "10px"
    fontWeight: 600
rounded:
  sm: "6px"
  md: "7px"
  lg: "8px"
  xl: "9px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "16px"
  lg: "24px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "#FFFFFF"
    rounded: "{rounded.md}"
    padding: "10px 16px"
  button-primary-hover:
    backgroundColor: "#0D584A"
  button-quiet:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "9px 14px"
---

# Design System: NetHog

## Overview

**Creative North Star: “A calm network operations desk”**

NetHog puts the current network and its devices first. The interface uses familiar Windows controls, a scan-friendly device table, and clear status language so operators can see what the app knows and what it can actually do.

The light neutral surfaces keep device information legible during routine use. Teal is reserved for the main action and selected navigation; amber marks limitations and unavailable controls. These choices describe the current implementation and can change with future product direction.

**Key Characteristics:**
- Device list first, with network context alongside it.
- State labels distinguish discovery, session readiness, and active controls.
- One restrained accent; warning color is reserved for limitations.

## Colors

The palette is built from cool neutral surfaces, dark ink, teal actions, and amber warnings.

### Primary
- **Deep Teal** (#126B5A): Main scan action and active navigation.
- **Soft Teal** (#E7F3EF): Selected navigation and quiet emphasis.

### Neutral
- **Cool Canvas** (#F4F6F8): Main application background.
- **White Surface** (#FFFFFF): Table and content surface.
- **Sidebar Fog** (#F8F9FA): Navigation rail.
- **Ink** (#18212B): Primary text.
- **Slate** (#626D7A): Supporting text and metadata.
- **Divider** (#E2E7EC): Panel and row boundaries.

### Status
- **Amber** (#946200) and **Pale Amber** (#FFF4D6): Warnings and unavailable states.
- **Brick** (#B33D43) and **Pale Brick** (#FBECEE): Reserved for blocking or destructive states.

## Typography

**Body Font:** Inter where available, with Segoe UI Variable Text and Segoe UI fallback.

**Character:** Compact and familiar, with enough weight contrast to separate headings, labels, and device data.

### Hierarchy
- **Title** (600, 25px): Current screen title.
- **Body** (400, 13px): Main descriptions and controls.
- **Label** (600, 10–11px): Table headers and section labels.

## Layout

Use a fixed-width navigation rail beside a flexible work area. Keep network context and the primary scan action above the device table. The table should remain the largest region; status messages sit near the work they describe.

## Elevation & Depth

The interface uses tonal layering and thin borders rather than shadows. Surfaces remain flat at rest.

## Shapes

Use restrained 6–9px corners for buttons, notices, and the table surface. Keep row separators and one-pixel borders crisp. Avoid decorative pills for ordinary controls.

## Components

### Buttons
- Primary scan action uses deep teal, white text, and a 7px corner radius.
- Quiet actions use a white surface and a thin neutral border.
- Keyboard focus uses a visible teal outline; disabled controls remain visibly disabled.

### Navigation
- A single selected item uses pale teal fill and a narrow teal state marker.

### Device table
- Device identity, IP, MAC, and current control state use consistent columns.
- Never show traffic as zero when it has not been measured.

### Status notices
- Amber identifies unavailable capabilities and compatibility limits.
- Copy states whether a control is active; unavailable actions are disabled.

## Do's and Don'ts

### Do
- Do keep discovery and enforcement states distinct.
- Do explain why a device or capability may not appear.
- Do preserve comfortable table scanning and keyboard focus.

### Don't
- Don't display a limit, block, or traffic rate as active unless it is measured or enforced.
- Don't use decorative gradients, shadows, or icon fonts.
