"""Builds src/LtcMtc/app.ico (python3 tools/make_icon.py src/LtcMtc/app.ico preview.png): the app's dark tile with a red LTC biphase waveform. Usage: make_icon.py out.ico preview.png"""
import sys
from PIL import Image, ImageDraw

BG, BORDER, RED = (21, 21, 23, 255), (42, 42, 47, 255), (239, 75, 85, 255)

def wave(x0, x1, ymid, amp, halves):
    w = (x1 - x0) / len(halves)
    pts = []
    for i, l in enumerate(halves):
        y = ymid - l * amp
        if pts and pts[-1][1] != y: pts.append((x0 + i * w, pts[-1][1]))
        pts.append((x0 + i * w, y))
    pts.append((x1, pts[-1][1]))
    return pts

def draw(size):
    S = 1024                                  # draw big, downsample: smooth edges at every size
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    small = size <= 32
    pad = 8 if small else 48
    d.rounded_rectangle([pad, pad, S - pad, S - pad], radius=190, fill=BG, outline=None if small else BORDER, width=0 if small else 18)
    # Large: bits 1-0-1 in biphase mark (low-high, low-low, high-low), symmetric. Small: one wide pulse that survives 16 px.
    stroke, margin, amp, halves = (150, 150, 250, [-1, 1, 1, -1]) if small else (70, 190, 190, [-1, 1, -1, -1, 1, -1])
    pts = wave(margin, S - margin, S / 2, amp, halves)
    h = stroke / 2
    for (ax, ay), (bx, by) in zip(pts, pts[1:]):   # square joints: a square wave should look square
        d.rectangle([min(ax, bx) - h, min(ay, by) - h, max(ax, bx) + h, max(ay, by) + h], fill=RED)
    return im.resize((size, size), Image.LANCZOS)

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [draw(s) for s in sizes]
imgs[-1].save(sys.argv[1], format="ICO", sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
sheet = Image.new("RGBA", (sum(sizes) + 20 * len(sizes) + 20, 300), (12, 12, 14, 255))
x = 20
for s, im in zip(sizes, imgs):
    sheet.alpha_composite(im, (x, 150 - s // 2)); x += s + 20
sheet.save(sys.argv[2])
