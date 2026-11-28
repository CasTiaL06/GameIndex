"""Builds the brand assets from the two reference designs in docs/reference:
  floating-icon-reference.png -> floating icon style "g_ref"
  tray-icon-reference.png     -> app/tray icon (GameIndex.ico), Brand/logo.png and floating style "g_play"
The references are cut out of their backgrounds (tile = measured rounded rectangle, halo/orbit = bright neon pixels only). Needs numpy + Pillow."""
import os, math, numpy as np
from PIL import Image, ImageDraw, ImageFilter
HERE = os.path.dirname(__file__); ROOT = os.path.join(HERE, "..")
REF = os.path.join(ROOT, "docs", "reference"); ICONS = os.path.join(ROOT, "GameIndex", "Assets", "FloatingIcons"); BRAND = os.path.join(ROOT, "GameIndex", "Assets", "Brand")
os.makedirs(ICONS, exist_ok=True); os.makedirs(BRAND, exist_ok=True)

def comet(size, radius, color, width, span=150, steps=60):
    S = size * 4; img = Image.new("RGBA", (S, S), (0, 0, 0, 0)); d = ImageDraw.Draw(img); c = S / 2; r = radius * 4; box = (c - r, c - r, c + r, c + r)
    for i in range(steps):
        a0 = -span + span * i / steps; a1 = -span + span * (i + 1) / steps + 1.2
        d.arc(box, a0, a1, fill=color + (int(235 * (i / steps) ** 1.6),), width=int(width * 4 * (0.35 + 0.65 * i / steps)))
    d.ellipse((c + r - width * 4, c - width * 4, c + r + width * 4, c + width * 4), fill=color + (235,))
    g = img.split()[3].filter(ImageFilter.GaussianBlur(S * .012)); glow = Image.new("RGBA", (S, S), color + (0,)); glow.putalpha(g)
    return Image.alpha_composite(glow, img).resize((size, size), Image.LANCZOS)

def to_canvas(img, canvas=512, content=0.84):
    s = canvas * content / max(img.size); r = img.resize((int(img.width * s), int(img.height * s)), Image.LANCZOS)
    out = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0)); out.alpha_composite(r, ((canvas - r.width) // 2, (canvas - r.height) // 2)); return out

# ---------------- tray / app logo (black background -> alpha) ----------------
src = Image.open(os.path.join(REF, "tray-icon-reference.png")).convert("RGB")
a = np.asarray(src).astype(np.float32) / 255; v = a.max(axis=2)
ys, xs = np.where(v[:, :1100] > 0.55); x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max(); W, H = x1 - x0, y1 - y0
pad = int(0.22 * W); crop = src.crop((x0 - pad, y0 - pad, x1 + pad, y1 + pad)); ca = np.asarray(crop).astype(np.float32) / 255; cv = ca.max(axis=2)
m = Image.new("L", crop.size, 0); ImageDraw.Draw(m).rounded_rectangle((pad, pad, pad + W, pad + H), radius=int(0.20 * W), fill=255)
inside = np.asarray(m.filter(ImageFilter.GaussianBlur(1.0))).astype(np.float32) / 255
alpha = np.clip(np.maximum(inside, np.clip((cv - 0.03) * 1.8, 0, 1) * (1 - inside)), 0, 1)
rgb = np.clip(np.where(inside[..., None] > 0.5, ca, ca / np.maximum(cv[..., None], 1e-3)), 0, 1)
logo = Image.fromarray((np.dstack([rgb, alpha]) * 255).astype(np.uint8), "RGBA")
S = max(logo.size); sq = Image.new("RGBA", (S, S), (0, 0, 0, 0)); sq.alpha_composite(logo, ((S - logo.width) // 2, (S - logo.height) // 2))
sq.resize((512, 512), Image.LANCZOS).save(os.path.join(BRAND, "logo.png"))
to_canvas(sq, 512, 1.0).save(os.path.join(ICONS, "g_play_base.png")); comet(512, 232, (150, 170, 255), 5).save(os.path.join(ICONS, "g_play_ring.png"))
# .ico: tight crop of the tile (a bit of margin) so it stays readable at 16-32 px
m_ = int(0.035 * W); cx0 = (S - logo.width) // 2 + pad - m_; cy0 = (S - logo.height) // 2 + pad - m_
tight = sq.crop((cx0, cy0, cx0 + W + 2 * m_, cy0 + H + 2 * m_)).resize((256, 256), Image.LANCZOS)
tight.save(os.path.join(ROOT, "GameIndex", "Assets", "GameIndex.ico"), sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])

# ---------------- floating icon (textured background -> alpha) ----------------
src = Image.open(os.path.join(REF, "floating-icon-reference.png")).convert("RGB")
cx, cy, half = 621, 589, 520; crop = src.crop((cx - half, cy - half, cx + half, cy + half)); a = np.asarray(crop).astype(np.float32) / 255; r, g, b = a[..., 0], a[..., 1], a[..., 2]
SS = 3; Wc = crop.width; m = Image.new("L", (Wc * SS, Wc * SS), 0)
ImageDraw.Draw(m).rounded_rectangle(((226 - (cx - half)) * SS, (198 - (cy - half)) * SS, (1017 - (cx - half)) * SS, (980 - (cy - half)) * SS), radius=185 * SS, fill=255)
inside = np.asarray(m.resize((Wc, Wc), Image.LANCZOS)).astype(np.float32) / 255
nn = np.clip((b - np.maximum(r * 1.8, 0.32)) * 2.4, 0, 1) * np.clip(((b - np.minimum(r, g)) - 0.30) * 4, 0, 1)
nn = np.asarray(Image.fromarray((nn * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.8))).astype(np.float32) / 255
core = np.clip(np.maximum(inside, nn * (1 - inside)), 0, 1)
rgb = np.where(inside[..., None] > 0.5, a, np.clip(a / np.maximum(b, 1e-3)[..., None], 0, 1))
img = Image.fromarray((np.dstack([rgb, core]) * 255).astype(np.uint8), "RGBA")
al = Image.fromarray((core * 255).astype(np.uint8)); h = np.clip(np.asarray(al.filter(ImageFilter.GaussianBlur(26))).astype(np.float32) / 255 * .85 + np.asarray(al.filter(ImageFilter.GaussianBlur(8))).astype(np.float32) / 255 * .5, 0, 1) * (1 - core)
glow = Image.fromarray((np.dstack([np.full(core.shape, .25), np.full(core.shape, .62), np.full(core.shape, 1.0), h * .75]) * 255).astype(np.uint8), "RGBA")
final = Image.alpha_composite(glow, img)
final.resize((512, 512), Image.LANCZOS).save(os.path.join(ICONS, "g_ref_base.png")); comet(512, 236, (120, 190, 255), 5, span=120).save(os.path.join(ICONS, "g_ref_ring.png"))
print("brand assets written")
