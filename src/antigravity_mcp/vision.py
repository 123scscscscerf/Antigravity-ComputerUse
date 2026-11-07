"""Computer Vision pipeline for Antigravity Bridge.

Provides:
1. Smart Coordinate Grid Overlay (helps Vision LLMs click pixel-accurately).
2. Set-of-Marks (SoM) UI Element Annotation (draws [1], [2], ... badges over UIA controls).
3. Cursor crosshair marker and image saving/compression.
"""

from __future__ import annotations

import base64
import io
from pathlib import Path
from typing import Any

try:
    from PIL import Image, ImageDraw, ImageFont
    HAS_PIL = True
except ImportError:
    HAS_PIL = False


class VisionProcessor:
    """Processes raw screenshots from C# Core into annotated Vision frames for Antigravity."""

    def __init__(self, captures_dir: Path | None = None) -> None:
        self.captures_dir = captures_dir or (Path(__file__).resolve().parents[2] / "captures")
        self.captures_dir.mkdir(parents=True, exist_ok=True)

    def process_capture(
        self,
        image_base64: str,
        offset_x: int = 0,
        offset_y: int = 0,
        cursor_x: int | None = None,
        cursor_y: int | None = None,
        draw_grid: bool = True,
        grid_step: int = 200,
        ui_elements: list[dict[str, Any]] | None = None,
        use_relative_coords: bool = False,
        save_filename: str | None = None,
    ) -> dict[str, Any]:
        """Applies coordinate grid, cursor marker, and UI element Set-of-Marks badges."""
        raw_bytes = base64.b64decode(image_base64)

        if not HAS_PIL:
            out_path = None
            if save_filename:
                out_path = self.captures_dir / save_filename
                out_path.write_bytes(raw_bytes)
            return {
                "image_base64": image_base64,
                "saved_path": str(out_path) if out_path else None,
                "annotated": False,
                "note": "Pillow not installed; returning raw PNG from C# Core.",
            }

        img = Image.open(io.BytesIO(raw_bytes)).convert("RGBA")
        overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
        draw = ImageDraw.Draw(overlay)
        font = ImageFont.load_default()

        width, height = img.size

        # 1. Draw coordinate grid
        if draw_grid and grid_step >= 50:
            for x in range(grid_step, width, grid_step):
                draw.line([(x, 0), (x, height)], fill=(0, 200, 255, 75), width=1)
                label_x = x if use_relative_coords else (offset_x + x)
                self._draw_badge(draw, x + 3, 4, f"X:{label_x}", (0, 90, 140, 190), font)

            for y in range(grid_step, height, grid_step):
                draw.line([(0, y), (width, y)], fill=(0, 200, 255, 75), width=1)
                label_y = y if use_relative_coords else (offset_y + y)
                self._draw_badge(draw, 4, y + 3, f"Y:{label_y}", (0, 90, 140, 190), font)

        # 2. Draw Set-of-Marks (SoM) UI Element bounding boxes and [ID] tags
        if ui_elements:
            for el in ui_elements:
                el_id = el.get("id")
                rx = int(el.get("rel_x", 0))
                ry = int(el.get("rel_y", 0))
                rw = int(el.get("width", 0))
                rh = int(el.get("height", 0))
                if rw <= 0 or rh <= 0:
                    continue

                # Bounding box around control
                draw.rectangle(
                    [(rx, ry), (rx + rw, ry + rh)],
                    outline=(50, 255, 100, 210),
                    width=2,
                )
                self._draw_badge(
                    draw,
                    max(0, rx),
                    max(0, ry - 14),
                    f"[{el_id}]",
                    (16, 135, 50, 230),
                    font,
                )

        # 3. Draw cursor crosshair if within image bounds
        if cursor_x is not None and cursor_y is not None:
            local_cx = cursor_x - offset_x
            local_cy = cursor_y - offset_y
            if 0 <= local_cx < width and 0 <= local_cy < height:
                r = 10
                draw.ellipse(
                    [(local_cx - r, local_cy - r), (local_cx + r, local_cy + r)],
                    outline=(255, 60, 60, 230),
                    width=2,
                )
                draw.line([(local_cx - 14, local_cy), (local_cx + 14, local_cy)], fill=(255, 60, 60, 230), width=2)
                draw.line([(local_cx, local_cy - 14), (local_cx, local_cy + 14)], fill=(255, 60, 60, 230), width=2)

        combined = Image.alpha_composite(img, overlay).convert("RGB")
        buf = io.BytesIO()
        combined.save(buf, format="PNG", optimize=True)
        final_bytes = buf.getvalue()

        saved_path = None
        if save_filename:
            out_file = self.captures_dir / save_filename
            out_file.write_bytes(final_bytes)
            saved_path = str(out_file)

        return {
            "image_base64": base64.b64encode(final_bytes).decode("ascii"),
            "saved_path": saved_path,
            "annotated": True,
            "width": width,
            "height": height,
        }

    @staticmethod
    def _draw_badge(
        draw: Any,
        x: int,
        y: int,
        text: str,
        bg_color: tuple[int, int, int, int],
        font: Any,
    ) -> None:
        bbox = draw.textbbox((x, y), text, font=font)
        pad = 2
        draw.rectangle(
            [(bbox[0] - pad, bbox[1] - pad), (bbox[2] + pad, bbox[3] + pad)],
            fill=bg_color,
        )
        draw.text((x, y), text, fill=(255, 255, 255, 255), font=font)
