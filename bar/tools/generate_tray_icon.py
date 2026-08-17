#!/usr/bin/env python
"""Generate CCCOG-Bar's tray/app icon programmatically — checked in so the
.ico is reproducible from source, never hand-placed binary-only art (task 7,
2026-08-17).

Design (task 10, 2026-08-17, operator direction — temporary branding):
a rounded-square badge, claude-orange background (`#DA7756` — the exact
`ProviderPalette` claude color from `ViewModels.cs`), holding a single bold
white "C" centered on it. The three-dot provider-palette design (see git
history for the previous version of this file) is replaced outright, not
layered alongside it — this is a full swap, not an addition.

The "C" is drawn as a vector arc (a thick ring with a gap), not rendered
from a system font file. Deliberate: a font-based glyph would make this
script's output depend on whichever font happens to be installed on the
machine it runs on (Arial Bold / Segoe UI Bold are common on Windows but not
guaranteed), which would silently break the "reproducible from source"
property this file exists for. An arc has no such dependency — only Pillow's
own drawing primitives.

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

# Same claude color as ViewModels.cs::ProviderPalette.Color/Hex — the badge
# background IS the claude accent color this time (operator direction),
# not a neutral dark shell holding provider-colored accents like the
# previous three-dot design.
CLAUDE_ORANGE = (0xDA, 0x77, 0x56, 255)
LETTER_WHITE = (0xFF, 0xFF, 0xFF, 255)

OUTPUT = pathlib.Path(__file__).resolve().parent.parent / "app" / "CCCOG.Bar.App" / "Assets" / "tray-icon.ico"
ICO_SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (48, 48)]


def build_master() -> Image.Image:
    image = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # Rounded-square badge, small transparent margin so the corner rounding
    # never gets clipped by the canvas edge — corners stay transparent, per
    # the operator's own spec, not squared off.
    margin = 10
    radius = 64
    draw.rounded_rectangle(
        [margin, margin, CANVAS - margin, CANVAS - margin],
        radius=radius,
        fill=CLAUDE_ORANGE,
    )

    # The "C": a thick white arc with a gap on the right, i.e. a circle with
    # roughly its rightmost ~80 degrees left open — reads unambiguously as
    # the letter "C" even fully downsampled to 16px, without needing a font.
    # PIL's arc() sweeps clockwise from `start` to `end` in degrees (0 = 3
    # o'clock); 40->320 draws the long way around and leaves the gap
    # centered on the right edge, which is where a capital C's opening
    # belongs.
    letter_margin = 56
    box = [letter_margin, letter_margin, CANVAS - letter_margin, CANVAS - letter_margin]
    stroke_width = 46
    draw.arc(box, start=40, end=320, fill=LETTER_WHITE, width=stroke_width)

    return image


def main() -> None:
    master = build_master()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    master.save(OUTPUT, format="ICO", sizes=ICO_SIZES)
    print(f"wrote {OUTPUT} ({', '.join(f'{w}x{h}' for w, h in ICO_SIZES)})")


if __name__ == "__main__":
    main()
