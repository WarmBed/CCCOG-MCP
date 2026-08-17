#!/usr/bin/env python
"""Generate CCCOG-Bar's tray/app icon programmatically — checked in so the
.ico is reproducible from source, never hand-placed binary-only art (task 7,
2026-08-17).

Design: a rounded-square badge (dark, matching the flyout's own dark-UI
aesthetic) holding three dots in the app's own provider palette — the EXACT
same colors `ProviderPalette` (bar/app/CCCOG.Bar.App/ViewModels.cs) already
uses for every Flow row's status dot (claude #DA7756, codex #3B82F6, grok
#1F2937), left-to-right in that same order. Reusing that established visual
language (a colored dot = a provider) rather than inventing a new motif is
deliberate: the icon should read as "this is the same app" the moment you
glance at the Flow list.

Run: `python bar/tools/generate_tray_icon.py` (requires Pillow — this
machine has it via the system `python`, not `python3`). Regenerates
bar/app/CCCOG.Bar.App/Assets/tray-icon.ico from scratch every time; nothing
here depends on the previous run's output.
"""

from __future__ import annotations

import pathlib

from PIL import Image, ImageDraw

# Master canvas — drawn once at high resolution, then downsampled per
# target size (LANCZOS) for clean anti-aliasing at every icon size,
# rather than redrawing shapes separately per tiny canvas.
CANVAS = 256

# Same three colors, same left-to-right order, as
# ViewModels.cs::ProviderPalette.Color/Hex.
CLAUDE_ORANGE = (0xDA, 0x77, 0x56, 255)
CODEX_BLUE = (0x3B, 0x82, 0xF6, 255)
GROK_DARK = (0x1F, 0x29, 0x37, 255)

# The badge itself: a dark rounded square, matching the flyout's own dark
# acrylic/dark-UI look (bar/app/CCCOG.Bar.App's flyout background is dark),
# so the icon doesn't clash with the app it represents. Kept a shade
# lighter than the darkest provider dot (grok, #1F2937) on purpose — tried
# an exact near-match first and the grok dot nearly vanished at 16px.
BADGE_FILL = (0x2A, 0x30, 0x3C, 255)
# A soft ring around every dot (not just the dark grok one) so all three
# stay legible against the dark badge at 16px, and so the three read as one
# consistent "beads" family rather than the grok dot looking like a hole.
DOT_RING = (255, 255, 255, 200)

OUTPUT = pathlib.Path(__file__).resolve().parent.parent / "app" / "CCCOG.Bar.App" / "Assets" / "tray-icon.ico"
ICO_SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (48, 48)]


def build_master() -> Image.Image:
    image = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # Rounded-square badge, small transparent margin so the corner rounding
    # never gets clipped by the canvas edge.
    margin = 10
    radius = 64
    draw.rounded_rectangle(
        [margin, margin, CANVAS - margin, CANVAS - margin],
        radius=radius,
        fill=BADGE_FILL,
    )

    # Three dots, evenly spaced in a horizontal row, centered in the badge —
    # the layout that stays legible even once downsampled to 16px (a
    # diagonal or triangular cluster loses separation first). Sized large
    # relative to the badge (bigger than a typical status-dot proportion)
    # specifically because this whole graphic still has to read at 16px.
    dot_diameter = 68
    dot_radius = dot_diameter // 2
    ring_width = 7
    center_y = CANVAS // 2
    spacing = 78
    centers_x = [CANVAS // 2 - spacing, CANVAS // 2, CANVAS // 2 + spacing]
    colors = [CLAUDE_ORANGE, CODEX_BLUE, GROK_DARK]

    for center_x, color in zip(centers_x, colors):
        bbox = [
            center_x - dot_radius,
            center_y - dot_radius,
            center_x + dot_radius,
            center_y + dot_radius,
        ]
        draw.ellipse(bbox, fill=color, outline=DOT_RING, width=ring_width)

    return image


def main() -> None:
    master = build_master()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    master.save(OUTPUT, format="ICO", sizes=ICO_SIZES)
    print(f"wrote {OUTPUT} ({', '.join(f'{w}x{h}' for w, h in ICO_SIZES)})")


if __name__ == "__main__":
    main()
