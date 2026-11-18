"""Generates the floating desktop icon styles: for each style a static body ({id}_base.png) and a rotating overlay ({id}_ring.png),
512x512 transparent PNGs. Everything is drawn procedurally (4x supersampled). Run: python tools/generate_icons.py (needs Pillow)."""
import math, os
from PIL import Image, ImageDraw, ImageFilter, ImageChops

OUT = os.path.join(os.path.dirname(__file__), "..", "GameIndex", "Assets", "FloatingIcons")
N, SS = 512, 4
S = N * SS
CYAN, VIOLET, WHITE = (101, 217, 255), (139, 124, 255), (242, 244, 248)

def blank(): return Image.new("RGBA", (S, S), (0, 0, 0, 0))

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def gradient(c1, c2, angle=45):
    """linear gradient image S x S"""
    g = Image.new("RGB", (S, S)); px = g.load()
    ca, sa = math.cos(math.radians(angle)), math.sin(math.radians(angle))
    for y in range(0, S, 2):
        for x in range(0, S, 2):
            t = ((x - S / 2) * ca + (y - S / 2) * sa) / S + 0.5
            c = lerp(c1, c2, max(0, min(1, t)))
            px[x, y] = c
            if x + 1 < S: px[x + 1, y] = c
            if y + 1 < S:
                px[x, y + 1] = c
                if x + 1 < S: px[x + 1, y + 1] = c
    return g

def disc_mask(r, cx=S // 2, cy=S // 2):
    m = Image.new("L", (S, S), 0); ImageDraw.Draw(m).ellipse((cx - r, cy - r, cx + r, cy + r), fill=255); return m

def paste_masked(img, fill_img, mask):
    img.paste(fill_img.convert("RGBA"), (0, 0), mask)

def glow(img, color, radius, strength=1.0):
    a = img.split()[3].filter(ImageFilter.GaussianBlur(radius))
    a = a.point(lambda v: min(255, int(v * strength)))
    g = Image.new("RGBA", (S, S), color + (0,)); g.putalpha(a)
    return Image.alpha_composite(g, img)

def controller(draw, cx, cy, w, color, shadow=None, body_on=True, holes_on=True):
    """simple game controller glyph"""
    h = w * 0.62
    def body(d, col, off=0):
        d.rounded_rectangle((cx - w / 2, cy - h / 2 + off, cx + w / 2, cy + h / 4 + off), radius=h * .42, fill=col)
        d.ellipse((cx - w / 2 - h * .02, cy - h * .05 + off, cx - w / 2 + h * .62, cy + h * .62 + off), fill=col)
        d.ellipse((cx + w / 2 - h * .62, cy - h * .05 + off, cx + w / 2 + h * .02, cy + h * .62 + off), fill=col)
    if body_on:
        if shadow: body(draw, shadow, off=h * .04)
        body(draw, color)
    if not holes_on: return
    hole = (20, 24, 36, 255)
    # d-pad
    px, py, k = cx - w * .25, cy - h * .06, h * .13
    draw.rectangle((px - k * .38, py - k, px + k * .38, py + k), fill=hole); draw.rectangle((px - k, py - k * .38, px + k, py + k * .38), fill=hole)
    # buttons
    bx, by, d = cx + w * .25, cy - h * .06, h * .15
    for dx, dy in ((0, -d), (0, d), (-d, 0), (d, 0)):
        draw.ellipse((bx + dx - h * .05, by + dy - h * .05, bx + dx + h * .05, by + dy + h * .05), fill=hole)
    # sticks
    for sx in (cx - w * .12, cx + w * .12):
        draw.ellipse((sx - h * .075, cy + h * .08 - h * .075, sx + h * .075, cy + h * .08 + h * .075), fill=hole)

def dashed_ring(r, width, color, dash_deg, gap_deg, alpha=255, rot=0):
    img = blank(); d = ImageDraw.Draw(img); box = (S / 2 - r, S / 2 - r, S / 2 + r, S / 2 + r)
    a = rot
    while a < rot + 360:
        d.arc(box, a, a + dash_deg, fill=color + (alpha,), width=int(width)); a += dash_deg + gap_deg
    return img

def save(name, img):
    img.resize((N, N), Image.LANCZOS).save(os.path.join(OUT, name))

def gloss(img, r, strength=70):
    g = blank(); d = ImageDraw.Draw(g)
    d.ellipse((S / 2 - r * .78, S / 2 - r * .95, S / 2 + r * .78, S / 2 - r * .05), fill=(255, 255, 255, strength))
    m = disc_mask(r); g.putalpha(ImageChops.multiply(g.split()[3], m)); return Image.alpha_composite(img, g.filter(ImageFilter.GaussianBlur(S * .004)))

R = int(S * 0.36)   # body radius: leaves room for the glow

# 1 ---- NEON: dark glass orb, gradient rim, glowing ring
base = blank()
paste_masked(base, gradient((44, 52, 76), (14, 18, 27), 60), disc_mask(R))
rim = blank(); ImageDraw.Draw(rim).ellipse((S / 2 - R, S / 2 - R, S / 2 + R, S / 2 + R), outline=CYAN + (255,), width=int(S * .010))
base = Image.alpha_composite(base, rim); base = gloss(base, R, 38)
ImageDraw.Draw(base).ellipse((0, 0, 0, 0))
d = ImageDraw.Draw(base); controller(d, S / 2, S / 2 + S * .005, R * 1.12, WHITE + (255,), (0, 0, 0, 90))
base = glow(base, CYAN, S * .03, 1.1)
ring = Image.alpha_composite(dashed_ring(int(R * 1.16), S * .011, CYAN, 36, 14), dashed_ring(int(R * 0.86), S * .006, VIOLET, 8, 26, 190))
ring = glow(ring, CYAN, S * .012, .9)
save("neon_base.png", base); save("neon_ring.png", ring)

# 2 ---- PULSE: solid gradient orb with strong gloss
base = blank(); paste_masked(base, gradient(CYAN, VIOLET, 45), disc_mask(R))
base = gloss(base, R, 95)
d = ImageDraw.Draw(base); controller(d, S / 2, S / 2 + S * .01, R * 1.1, (255, 255, 255, 255), (30, 20, 90, 110))
base = glow(base, VIOLET, S * .035, 1.0)
ring = dashed_ring(int(R * 1.15), S * .008, WHITE, 70, 20, 210); ring = glow(ring, WHITE, S * .008, .6)
save("pulse_base.png", base); save("pulse_ring.png", ring)

# 3 ---- PLANET: shaded sphere with tilted orbit
base = blank(); paste_masked(base, gradient((150, 130, 255), (22, 18, 70), 70), disc_mask(R))
sh = blank(); ImageDraw.Draw(sh).ellipse((S / 2 - R * .55, S / 2 - R * .85, S / 2 + R * .2, S / 2 - R * .25), fill=(255, 255, 255, 80))
sh.putalpha(ImageChops.multiply(sh.split()[3], disc_mask(R))); base = Image.alpha_composite(base, sh.filter(ImageFilter.GaussianBlur(S * .012)))
d = ImageDraw.Draw(base); controller(d, S / 2, S / 2 + S * .01, R * .95, (255, 255, 255, 235), (10, 8, 40, 120))
base = glow(base, (170, 150, 255), S * .03, .9)
orbit = blank(); od = ImageDraw.Draw(orbit)
od.ellipse((S / 2 - R * 1.38, S / 2 - R * .42, S / 2 + R * 1.38, S / 2 + R * .42), outline=CYAN + (255,), width=int(S * .011))
orbit = orbit.rotate(-22, resample=Image.BICUBIC)
dot = ImageDraw.Draw(orbit); ox, oy = S / 2 + R * 1.38 * math.cos(math.radians(-22)), S / 2 - R * 1.38 * math.sin(math.radians(22)) * .3
dot.ellipse((ox - S * .02, oy - S * .02, ox + S * .02, oy + S * .02), fill=WHITE + (255,))
ring = glow(orbit, CYAN, S * .012, .9)
save("planet_base.png", base); save("planet_ring.png", ring)

# 4 ---- CRYSTAL: faceted hexagonal gem
base = blank(); d = ImageDraw.Draw(base)
pts = [(S / 2 + R * 1.02 * math.cos(math.radians(a)), S / 2 + R * 1.02 * math.sin(math.radians(a))) for a in range(-90, 270, 60)]
d.polygon(pts, fill=CYAN + (255,))
facets = [((pts[0], pts[1], (S / 2, S / 2)), (150, 235, 255)), ((pts[1], pts[2], (S / 2, S / 2)), (70, 170, 235)), ((pts[2], pts[3], (S / 2, S / 2)), (50, 120, 210)),
          ((pts[3], pts[4], (S / 2, S / 2)), (60, 110, 220)), ((pts[4], pts[5], (S / 2, S / 2)), (110, 160, 255)), ((pts[5], pts[0], (S / 2, S / 2)), (190, 240, 255))]
for tri, col in facets: d.polygon(list(tri), fill=col + (255,))
d.polygon(pts, outline=WHITE + (230,), width=int(S * .006))
d = ImageDraw.Draw(base); controller(d, S / 2, S / 2 + S * .005, R * .8, (255, 255, 255, 235), (20, 40, 110, 120))
base = glow(base, CYAN, S * .03, 1.0)
ring = blank(); rd = ImageDraw.Draw(ring)
for i in range(6):
    a = math.radians(i * 60 + 15); x, y = S / 2 + R * 1.25 * math.cos(a), S / 2 + R * 1.25 * math.sin(a); k = S * (.026 if i % 2 else .016)
    rd.polygon([(x, y - k), (x + k * .3, y - k * .3), (x + k, y), (x + k * .3, y + k * .3), (x, y + k), (x - k * .3, y + k * .3), (x - k, y), (x - k * .3, y - k * .3)], fill=WHITE + (255,))
ring = glow(ring, CYAN, S * .01, 1.0)
save("crystal_base.png", base); save("crystal_ring.png", ring)

# 5 ---- MINIMAL: thin glass ring + dot, very light
base = blank(); d = ImageDraw.Draw(base)
d.ellipse((S / 2 - R * .95, S / 2 - R * .95, S / 2 + R * .95, S / 2 + R * .95), fill=(255, 255, 255, 26), outline=WHITE + (235,), width=int(S * .008))
d.ellipse((S / 2 - R * .2, S / 2 - R * .2, S / 2 + R * .2, S / 2 + R * .2), fill=CYAN + (255,))
base = glow(base, CYAN, S * .02, .8)
ring = dashed_ring(int(R * 1.12), S * .006, CYAN, 50, 130, 230); ring = glow(ring, CYAN, S * .008, .8)
save("minimal_base.png", base); save("minimal_ring.png", ring)

# 6 ---- GAMEPAD: big pad with gradient fill on a soft glow, no disc
pad = blank(); pd = ImageDraw.Draw(pad); controller(pd, S / 2, S / 2, R * 2.0, (255, 255, 255, 255), holes_on=False)
grad = gradient(CYAN, VIOLET, 20).convert("RGBA"); grad.putalpha(pad.split()[3])
base = grad
d = ImageDraw.Draw(base); controller(d, S / 2, S / 2, R * 2.0, (0, 0, 0, 0), body_on=False)       # dark button/stick holes
base = glow(base, VIOLET, S * .03, 1.1)
ring = blank(); rd = ImageDraw.Draw(ring)
for i in range(3):
    a = math.radians(i * 120); x, y = S / 2 + R * 1.3 * math.cos(a), S / 2 + R * 1.3 * math.sin(a); rd.ellipse((x - S * .014, y - S * .014, x + S * .014, y + S * .014), fill=CYAN + (255,))
save("gamepad_base.png", base); save("gamepad_ring.png", glow(ring, CYAN, S * .008, 1.0))


def bolt(draw, cx, cy, w, color):
    p = [(0.10, -0.50), (-0.30, 0.05), (-0.02, 0.05), (-0.14, 0.52), (0.32, -0.12), (0.04, -0.12), (0.22, -0.50)]
    draw.polygon([(cx + x * w, cy + y * w) for x, y in p], fill=color)

def star_pts(cx, cy, r1, r2, n=5, rot=-90):
    pts = []
    for i in range(n * 2):
        a = math.radians(rot + i * 180 / n); r = r1 if i % 2 == 0 else r2
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts

def gradient_shape(poly_fn, c1, c2, angle=60):
    m = Image.new("L", (S, S), 0); poly_fn(ImageDraw.Draw(m)); g = gradient(c1, c2, angle).convert("RGBA"); g.putalpha(m); return g

# 7 ---- EMBER: hot orange orb with a bolt
base = blank(); paste_masked(base, gradient((255, 190, 70), (225, 55, 60), 60), disc_mask(R)); base = gloss(base, R, 80)
d = ImageDraw.Draw(base); bolt(d, S / 2 + S * .004, S / 2 + S * .004, R * 1.15, (0, 0, 0, 70)); bolt(d, S / 2, S / 2, R * 1.15, (255, 255, 255, 255))
base = glow(base, (255, 150, 60), S * .035, 1.1)
ring = glow(dashed_ring(int(R * 1.15), S * .010, (255, 200, 120), 40, 18, 230), (255, 150, 60), S * .01, .9)
save("ember_base.png", base); save("ember_ring.png", ring)

# 8 ---- AURORA: teal -> violet orb
base = blank(); paste_masked(base, gradient((70, 255, 200), (110, 100, 255), 50), disc_mask(R)); base = gloss(base, R, 85)
d = ImageDraw.Draw(base); controller(d, S / 2, S / 2 + S * .01, R * 1.1, (255, 255, 255, 255), (10, 50, 90, 100))
base = glow(base, (90, 255, 210), S * .035, 1.0)
ring = Image.alpha_composite(dashed_ring(int(R * 1.15), S * .009, (150, 255, 230), 30, 12, 230), dashed_ring(int(R * 0.88), S * .006, (200, 170, 255), 6, 24, 200))
save("aurora_base.png", base); save("aurora_ring.png", glow(ring, (90, 255, 210), S * .01, .9))

# 9 ---- SHIELD: crest with a controller
def shield(dr, scale=1.0, inset=0):
    k = R * 1.05 * scale - inset
    dr.polygon([(S / 2 - k * .88, S / 2 - k * .78), (S / 2, S / 2 - k * 1.02), (S / 2 + k * .88, S / 2 - k * .78), (S / 2 + k * .86, S / 2 + k * .12),
                (S / 2 + k * .5, S / 2 + k * .66), (S / 2, S / 2 + k * 1.02), (S / 2 - k * .5, S / 2 + k * .66), (S / 2 - k * .86, S / 2 + k * .12)], fill=255)
outer = Image.new("L", (S, S), 0); shield(ImageDraw.Draw(outer)); inner = Image.new("L", (S, S), 0); shield(ImageDraw.Draw(inner), 1.0, S * .018)
base = blank(); base.paste(gradient(CYAN, VIOLET, 70).convert("RGBA"), (0, 0), outer)
dark = gradient((34, 42, 66), (14, 18, 28), 70).convert("RGBA"); base.paste(dark, (0, 0), inner)
d = ImageDraw.Draw(base); controller(d, S / 2, S / 2 + S * .01, R * 1.0, WHITE + (255,), (0, 0, 0, 90))
base = glow(base, CYAN, S * .03, 1.0)
ring = Image.alpha_composite(dashed_ring(int(R * 1.28), S * .007, CYAN, 20, 30, 220), dashed_ring(int(R * 1.28), S * .004, VIOLET, 6, 44, 200, rot=20))
save("shield_base.png", base); save("shield_ring.png", glow(ring, CYAN, S * .01, .8))

# 10 ---- STAR: golden star
base = gradient_shape(lambda dr: dr.polygon(star_pts(S / 2, S / 2 + S * .01, R * 1.12, R * .47), fill=255), (255, 236, 130), (255, 150, 40), 70)
hi = blank(); ImageDraw.Draw(hi).polygon(star_pts(S / 2, S / 2 + S * .01, R * 1.12, R * .47), outline=(255, 255, 255, 220), width=int(S * .006)); base = Image.alpha_composite(base, hi)
base = glow(base, (255, 200, 80), S * .035, 1.1)
ring = blank(); rd = ImageDraw.Draw(ring)
for i in range(8):
    a = math.radians(i * 45 + 10); x, y = S / 2 + R * 1.3 * math.cos(a), S / 2 + R * 1.3 * math.sin(a); k = S * (.02 if i % 2 else .012)
    rd.polygon([(x, y - k), (x + k * .3, y - k * .3), (x + k, y), (x + k * .3, y + k * .3), (x, y + k), (x - k * .3, y + k * .3), (x - k, y), (x - k * .3, y - k * .3)], fill=(255, 240, 190, 255))
save("star_base.png", base); save("star_ring.png", glow(ring, (255, 200, 80), S * .01, 1.0))

# 11 ---- JOYSTICK: arcade stick
base = blank(); d = ImageDraw.Draw(base)
d.rounded_rectangle((S / 2 - R * .95, S / 2 + R * .28, S / 2 + R * .95, S / 2 + R * .86), radius=R * .22, fill=(45, 52, 76, 255), outline=CYAN + (255,), width=int(S * .006))
d.ellipse((S / 2 - R * .32, S / 2 + R * .12, S / 2 + R * .32, S / 2 + R * .46), fill=(25, 30, 46, 255))
d.rectangle((S / 2 - R * .075, S / 2 - R * .52, S / 2 + R * .075, S / 2 + R * .3), fill=(190, 198, 215, 255))
ball = gradient_shape(lambda dr: dr.ellipse((S / 2 - R * .4, S / 2 - R * 1.0, S / 2 + R * .4, S / 2 - R * .2), fill=255), (255, 120, 120), (200, 20, 60), 60)
base = Image.alpha_composite(base, ball)
d = ImageDraw.Draw(base)
d.ellipse((S / 2 - R * .24, S / 2 - R * .88, S / 2 - R * .02, S / 2 - R * .66), fill=(255, 255, 255, 140))
for i, c in enumerate(((255, 210, 70), (70, 200, 255), (120, 255, 150))):
    x = S / 2 - R * .52 + i * R * .52; d.ellipse((x - R * .13, S / 2 + R * .5 - R * .13, x + R * .13, S / 2 + R * .5 + R * .13), fill=c + (255,))
base = glow(base, (255, 90, 120), S * .028, .8)
ring = dashed_ring(int(R * 1.2), S * .007, (255, 140, 160), 24, 36, 210)
save("joystick_base.png", base); save("joystick_ring.png", glow(ring, (255, 90, 120), S * .008, .8))

# 12 ---- ATOM: nucleus + three orbits (the rotating layer carries the bright orbits)
def orbit_layer(alpha, width):
    lay = blank()
    for k, col in enumerate((CYAN, VIOLET, (120, 255, 220))):
        o = blank(); ImageDraw.Draw(o).ellipse((S / 2 - R * 1.25, S / 2 - R * .46, S / 2 + R * 1.25, S / 2 + R * .46), outline=col + (alpha,), width=int(width))
        lay = Image.alpha_composite(lay, o.rotate(k * 60, resample=Image.BICUBIC))
    return lay
base = orbit_layer(110, S * .004); bd = ImageDraw.Draw(base)
nuc = gradient_shape(lambda dr: dr.ellipse((S / 2 - R * .3, S / 2 - R * .3, S / 2 + R * .3, S / 2 + R * .3), fill=255), (255, 255, 255), CYAN, 60)
base = Image.alpha_composite(base, nuc); base = glow(base, CYAN, S * .03, 1.1)
ring = orbit_layer(255, S * .008)
for k in range(3):
    a = math.radians(k * 60); x, y = R * 1.25 * math.cos(math.radians(40 + k * 120)), R * .46 * math.sin(math.radians(40 + k * 120))
    px = S / 2 + x * math.cos(a) - y * math.sin(a); py = S / 2 + x * math.sin(a) + y * math.cos(a)
    ImageDraw.Draw(ring).ellipse((px - S * .018, py - S * .018, px + S * .018, py + S * .018), fill=WHITE + (255,))
save("atom_base.png", base); save("atom_ring.png", glow(ring, CYAN, S * .01, .9))

# contact sheet for a quick visual check
names = ("neon", "pulse", "planet", "crystal", "minimal", "gamepad", "ember", "aurora", "shield", "star", "joystick", "atom")
sheet = Image.new("RGBA", (6 * 256, 2 * 256), (22, 26, 38, 255))
for i, n in enumerate(names):
    for layer in ("base", "ring"):
        im = Image.open(os.path.join(OUT, f"{n}_{layer}.png")).resize((256, 256), Image.LANCZOS)
        sheet.alpha_composite(im, ((i % 6) * 256, (i // 6) * 256))
sheet.convert("RGB").save("/tmp/icon_sheet.png")
print("icons written to", os.path.abspath(OUT))
