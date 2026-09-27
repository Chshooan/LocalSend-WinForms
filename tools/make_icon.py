#!/usr/bin/env python3
"""
Generate localsend-win.ico from the LocalSend paper-plane brand.

This reproduces the supplied localsend-win.svg look: a teal gradient disc
with a white paper-plane ("send") glyph and a faint dashed orbit ring.
Rendered at 4x super-sample then downscaled for clean edges, and emitted as
a multi-resolution .ico (256/128/64/48/32/24/16) suitable for WinForms
Form.Icon, NotifyIcon and the compiled EXE.
"""
from PIL import Image, ImageDraw

S = 1024
cx = cy = S // 2
R = int(S * 0.46)

# ---- teal gradient background -------------------------------------------
c1 = (0x00, 0xBD, 0xBD)  # top-left  #00bdbd
c2 = (0x00, 0x5B, 0x5B)  # bottom    #005b5b
base = Image.new("RGBA", (S, S), (0, 0,0,0))
bd = ImageDraw.Draw(base)
for y in range(S):
    t = y / S
    r = int(c1[0] + (c2[0] - c1[0]) * t)
    g = int(c1[1] + (c2[1] - c1[1]) * t)
    b = int(c1[2] + (c2[2] - c1[2]) * t)
    bd.line([(0, y), (S, y)], fill=(r, g, b, 255))

# circular clip (antialiased by supersampling + later downscale)
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).ellipse([cx - R, cy - R, cx + R, cy + R], fill=255)
disc = Image.composite(base, Image.new("RGBA", (S, S), (0,0,0,0)), mask)

d = ImageDraw.Draw(disc)

# faint orbit ring near the edge
ring_w = max(2, int(S * 0.010))
d.ellipse([cx - R + ring_w*3, cy - R + ring_w*3,
           cx + R - ring_w*3, cy + R - ring_w*3],
          outline=(255, 255, 255, 55), width=ring_w)

# ---- paper plane (256-space coords scaled by 4) -------------------------
def P(x, y):
    return (x * 4, y * 4)

A = P(212, 52)    # nose (points up-right)
B = P(48, 132)    # left wing tip
C = P(140, 208)   # bottom wing tip
D = P(141, 123)   # concave rear notch / centre fold

# large lit wing + slim shaded wing
d.polygon([A, B, D], fill=(255, 255, 255, 255))
d.polygon([A, D, C], fill=(198, 219, 219, 255))

# crisp outline around whole plane
outline = (0, 70, 70, 200)
ow = max(2, int(S * 0.006))
d.line([A, B], fill=outline, width=ow, joint="curve")
d.line([B, D], fill=outline, width=ow, joint="curve")
d.line([D, C], fill=outline, width=ow, joint="curve")
d.line([C, A], fill=outline, width=ow, joint="curve")
d.line([A, D], fill=outline, width=ow)  # centre fold

# ---- build multi-size ico -----------------------------------------------
out_path = "Resources/localsend-win.ico"
sizes = [256, 128, 64, 48, 32, 24, 16]
frames = []
for sz in sizes:
    frames.append(disc.resize((sz, sz), Image.LANCZOS))
disc.save(out_path, sizes=[(s, s) for s in sizes])
print("wrote", out_path, "sizes", sizes)
