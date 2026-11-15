#!/usr/bin/env python3
"""Generate the ClassroomKit desktop icon assets using only the Python standard library.

Outputs (paths relative to the repository root):

    src/ClassroomKit.Agent/Assets/app.ico       ICO container with 7 PNG frames
                                                (16/24/32/48/64/128/256 px)
    src/ClassroomKit.Agent/Assets/app-64.png    64x64 PNG for WPF Image sources

Design: fully transparent canvas, a rounded square inset by 1 px on every side
(corner radius = 0.22 x side) filled with a top-to-bottom #3B82F6 -> #2563EB
gradient, and a white Wi-Fi glyph (a dot plus three concentric upper arcs).
Everything is rasterised from signed distance fields with 4x4 supersampling, so
the output is anti-aliased and byte-for-byte reproducible.

Geometry, in pixel coordinates (y grows downwards, S = frame edge length):

    dot centre    (0.50 S, 0.715 S), radius 0.052 S
    arc centre    same as the dot
    arc radii     0.16 S / 0.27 S / 0.38 S
    stroke width  0.075 S (never thinner than 1.15 px on small frames)
    arc sector    45 deg .. 135 deg measured with the y axis pointing up,
                  i.e. the sector above the dot; both ends get round caps,
                  which fall out of the distance field for free

Usage:

    python3 scripts/make-icon.py            # regenerate both assets, then verify
    python3 scripts/make-icon.py --verify   # verify the existing assets only
"""

from __future__ import annotations

import argparse
import os
import struct
import sys
import zlib

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSET_DIR = os.path.join(REPO_ROOT, "src", "ClassroomKit.Agent", "Assets")
ICO_PATH = os.path.join(ASSET_DIR, "app.ico")
PNG64_PATH = os.path.join(ASSET_DIR, "app-64.png")

ICON_SIZES = (16, 24, 32, 48, 64, 128, 256)
PNG_SIGNATURE = bytes((0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))

GRADIENT_TOP = (0x3B, 0x82, 0xF6)      # #3B82F6
GRADIENT_BOTTOM = (0x25, 0x63, 0xEB)   # #2563EB

SQUARE_INSET = 1.0        # transparent 1 px border around the rounded square
CORNER_RADIUS = 0.22      # radius = 0.22 x side of the square
DOT_CENTRE = (0.50, 0.715)
DOT_RADIUS = 0.052
ARC_RADII = (0.16, 0.27, 0.38)
ARC_WIDTH = 0.075
MIN_STROKE_PX = 1.15      # keeps the 16 px frame readable
SUPERSAMPLE = 4           # 4x4 samples per pixel

SQRT1_2 = 0.7071067811865476


# --------------------------------------------------------------------------- #
# rasteriser
# --------------------------------------------------------------------------- #
def render_frame(size):
    """Render one square frame of edge `size`; returns a list of RGBA rows."""
    inset = SQUARE_INSET
    side = size - 2.0 * inset
    half = side / 2.0
    radius = min(CORNER_RADIUS * side, half)
    inner = half - radius                 # half extent of the corner-arc centres
    centre = size / 2.0

    stroke = ARC_WIDTH * size
    if size <= 32:
        stroke = max(stroke, MIN_STROKE_PX)   # small frames: never below ~1 px
    half_stroke = stroke / 2.0

    dot_x = DOT_CENTRE[0] * size
    dot_y = DOT_CENTRE[1] * size
    dot_r = DOT_RADIUS * size
    arc_r = tuple(r * size for r in ARC_RADII)

    step = 1.0 / SUPERSAMPLE
    samples = SUPERSAMPLE * SUPERSAMPLE
    inv_samples = 1.0 / samples
    top_r, top_g, top_b = GRADIENT_TOP
    bot_r, bot_g, bot_b = GRADIENT_BOTTOM

    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            rect_hits = 0
            glyph_hits = 0
            for sy in range(SUPERSAMPLE):
                y = py + (sy + 0.5) * step
                for sx in range(SUPERSAMPLE):
                    x = px + (sx + 0.5) * step
                    # --- rounded square (signed distance, negative inside) ---
                    qx = abs(x - centre) - inner
                    qy = abs(y - centre) - inner
                    ox = qx if qx > 0.0 else 0.0
                    oy = qy if qy > 0.0 else 0.0
                    rect_d = (ox * ox + oy * oy) ** 0.5
                    if qx > qy:
                        if qx < 0.0:
                            rect_d += qx
                    else:
                        if qy < 0.0:
                            rect_d += qy
                    rect_d -= radius
                    if rect_d > 0.0:
                        continue
                    rect_hits += 1

                    # --- white Wi-Fi glyph ---
                    gx = x - dot_x
                    gy_up = dot_y - y                # y axis pointing up
                    rr = (gx * gx + gy_up * gy_up) ** 0.5
                    best = rr - dot_r
                    if best > 0.0:
                        abs_gx = gx if gx >= 0.0 else -gx
                        upper = gy_up >= abs_gx          # 45 deg .. 135 deg
                        for arc in arc_r:
                            if upper:
                                d = abs(rr - arc) - half_stroke
                            else:
                                ex = arc * SQRT1_2
                                ey = ex
                                d1 = ((gx - ex) ** 2 + (gy_up - ey) ** 2) ** 0.5
                                d2 = ((gx + ex) ** 2 + (gy_up - ey) ** 2) ** 0.5
                                d = (d1 if d1 < d2 else d2) - half_stroke
                            if d < best:
                                best = d
                                if best <= 0.0:
                                    break
                    if best <= 0.0:
                        glyph_hits += 1

            if rect_hits == 0:
                row += bytes((0, 0, 0, 0))
                continue

            # vertical gradient sampled at the pixel centre
            t = (py + 0.5 - inset) / side
            if t < 0.0:
                t = 0.0
            elif t > 1.0:
                t = 1.0
            g_r = top_r + (bot_r - top_r) * t
            g_g = top_g + (bot_g - top_g) * t
            g_b = top_b + (bot_b - top_b) * t

            a_rect = rect_hits * inv_samples
            a_glyph = glyph_hits * inv_samples
            out_a = a_glyph + a_rect * (1.0 - a_glyph)
            if out_a <= 0.0:
                row += bytes((0, 0, 0, 0))
                continue
            keep = a_rect * (1.0 - a_glyph)
            c_r = (255.0 * a_glyph + g_r * keep) / out_a
            c_g = (255.0 * a_glyph + g_g * keep) / out_a
            c_b = (255.0 * a_glyph + g_b * keep) / out_a
            row += bytes((
                int(c_r + 0.5) & 0xFF,
                int(c_g + 0.5) & 0xFF,
                int(c_b + 0.5) & 0xFF,
                int(out_a * 255.0 + 0.5),
            ))
        rows.append(bytes(row))
    return rows


# --------------------------------------------------------------------------- #
# containers
# --------------------------------------------------------------------------- #
def _chunk(tag, data):
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))


def encode_png(size, rows):
    """Encode RGBA rows as a PNG (8-bit, colour type 6, no interlace)."""
    raw = bytearray()
    for row in rows:
        raw.append(0)          # filter type 0 (None)
        raw += row
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (PNG_SIGNATURE
            + _chunk(b"IHDR", header)
            + _chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + _chunk(b"IEND", b""))


def encode_ico(frames):
    """Wrap (size, png_bytes) pairs in an ICO container."""
    count = len(frames)
    offset = 6 + 16 * count
    entries = bytearray()
    blobs = bytearray()
    for size, png in frames:
        dim = 0 if size >= 256 else size     # 0 means 256 in the ICO directory
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32,
                               len(png), offset + len(blobs))
        blobs += png
    return struct.pack("<HHH", 0, 1, count) + bytes(entries) + bytes(blobs)


# --------------------------------------------------------------------------- #
# build
# --------------------------------------------------------------------------- #
def build():
    os.makedirs(ASSET_DIR, exist_ok=True)
    frames = []
    for size in ICON_SIZES:
        png = encode_png(size, render_frame(size))
        frames.append((size, png))
    ico = encode_ico(frames)
    png64 = dict(frames)[64]
    with open(ICO_PATH, "wb") as fh:
        fh.write(ico)
    with open(PNG64_PATH, "wb") as fh:
        fh.write(png64)
    return frames


# --------------------------------------------------------------------------- #
# verification
# --------------------------------------------------------------------------- #
def _png_ihdr(blob):
    if not blob.startswith(PNG_SIGNATURE):
        return None
    if len(blob) < 33 or blob[12:16] != b"IHDR":
        return None
    width, height = struct.unpack(">II", blob[16:24])
    return width, height


def verify():
    ok = True
    notes = []

    def fail(msg):
        nonlocal ok
        ok = False
        notes.append("FAIL " + msg)

    if not os.path.isfile(ICO_PATH):
        print("missing: " + ICO_PATH)
        return False
    with open(ICO_PATH, "rb") as fh:
        ico = fh.read()
    if not os.path.isfile(PNG64_PATH):
        print("missing: " + PNG64_PATH)
        return False
    with open(PNG64_PATH, "rb") as fh:
        png64 = fh.read()

    print("== ICO header ==")
    if len(ico) < 6:
        fail("file shorter than the 6-byte ICO header")
        print("RESULT: FAILED")
        return False
    reserved, kind, count = struct.unpack("<HHH", ico[:6])
    print("reserved=%d  type=%d  count=%d  (expect 0 / 1 / %d)"
          % (reserved, kind, count, len(ICON_SIZES)))
    if (reserved, kind, count) != (0, 1, len(ICON_SIZES)):
        fail("ICO header mismatch")

    print("== directory entries ==")
    frame_bytes = []
    for index in range(count):
        base = 6 + 16 * index
        if base + 16 > len(ico):
            fail("directory entry %d truncated" % index)
            break
        (dim_w, dim_h, colours, entry_reserved, planes, bitcount,
         length, offset) = struct.unpack("<BBBBHHII", ico[base:base + 16])
        expected = ICON_SIZES[index]
        blob = ico[offset:offset + length]
        ihdr = _png_ihdr(blob)
        frame_bytes.append((expected, length))
        status = "OK"
        if (dim_w, dim_h) != (0 if expected >= 256 else expected,) * 2:
            status = "BAD width/height"
        elif planes != 1 or bitcount != 32:
            status = "BAD planes/bitcount"
        elif offset + length > len(ico):
            status = "BAD offset/length"
        elif blob[:8] != PNG_SIGNATURE:
            status = "BAD PNG signature"
        elif ihdr != (expected, expected):
            status = "BAD IHDR"
        if status != "OK":
            fail("entry %d (%d px): %s" % (index, expected, status))
        print("[%d] declared %dx%d (dir bytes %d/%d)  PNG IHDR %sx%s  "
              "%5d bytes @0x%X  planes=%d bpp=%d  %s"
              % (index, dim_w or 256, dim_h or 256, dim_w, dim_h,
                 ihdr[0] if ihdr else -1, ihdr[1] if ihdr else -1,
                 length, offset, planes, bitcount, status))

    print("== app-64.png ==")
    ihdr64 = _png_ihdr(png64)
    if ihdr64 != (64, 64):
        fail("app-64.png is not a 64x64 PNG")
    print("signature=%s  IHDR %sx%s  %d bytes"
          % ("ok" if png64.startswith(PNG_SIGNATURE) else "BAD",
             ihdr64[0] if ihdr64 else -1,
             ihdr64[1] if ihdr64 else -1,
             len(png64)))
    if frame_bytes and dict(frame_bytes).get(64) != len(png64):
        fail("app-64.png differs from the 64 px ICO frame")

    print("== sizes ==")
    for size, length in frame_bytes:
        print("  %3d px frame: %6d bytes" % (size, length))
    print("  ICO total   : %6d bytes" % len(ico))
    print("  app-64.png  : %6d bytes" % len(png64))

    if ok:
        print("RESULT: all checks passed")
    else:
        print("RESULT: FAILED")
        for note in notes:
            print("  " + note)
    return ok


def main(argv=None):
    parser = argparse.ArgumentParser(description="Generate the ClassroomKit icon assets")
    parser.add_argument("--verify", action="store_true",
                        help="only verify the existing assets")
    args = parser.parse_args(argv)

    if not args.verify:
        build()
        print("wrote " + os.path.relpath(ICO_PATH, REPO_ROOT))
        print("wrote " + os.path.relpath(PNG64_PATH, REPO_ROOT))
        print("")
    return 0 if verify() else 1


if __name__ == "__main__":
    sys.exit(main())
