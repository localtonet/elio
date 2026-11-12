#!/usr/bin/env python3
"""Draws Elio's icon, a prompt and a rising sun on a rounded square, and writes it in the sizes each platform wants.

Run from the repository root:  python build/make-icons.py
It needs nothing beyond Python itself. The same shape is drawn at runtime by src/Elio.Desktop/AppIcon.cs,
so change both together.
"""
import os
import struct
import zlib

BACKGROUND = (0xFF, 0xB0, 0x20)
MARK = (0x0E, 0x11, 0x16)
SAMPLES = 4  # each pixel is the average of this many points a side, which smooths the edges

# The mark on a 64 by 64 grid: a prompt, and beside it the sun coming up over a line.
STROKES = (
    ((17, 22), (27, 32), 2.6), ((27, 32), (17, 42), 2.6),  # the ">"
    ((43, 19), (43, 23), 1.5), ((34.5, 22.5), (37, 25), 1.5), ((51.5, 22.5), (49, 25), 1.5),  # the rays
)
SUN = (43, 37, 9)  # center and radius; only the half above the center is drawn
LINE = (32.5, 40, 53.5, 44)


def near(x, y, start, end, reach):
    """Whether a point is within reach of the line from start to end."""
    (ax, ay), (bx, by) = start, end
    dx, dy = bx - ax, by - ay
    along = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
    return (x - ax - along * dx) ** 2 + (y - ay - along * dy) ** 2 <= reach * reach


def covered(x, y):
    """What is at a point of the 64 by 64 design: None outside the square, else the color there."""
    corner = 14
    cx = min(max(x, corner), 64 - corner)
    cy = min(max(y, corner), 64 - corner)
    if (x - cx) ** 2 + (y - cy) ** 2 > corner ** 2:
        return None

    if any(near(x, y, start, end, reach) for start, end, reach in STROKES):
        return MARK
    if y <= SUN[1] and (x - SUN[0]) ** 2 + (y - SUN[1]) ** 2 <= SUN[2] ** 2:
        return MARK
    if LINE[0] <= x <= LINE[2] and LINE[1] <= y <= LINE[3]:
        return MARK
    return BACKGROUND


def draw(size):
    """The icon as rows of RGBA bytes."""
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            red = green = blue = alpha = 0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    color = covered((px + (sx + 0.5) / SAMPLES) * 64 / size, (py + (sy + 0.5) / SAMPLES) * 64 / size)
                    if color is not None:
                        red += color[0]
                        green += color[1]
                        blue += color[2]
                        alpha += 1
            total = SAMPLES * SAMPLES
            row += bytes((red // alpha, green // alpha, blue // alpha, alpha * 255 // total)) if alpha else bytes(4)
        rows.append(bytes(row))
    return rows


def png(size):
    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    pixels = b"".join(b"\x00" + row for row in draw(size))
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(pixels, 9)) + chunk(b"IEND", b"")


def ico(sizes):
    """A Windows icon file holding one PNG per size."""
    images = [png(size) for size in sizes]
    out = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    for size, image in zip(sizes, images):
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(image), offset)
        offset += len(image)
    return out + b"".join(images)


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as file:
        file.write(data)
    print(f"{path}  {len(data)} bytes")


if __name__ == "__main__":
    write("src/Elio.Desktop/elio.ico", ico([16, 24, 32, 48, 64, 256]))
    for density, size in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)):
        write(f"src/Elio.Android/Resources/mipmap-{density}/icon.png", png(size))
