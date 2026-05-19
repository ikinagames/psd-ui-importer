#!/usr/bin/env python3
"""Extract PSD/PSB layers into PNG files and Unity-friendly JSON metadata."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

from PIL import Image
from psd_tools import PSDImage

SKIP_PREFIXES = ("!ref",)
LAYER_SCALE_RE = re.compile(r"!x([\d]+(?:\.[\d]+)?)$", re.IGNORECASE)


class Options:
    scale: float = 1.0
    max_dim: int = 0
    pot_snap: bool = False
    pot_threshold: float = 1.05
    force_topil: bool = False
    snap_alpha: int = -1
    out_dir: Path = Path("Assets/Art/Extracted")


OPTS = Options()


def smallest_pot_ge(value: int) -> int:
    if value <= 1:
        return 1
    result = 1
    while result < value:
        result <<= 1
    return result


def resize_for_export(img: Image.Image | None) -> Image.Image | None:
    if img is None:
        return img

    width, height = img.size
    if width == 0 or height == 0:
        return img

    if OPTS.scale > 0 and OPTS.scale != 1.0:
        width = max(1, int(round(width * OPTS.scale)))
        height = max(1, int(round(height * OPTS.scale)))

    if OPTS.max_dim > 0:
        longer = max(width, height)
        if longer > OPTS.max_dim:
            ratio = OPTS.max_dim / longer
            width = max(1, int(round(width * ratio)))
            height = max(1, int(round(height * ratio)))

    if OPTS.pot_snap and OPTS.pot_threshold > 1.0:
        pot_width = smallest_pot_ge(width)
        pot_height = smallest_pot_ge(height)
        if pot_width / width <= OPTS.pot_threshold and pot_height / height <= OPTS.pot_threshold:
            width = pot_width
            height = pot_height

    if (width, height) != img.size:
        resample = getattr(Image.Resampling, "NEAREST", Image.NEAREST)
        img = img.resize((width, height), resample)

    return img


def snap_alpha_channel(img: Image.Image | None) -> Image.Image | None:
    if img is None or OPTS.snap_alpha < 0 or img.mode != "RGBA":
        return img

    red, green, blue, alpha = img.split()
    threshold = OPTS.snap_alpha
    alpha = alpha.point(lambda value: 255 if value > threshold else 0)
    return Image.merge("RGBA", (red, green, blue, alpha))


def should_skip(name: str | None) -> bool:
    if not name:
        return False
    lowered = name.lower().lstrip()
    return any(lowered.startswith(prefix) for prefix in SKIP_PREFIXES)


def sanitize(name: str) -> str:
    return "".join(char if char not in r'\/:*?"<>|' else "_" for char in name)


def parse_layer_name(name: str) -> tuple[str, float]:
    match = LAYER_SCALE_RE.search(name)
    if not match:
        return name, 1.0

    try:
        scale = float(match.group(1))
    except ValueError:
        scale = 1.0

    clean = name[: match.start()].rstrip()
    return clean, scale if scale > 0 else 1.0


def apply_layer_scale(img: Image.Image | None, scale: float) -> Image.Image | None:
    if img is None or scale == 1.0:
        return img

    width = max(1, int(round(img.width * scale)))
    height = max(1, int(round(img.height * scale)))
    if (width, height) == img.size:
        return img

    resample = getattr(Image.Resampling, "NEAREST", Image.NEAREST)
    return img.resize((width, height), resample)


def extract_layer_image(layer) -> Image.Image | None:
    img = layer.topil()
    if img is not None:
        return img
    if OPTS.force_topil:
        return None
    return layer.composite()


def walk_layers(layer, parent_path: str = "", parent_visible: bool = True, output_dir: Path | None = None):
    if output_dir is None:
        output_dir = OPTS.out_dir

    result = []
    for child in layer:
        raw_name = child.name or "Layer"
        if should_skip(raw_name):
            print(f"  [skip] {parent_path}/{raw_name}")
            continue

        clean_name, layer_scale = parse_layer_name(raw_name)
        safe_name = sanitize(clean_name)
        relative_path = f"{parent_path}/{safe_name}" if parent_path else safe_name
        visible = parent_visible and child.is_visible()

        info = {
            "name": clean_name,
            "path": relative_path,
            "kind": child.kind,
            "visible": child.is_visible(),
            "effectiveVisible": visible,
            "bbox": {
                "left": child.left,
                "top": child.top,
                "right": child.right,
                "bottom": child.bottom,
            },
            "size": {
                "width": child.width,
                "height": child.height,
            },
        }

        if child.kind == "group":
            info["children"] = walk_layers(child, relative_path, visible, output_dir)
        elif child.width > 0 and child.height > 0:
            png_dir = output_dir / parent_path if parent_path else output_dir
            png_dir.mkdir(parents=True, exist_ok=True)
            png_path = png_dir / f"{safe_name}.png"
            try:
                img = extract_layer_image(child)
                if img is not None:
                    original_size = img.size
                    img = apply_layer_scale(img, layer_scale)
                    img = resize_for_export(img)
                    img = snap_alpha_channel(img)
                    img.save(str(png_path))
                    info["png"] = str(png_path.relative_to(OPTS.out_dir)).replace(os.sep, "/")
                    if img.size != original_size:
                        info["pngSize"] = {"width": img.size[0], "height": img.size[1]}
                        info["originalSize"] = {"width": original_size[0], "height": original_size[1]}
                else:
                    info["png_error"] = "topil() returned None"
            except Exception as exc:  # noqa: BLE001 - surfaced in JSON for artists.
                info["png_error"] = str(exc)

        result.append(info)

    return result


def process_psd(psd_path: Path):
    print(f"\n{'=' * 60}\nProcessing: {psd_path}\n{'=' * 60}")
    psd = PSDImage.open(str(psd_path))
    psd_output_dir = OPTS.out_dir / psd_path.stem
    psd_output_dir.mkdir(parents=True, exist_ok=True)

    meta = {
        "source": psd_path.name,
        "canvas": {"width": psd.width, "height": psd.height},
        "layers": walk_layers(psd, output_dir=psd_output_dir),
    }

    json_path = OPTS.out_dir / f"{psd_path.stem}.json"
    json_path.parent.mkdir(parents=True, exist_ok=True)
    with open(json_path, "w", encoding="utf-8") as handle:
        json.dump(meta, handle, ensure_ascii=False, indent=2)

    print(f"  Canvas: {psd.width}x{psd.height}")
    print(f"  Output: {psd_output_dir}")
    print(f"  Meta: {json_path}")
    return meta


def collect_psd_files(files: list[Path], psd_dir: Path) -> list[Path]:
    if files:
        return [path.resolve() for path in files]

    result = []
    for pattern in ("*.psd", "*.psb"):
        result.extend(psd_dir.glob(pattern))
    return sorted(path.resolve() for path in result)


def write_all_meta():
    all_meta = []
    for json_file in sorted(OPTS.out_dir.glob("*.json")):
        if json_file.name == "_all_meta.json":
            continue
        try:
            with open(json_file, "r", encoding="utf-8") as handle:
                all_meta.append(json.load(handle))
        except Exception as exc:  # noqa: BLE001
            print(f"  Skip {json_file.name}: {exc}")

    all_path = OPTS.out_dir / "_all_meta.json"
    with open(all_path, "w", encoding="utf-8") as handle:
        json.dump(all_meta, handle, ensure_ascii=False, indent=2)
    print(f"\nAll meta saved: {all_path} ({len(all_meta)} PSDs)")


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract PSD/PSB layers into PNG + JSON metadata.")
    parser.add_argument("files", nargs="*", type=Path, help="PSD/PSB files to process. If omitted, --psd-dir is scanned.")
    parser.add_argument("--psd-dir", type=Path, default=Path("Assets/Art/PSD"), help="Folder to scan when files are omitted.")
    parser.add_argument("--out-dir", type=Path, default=Path("Assets/Art/Extracted"), help="Output folder for PNG and JSON files.")
    parser.add_argument("--scale", type=float, default=1.0, help="Scale exported PNGs.")
    parser.add_argument("--max-dim", type=int, default=0, help="Clamp the longest exported PNG side. 0 means unlimited.")
    parser.add_argument("--pot-snap", action="store_true", help="Upscale near-power-of-two textures to POT when within threshold.")
    parser.add_argument("--pot-threshold", type=float, default=1.05, help="Allowed POT upscale ratio.")
    parser.add_argument("--force-topil", action="store_true", help="Skip layers that cannot be exported with topil().")
    parser.add_argument("--snap-alpha", type=int, default=-1, help="Snap alpha channel to 0/255 using this threshold. -1 disables it.")
    args = parser.parse_args()

    OPTS.scale = args.scale
    OPTS.max_dim = args.max_dim
    OPTS.pot_snap = args.pot_snap
    OPTS.pot_threshold = args.pot_threshold
    OPTS.force_topil = args.force_topil
    OPTS.snap_alpha = args.snap_alpha
    OPTS.out_dir = args.out_dir.resolve()
    OPTS.out_dir.mkdir(parents=True, exist_ok=True)

    psd_files = collect_psd_files(args.files, args.psd_dir.resolve())
    if not psd_files:
        print(f"No PSD/PSB files found in {args.psd_dir}")
        return 1

    print(
        "Export options: "
        f"scale={OPTS.scale}, max_dim={OPTS.max_dim or 'unlimited'}, "
        f"pot_snap={OPTS.pot_snap}, pot_threshold={OPTS.pot_threshold}, "
        f"force_topil={OPTS.force_topil}, snap_alpha={OPTS.snap_alpha}"
    )

    for path in psd_files:
        process_psd(path)

    write_all_meta()
    print("Done!")
    return 0


if __name__ == "__main__":
    sys.exit(main())
