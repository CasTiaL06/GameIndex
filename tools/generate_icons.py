"""Generates the floating desktop icon styles. Each style = a static body ({id}_base.png) + a rotating overlay ({id}_ring.png), 512x512 RGBA.
Everything is procedural (numpy + Pillow, 4x supersampled): glass tiles, rim light, glow, a geometric "G" glyph, comet rings.
Run: python tools/generate_icons.py"""
import math, os
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageChops

OUT = os.path.join(os.path.dirname(__file__), "..", "GameIndex", "Assets", "FloatingIcons")
N, SS = 512, 4
S = N * SS
C = S / 2
CYAN, VIOLET, PINK, GOLD, TEAL, ORANGE, RED, WHITE = (101, 217, 255), (139, 124, 255), (255, 110, 200), (255, 205, 90), (70, 240, 200), (255, 150, 60), (235, 60, 80), (245, 247, 252)
os.makedirs(OUT, exist_ok=True)

# ------------------------------------------------------------ primitives
def blank(): return Image.new("RGBA", (S, S), (0, 0, 0, 0))

def lin_grad(c1, c2, angle=45, stops=None):
    y, x = np.mgrid[0:S, 0:S].astype(np.float32)
    a = math.radians(angle)
    t = ((x - C) * math.cos(a) + (y - C) * math.sin(a)) / S + 0.5
    t = np.clip(t, 0, 1)[..., None]
    c1, c2 = np.array(c1, np.float32), np.array(c2, np.float32)
    rgb = c1 + (c2 - c1) * t
    return Image.fromarray(np.dstack([rgb, np.full((S, S), 255, np.float32)]).astype(np.uint8), "RGBA")

def radial_grad(c_in, c_out, cx=C, cy=C, r=S * .5):
    y, x = np.mgrid[0:S, 0:S].astype(np.float32)
    t = np.clip(np.hypot(x - cx, y - cy) / r, 0, 1)[..., None]
    c1, c2 = np.array(c_in, np.float32), np.array(c_out, np.float32)
    rgb = c1 + (c2 - c1) * t
    return Image.fromarray(np.dstack([rgb, np.full((S, S), 255, np.float32)]).astype(np.uint8), "RGBA")

def mask_circle(r, cx=C, cy=C):
    m = Image.new("L", (S, S), 0); ImageDraw.Draw(m).ellipse((cx - r, cy - r, cx + r, cy + r), fill=255); return m

def mask_squircle(half, n=4.6, cx=C, cy=C):
    y, x = np.mgrid[0:S, 0:S].astype(np.float32)
    v = (np.abs(x - cx) / half) ** n + (np.abs(y - cy) / half) ** n
    return Image.fromarray(((v <= 1) * 255).astype(np.uint8), "L")

def mask_poly(pts):
    m = Image.new("L", (S, S), 0); ImageDraw.Draw(m).polygon(pts, fill=255); return m

def fill_mask(fill_img, mask):
    out = blank(); out.paste(fill_img, (0, 0), mask); return out

def outline_of(mask, width):
    inner = mask.filter(ImageFilter.MinFilter(int(width) * 2 + 1))
    return ImageChops.subtract(mask, inner)

def glow(img, color, radius, strength=1.0):
    a = img.split()[3].filter(ImageFilter.GaussianBlur(radius)).point(lambda v: min(255, int(v * strength)))
    g = Image.new("RGBA", (S, S), tuple(color) + (0,)); g.putalpha(a)
    return Image.alpha_composite(g, img)

def drop_shadow(img, dy=S * .012, blur=S * .02, alpha=120):
    a = img.split()[3].filter(ImageFilter.GaussianBlur(blur)).point(lambda v: int(v * alpha / 255))
    sh = Image.new("RGBA", (S, S), (0, 0, 0, 0)); sh.putalpha(ImageChops.offset(a, 0, int(dy)))
    return Image.alpha_composite(sh, img)

def clip_alpha(layer, mask):
    layer = layer.copy(); layer.putalpha(ImageChops.multiply(layer.split()[3], mask)); return layer

def gloss(shape_mask, strength=80, top=0.0, height=0.55):
    """soft specular highlight on the upper part of a shape"""
    g = blank(); d = ImageDraw.Draw(g)
    bbox = shape_mask.getbbox()
    if not bbox: return g
    x0, y0, x1, y1 = bbox; w, h = x1 - x0, y1 - y0
    d.ellipse((x0 + w * .08, y0 + h * (top - .35), x1 - w * .08, y0 + h * height), fill=(255, 255, 255, strength))
    return clip_alpha(g.filter(ImageFilter.GaussianBlur(S * .006)), shape_mask)

def rim_light(mask, c1, c2, width=S * .009, angle=60):
    return fill_mask(lin_grad(c1, c2, angle), outline_of(mask, width))

def comet_ring(r, color, width, span=150, steps=60, alpha=255):
    """a light streak that fades out behind its head; rotating it sweeps light around the icon"""
    img = blank(); d = ImageDraw.Draw(img); box = (C - r, C - r, C + r, C + r)
    for i in range(steps):
        a0 = -span + span * i / steps; a1 = -span + span * (i + 1) / steps + 1.2
        d.arc(box, a0, a1, fill=tuple(color) + (int(alpha * (i / steps) ** 1.6),), width=int(width * (0.35 + 0.65 * i / steps)))
    d.ellipse((C + r * math.cos(0) - width * .9, C - width * .9, C + r + width * .9, C + width * .9), fill=tuple(color) + (alpha,))
    return glow(img, color, S * .012, 1.0)

def sparkle(d, x, y, k, color=(255, 255, 255, 255)):
    d.polygon([(x, y - k), (x + k * .22, y - k * .22), (x + k, y), (x + k * .22, y + k * .22), (x, y + k), (x - k * .22, y + k * .22), (x - k, y), (x - k * .22, y - k * .22)], fill=color)

def save(name, img): img.resize((N, N), Image.LANCZOS).save(os.path.join(OUT, name))

# ------------------------------------------------------------ the "G" glyph (geometric, PlayStation-like weight)
def g_mask(size, stroke, cx=C, cy=C):
    """Bold geometric G: a ring open at the upper right plus one horizontal bar. Joints are softened with blur+threshold."""
    m = Image.new("L", (S, S), 0); d = ImageDraw.Draw(m)
    r = size - stroke / 2; box = (cx - r, cy - r, cx + r, cy + r)
    d.arc(box, 8, 318, fill=255, width=int(stroke))                       # clockwise from just below 3 o'clock, over the bottom, left and top
    d.rectangle((cx + size * .08, cy - stroke * .5, cx + size, cy + stroke * .52), fill=255)   # the bar, flush with the ring's outer edge
    m = m.filter(ImageFilter.GaussianBlur(S * .005)).point(lambda v: 255 if v > 118 else 0)   # round the corners
    return m.filter(ImageFilter.GaussianBlur(S * .0012))

def draw_g(color_or_img, size, stroke, cx=C, cy=C):
    m = g_mask(size, stroke, cx, cy)
    fill = color_or_img if isinstance(color_or_img, Image.Image) else Image.new("RGBA", (S, S), tuple(color_or_img) + (255,))
    return fill_mask(fill, m), m

def controller(size, cx=C, cy=C, color=WHITE):
    img = blank(); d = ImageDraw.Draw(img); w = size; h = w * .62
    col = tuple(color) + (255,)
    d.rounded_rectangle((cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 4), radius=h * .42, fill=col)
    d.ellipse((cx - w / 2 - h * .02, cy - h * .05, cx - w / 2 + h * .62, cy + h * .62), fill=col)
    d.ellipse((cx + w / 2 - h * .62, cy - h * .05, cx + w / 2 + h * .02, cy + h * .62), fill=col)
    hole = (20, 24, 36, 255); px, py, k = cx - w * .25, cy - h * .06, h * .13
    d.rectangle((px - k * .38, py - k, px + k * .38, py + k), fill=hole); d.rectangle((px - k, py - k * .38, px + k, py + k * .38), fill=hole)
    bx, by, dd = cx + w * .25, cy - h * .06, h * .15
    for dx, dy in ((0, -dd), (0, dd), (-dd, 0), (dd, 0)): d.ellipse((bx + dx - h * .05, by + dy - h * .05, bx + dx + h * .05, by + dy + h * .05), fill=hole)
    return img

R = S * .36   # body radius / half size

# ================= 1. G NEON : dark glass squircle, glowing cyan G =================
sq = mask_squircle(R * 1.02)
tile = fill_mask(lin_grad((40, 48, 74), (12, 15, 24), 65), sq)
tile = Image.alpha_composite(tile, gloss(sq, 38, 0.0, 0.5))
tile = Image.alpha_composite(tile, rim_light(sq, CYAN, VIOLET, S * .010))
gl, gm = draw_g(lin_grad((200, 250, 255), CYAN, 90), R * .62, R * .2)
glow_g = glow(gl, CYAN, S * .022, 1.6)
base = drop_shadow(Image.alpha_composite(tile, glow_g)); base = glow(base, CYAN, S * .03, .8)
save("g_neon_base.png", base); save("g_neon_ring.png", comet_ring(R * 1.22, CYAN, S * .012))

# ================= 2. G GRADIENT : bright gradient squircle, white 3D G =================
sq = mask_squircle(R * 1.02)
tile = fill_mask(lin_grad(CYAN, VIOLET, 55), sq)
tile = Image.alpha_composite(tile, gloss(sq, 90, 0.0, 0.5))
tile = Image.alpha_composite(tile, rim_light(sq, (255, 255, 255), (190, 180, 255), S * .007))
gl, gm = draw_g((255, 255, 255), R * .6, R * .2)
gsh = drop_shadow(gl, dy=S * .014, blur=S * .012, alpha=110)
base = drop_shadow(Image.alpha_composite(tile, gsh)); base = glow(base, VIOLET, S * .035, .9)
save("g_gradient_base.png", base); save("g_gradient_ring.png", comet_ring(R * 1.22, (255, 255, 255), S * .01, alpha=230))

# ================= 3. G ORB : glass sphere with a white glowing G =================
disc = mask_circle(R)
orb = fill_mask(radial_grad((60, 78, 130), (8, 10, 22), C - R * .25, C - R * .35, R * 1.45), disc)
orb = Image.alpha_composite(orb, gloss(disc, 70, 0.0, 0.52))
orb = Image.alpha_composite(orb, rim_light(disc, (170, 230, 255), (120, 110, 255), S * .008, 70))
gl, gm = draw_g((255, 255, 255), R * .56, R * .19)
orb = Image.alpha_composite(orb, glow(gl, CYAN, S * .02, 1.3))
base = glow(drop_shadow(orb), (120, 170, 255), S * .03, .8)
save("g_orb_base.png", base); save("g_orb_ring.png", comet_ring(R * 1.2, (150, 200, 255), S * .011))

# ================= 4. G HEX : pink/violet hexagon badge =================
hexpts = [(C + R * 1.08 * math.cos(math.radians(a)), C + R * 1.08 * math.sin(math.radians(a))) for a in range(-90, 270, 60)]
hm = mask_poly(hexpts).filter(ImageFilter.GaussianBlur(S * .002))
tile = fill_mask(lin_grad(PINK, VIOLET, 60), hm)
tile = Image.alpha_composite(tile, gloss(hm, 85, 0.0, 0.5))
tile = Image.alpha_composite(tile, rim_light(hm, (255, 255, 255), (255, 190, 240), S * .007))
inner = mask_poly([(C + R * .93 * math.cos(math.radians(a)), C + R * .93 * math.sin(math.radians(a))) for a in range(-90, 270, 60)])
tile = Image.alpha_composite(tile, fill_mask(Image.new("RGBA", (S, S), (20, 10, 50, 70)), inner))
gl, gm = draw_g((255, 255, 255), R * .56, R * .19)
base = glow(drop_shadow(Image.alpha_composite(tile, drop_shadow(gl, S * .012, S * .01, 90))), PINK, S * .035, .9)
save("g_hex_base.png", base); save("g_hex_ring.png", comet_ring(R * 1.28, (255, 190, 240), S * .01))

# ================= 5. G SHIELD : gold G on a dark crest =================
k = R * 1.08
shp = [(C - k * .88, C - k * .78), (C, C - k * 1.04), (C + k * .88, C - k * .78), (C + k * .86, C + k * .12), (C + k * .5, C + k * .68), (C, C + k * 1.04), (C - k * .5, C + k * .68), (C - k * .86, C + k * .12)]
sm = mask_poly(shp).filter(ImageFilter.GaussianBlur(S * .002))
tile = fill_mask(lin_grad((40, 46, 70), (12, 14, 24), 70), sm)
tile = Image.alpha_composite(tile, gloss(sm, 40, 0.0, 0.45))
tile = Image.alpha_composite(tile, rim_light(sm, GOLD, ORANGE, S * .011))
gl, gm = draw_g(lin_grad((255, 240, 160), ORANGE, 80), R * .52, R * .18, cy=C - R * .02)
base = glow(drop_shadow(Image.alpha_composite(tile, glow(gl, GOLD, S * .018, 1.2))), GOLD, S * .03, .7)
save("g_shield_base.png", base); save("g_shield_ring.png", comet_ring(R * 1.28, GOLD, S * .01))

# ================= 6. G MINIMAL : thin ring, small G, very light =================
base = blank(); d = ImageDraw.Draw(base)
d.ellipse((C - R * .98, C - R * .98, C + R * .98, C + R * .98), fill=(255, 255, 255, 22), outline=(255, 255, 255, 235), width=int(S * .009))
gl, gm = draw_g((255, 255, 255), R * .44, R * .15)
base = glow(Image.alpha_composite(base, gl), CYAN, S * .018, .9)
save("g_minimal_base.png", base); save("g_minimal_ring.png", comet_ring(R * 1.12, CYAN, S * .008, span=110, alpha=235))

# ================= 7. PLANET : banded sphere, orbit ring, moons =================
disc = mask_circle(R)
pl = fill_mask(radial_grad((190, 160, 255), (24, 16, 70), C - R * .35, C - R * .4, R * 1.5), disc)
band = blank(); bd = ImageDraw.Draw(band)
for i, (yy, hh, a) in enumerate(((-.45, .13, 40), (-.1, .2, 34), (.32, .12, 44))):
    bd.rectangle((C - R, C + R * yy, C + R, C + R * (yy + hh)), fill=(255, 255, 255, a))
pl = Image.alpha_composite(pl, clip_alpha(band.filter(ImageFilter.GaussianBlur(S * .008)), disc))
pl = Image.alpha_composite(pl, gloss(disc, 70, 0.0, 0.5)); pl = Image.alpha_composite(pl, rim_light(disc, (220, 200, 255), (110, 90, 255), S * .007, 60))
base = glow(drop_shadow(pl), (170, 140, 255), S * .035, .9)
orbit = blank(); od = ImageDraw.Draw(orbit)
od.ellipse((C - R * 1.42, C - R * .46, C + R * 1.42, C + R * .46), outline=CYAN + (255,), width=int(S * .011))
orbit = orbit.rotate(-24, resample=Image.BICUBIC)
od = ImageDraw.Draw(orbit); a = math.radians(-24)
for ang, rad in ((35, S * .02), (215, S * .014)):
    x, y = R * 1.42 * math.cos(math.radians(ang)), R * .46 * math.sin(math.radians(ang)); px, py = C + x * math.cos(a) - y * math.sin(a), C + x * math.sin(a) + y * math.cos(a)
    od.ellipse((px - rad, py - rad, px + rad, py + rad), fill=WHITE + (255,))
save("planet_base.png", base); save("planet_ring.png", glow(orbit, CYAN, S * .012, .9))

# ================= 8. CRYSTAL : faceted gem with inner light =================
pts = [(C + R * 1.04 * math.cos(math.radians(a)), C + R * 1.04 * math.sin(math.radians(a))) for a in range(-90, 270, 60)]
inn = [(C + R * .5 * math.cos(math.radians(a)), C + R * .5 * math.sin(math.radians(a))) for a in range(-60, 300, 60)]
gem = blank(); d = ImageDraw.Draw(gem)
cols = [(190, 245, 255), (110, 205, 250), (60, 150, 235), (50, 110, 215), (90, 150, 250), (170, 230, 255)]
for i in range(6):
    d.polygon([pts[i], pts[(i + 1) % 6], inn[i]], fill=cols[i] + (255,))
    d.polygon([pts[i], inn[i], inn[i - 1]], fill=tuple(min(255, c + 25) for c in cols[(i + 3) % 6]) + (255,))
d.polygon(inn, fill=(215, 248, 255, 255))
d.polygon(pts, outline=WHITE + (235,), width=int(S * .006))
gm_ = mask_poly(pts)
gem = Image.alpha_composite(gem, gloss(gm_, 55, 0.0, 0.42))
base = glow(drop_shadow(gem), CYAN, S * .035, 1.0)
ring = blank(); rd = ImageDraw.Draw(ring)
for i in range(8):
    aa = math.radians(i * 45 + 12); sparkle(rd, C + R * 1.3 * math.cos(aa), C + R * 1.3 * math.sin(aa), S * (.026 if i % 2 else .016))
save("crystal_base.png", base); save("crystal_ring.png", glow(ring, CYAN, S * .01, 1.0))

# ================= 9. STAR : bevelled golden star =================
def star(r1, r2, rot=-90):
    return [(C + (r1 if i % 2 == 0 else r2) * math.cos(math.radians(rot + i * 36)), C + S * .012 + (r1 if i % 2 == 0 else r2) * math.sin(math.radians(rot + i * 36))) for i in range(10)]
sm = mask_poly(star(R * 1.14, R * .5)).filter(ImageFilter.GaussianBlur(S * .0015))
st = fill_mask(lin_grad((255, 245, 160), (255, 150, 40), 70), sm)
bev = mask_poly(star(R * .95, R * .42)); st = Image.alpha_composite(st, fill_mask(lin_grad((255, 255, 210), (255, 190, 70), 60), bev).filter(ImageFilter.GaussianBlur(S * .002)))
st = Image.alpha_composite(st, gloss(sm, 80, 0.0, 0.45)); st = Image.alpha_composite(st, rim_light(sm, (255, 255, 255), (255, 190, 80), S * .006))
base = glow(drop_shadow(st), GOLD, S * .035, 1.1)
ring = blank(); rd = ImageDraw.Draw(ring)
for i in range(10):
    aa = math.radians(i * 36 + 8); sparkle(rd, C + R * 1.34 * math.cos(aa), C + R * 1.34 * math.sin(aa), S * (.022 if i % 2 else .013), (255, 244, 200, 255))
save("star_base.png", base); save("star_ring.png", glow(ring, GOLD, S * .01, 1.0))

# ================= 10. ATOM : glowing nucleus with three orbits =================
def orbits(alpha, width):
    lay = blank()
    for kk, col in enumerate((CYAN, VIOLET, TEAL)):
        o = blank(); ImageDraw.Draw(o).ellipse((C - R * 1.28, C - R * .46, C + R * 1.28, C + R * .46), outline=col + (alpha,), width=int(width))
        lay = Image.alpha_composite(lay, o.rotate(kk * 60, resample=Image.BICUBIC))
    return lay
nuc = fill_mask(radial_grad((255, 255, 255), CYAN, C - R * .08, C - R * .1, R * .42), mask_circle(R * .34))
base = glow(Image.alpha_composite(orbits(120, S * .005), nuc), CYAN, S * .035, 1.3)
ring = orbits(255, S * .009); rd = ImageDraw.Draw(ring)
for kk in range(3):
    aa = math.radians(kk * 60); x, y = R * 1.28 * math.cos(math.radians(40 + kk * 120)), R * .46 * math.sin(math.radians(40 + kk * 120))
    px, py = C + x * math.cos(aa) - y * math.sin(aa), C + x * math.sin(aa) + y * math.cos(aa); rd.ellipse((px - S * .02, py - S * .02, px + S * .02, py + S * .02), fill=WHITE + (255,))
save("atom_base.png", base); save("atom_ring.png", glow(ring, CYAN, S * .01, .9))

# ================= 11. PAD : glass squircle with a gradient controller =================
sq = mask_squircle(R * 1.02)
tile = fill_mask(lin_grad((46, 54, 84), (14, 17, 28), 65), sq)
tile = Image.alpha_composite(tile, gloss(sq, 36, 0.0, 0.5)); tile = Image.alpha_composite(tile, rim_light(sq, TEAL, VIOLET, S * .009))
pad = controller(R * 1.15, cy=C + R * .02); grad = lin_grad(TEAL, VIOLET, 20); grad.putalpha(pad.split()[3])
pad_holes = controller(R * 1.15, cy=C + R * .02); holes_only = Image.alpha_composite(grad, ImageChops.subtract(pad_holes.convert("RGB"), grad.convert("RGB")).convert("RGBA").point(lambda v: 0))
base = glow(drop_shadow(Image.alpha_composite(tile, glow(pad, TEAL, S * .015, .6))), TEAL, S * .03, .6)
save("pad_base.png", base); save("pad_ring.png", comet_ring(R * 1.22, TEAL, S * .011))

# ================= 12. EMBER : fiery orb with a lightning bolt =================
disc = mask_circle(R)
em = fill_mask(radial_grad((255, 210, 90), (200, 40, 60), C - R * .2, C - R * .3, R * 1.4), disc)
em = Image.alpha_composite(em, gloss(disc, 85, 0.0, 0.5)); em = Image.alpha_composite(em, rim_light(disc, (255, 235, 170), (255, 120, 60), S * .008, 70))
bolt = [(0.10, -0.50), (-0.30, 0.05), (-0.02, 0.05), (-0.14, 0.52), (0.32, -0.12), (0.04, -0.12), (0.22, -0.50)]
bi = blank(); ImageDraw.Draw(bi).polygon([(C + x * R * 1.15, C + y * R * 1.15) for x, y in bolt], fill=(255, 255, 255, 255))
em = Image.alpha_composite(em, drop_shadow(bi, S * .012, S * .012, 100))
base = glow(drop_shadow(em), ORANGE, S * .04, 1.1)
save("ember_base.png", base); save("ember_ring.png", comet_ring(R * 1.2, (255, 190, 110), S * .012))

# ------------------------------------------------------------ contact sheet
names = ("g_neon", "g_gradient", "g_orb", "g_hex", "g_shield", "g_minimal", "planet", "crystal", "star", "atom", "pad", "ember")
sheet = Image.new("RGBA", (6 * 256, 2 * 256), (22, 26, 38, 255))
for i, n in enumerate(names):
    for layer in ("base", "ring"):
        im = Image.open(os.path.join(OUT, f"{n}_{layer}.png")).resize((256, 256), Image.LANCZOS)
        sheet.alpha_composite(im, ((i % 6) * 256, (i // 6) * 256))
sheet.convert("RGB").save("/tmp/icon_sheet.png")
print("icons written to", os.path.abspath(OUT))
