#!/usr/bin/env python3
"""Renders a region of a station map (tiles + coarse entity classes) to PNG, highlighting given tiles."""
import sys, math
sys.path.insert(0, "Tools")
from iss14_place_devices import MapFile, tile_of
from PIL import Image, ImageDraw

def render(mf: MapFile, center, radius, highlights, out, scale=12):
    mf.index()
    cx, cy = center
    w = h = (2 * radius + 1) * scale
    im = Image.new("RGB", (w, h), (10, 10, 20))
    d = ImageDraw.Draw(im)
    for dx in range(-radius, radius + 1):
        for dy in range(-radius, radius + 1):
            t = (cx + dx, cy + dy)
            px = (dx + radius) * scale
            py = (radius - dy) * scale
            if t not in mf.floor:
                continue
            col = (70, 70, 80)
            protos = mf.at.get(t, ())
            if any(p.startswith("Wall") for p in protos):
                col = (160, 160, 170)
            elif any("Window" in p or p.startswith("Grille") for p in protos):
                col = (120, 170, 210)
            elif any("Airlock" in p or "Door" in p or "Firelock" in p for p in protos):
                col = (200, 140, 60)
            elif any(p.startswith("Table") for p in protos):
                col = (140, 100, 60)
            elif any(p.startswith("Vending") for p in protos):
                col = (60, 160, 90)
            elif any(p.startswith("Computer") for p in protos):
                col = (90, 110, 200)
            elif any(p.startswith(("Locker", "Closet", "Crate")) for p in protos):
                col = (120, 90, 140)
            elif any(p.startswith(("Chair", "Stool")) for p in protos):
                col = (110, 110, 70)
            elif mf.blocked(t):
                col = (110, 60, 60)
            d.rectangle([px, py, px + scale - 1, py + scale - 1], fill=col)
            if (cx + dx) % 5 == 0 and (cy + dy) % 5 == 0:
                d.point((px, py), fill=(255, 255, 255))
    for (t, label, colr) in highlights:
        dx, dy = t[0] - cx, t[1] - cy
        if abs(dx) > radius or abs(dy) > radius:
            continue
        px = (dx + radius) * scale
        py = (radius - dy) * scale
        d.rectangle([px, py, px + scale - 1, py + scale - 1], outline=colr, width=2)
        d.text((px + 1, py), label[:2], fill=colr)
    im.save(out)

if __name__ == "__main__":
    path, cx, cy, r, out = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), sys.argv[5]
    hl = []
    for spec in sys.argv[6:]:
        x, y, label = spec.split(":")
        hl.append(((int(x), int(y)), label, (255, 60, 60)))
    render(MapFile(path), (cx, cy), r, hl, out)
