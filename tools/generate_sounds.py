"""Generates GameIndex's UI sounds (startup / open / close) as 16-bit stereo WAV files.
Pure synthesis (no samples): airy detuned pad + glassy bell sparkles + small reverb, in the spirit of a console boot chime.
Run:  python tools/generate_sounds.py   (needs numpy)"""
import wave, numpy as np, os
SR = 44100
OUT = os.path.join(os.path.dirname(__file__), "..", "GameIndex", "Assets", "Sounds")

def t(d): return np.linspace(0, d, int(SR * d), endpoint=False)

def env(n, a, r, curve=2.0):
    e = np.ones(n); na, nr = int(a * SR), int(r * SR)
    if na: e[:na] = np.linspace(0, 1, na) ** curve
    if nr: e[-nr:] *= np.linspace(1, 0, nr) ** curve
    return e

def pad(freq, d, amp, attack, release):
    x = t(d); s = np.zeros_like(x)
    for det, w in ((-0.004, .5), (0.0, 1.0), (0.004, .5)):
        f = freq * (1 + det)
        s += w * (np.sin(2 * np.pi * f * x) + 0.25 * np.sin(2 * np.pi * 2 * f * x + 0.7))
    s *= 1 + 0.08 * np.sin(2 * np.pi * 0.35 * x)                # slow shimmer
    return amp * s * env(len(x), attack, release)

def bell(freq, d, amp):
    x = t(d); s = np.zeros_like(x)
    for ratio, w, dec in ((1, 1.0, 3.2), (2.0, .45, 4.5), (2.76, .3, 6.0), (5.4, .12, 9.0)):
        s += w * np.sin(2 * np.pi * freq * ratio * x) * np.exp(-x * dec)
    return amp * s * env(len(x), 0.004, 0.02)

def place(buf, sig, start, pan=0.0):
    i = int(start * SR); n = min(len(sig), buf.shape[1] - i)
    if n <= 0: return
    buf[0, i:i + n] += sig[:n] * (1 - max(0, pan) * .6)
    buf[1, i:i + n] += sig[:n] * (1 + min(0, pan) * .6)

def reverb(buf, mix=0.32):
    out = buf.copy()
    for d, g in ((0.071, .38), (0.109, .33), (0.151, .28), (0.197, .22), (0.263, .16)):
        n = int(d * SR)
        for ch, off in ((0, 0), (1, 37)):
            sh = np.zeros(buf.shape[1]); sh[n + off:] = buf[ch, :buf.shape[1] - n - off]
            out[ch] += mix * g * sh
    return out

def finish(buf, peak, fade_out):
    buf = reverb(buf)
    n = buf.shape[1]; buf[:, -int(fade_out * SR):] *= np.linspace(1, 0, int(fade_out * SR))
    buf *= peak / np.max(np.abs(buf))
    return buf

def save(name, buf):
    data = (np.clip(buf, -1, 1) * 32767).astype("<i2").T.copy()
    os.makedirs(OUT, exist_ok=True)
    with wave.open(os.path.join(OUT, name), "wb") as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR); w.writeframes(data.tobytes())

def note(n): return 440.0 * 2 ** ((n - 69) / 12)   # MIDI -> Hz

# ---- startup: ~2.6 s, rising airy chord + glassy sparkles ----
D = 2.6; b = np.zeros((2, int(SR * (D + 0.8))))
for midi, start, amp in ((38, 0.00, .50), (50, 0.10, .32), (57, 0.25, .30), (62, 0.40, .30), (66, 0.55, .26), (69, 0.70, .24), (76, 0.90, .14)):
    place(b, pad(note(midi), D - start, amp, 0.75, 1.2), start)
for i, (midi, st) in enumerate(((81, .50), (86, .72), (90, .95), (93, 1.18), (98, 1.42), (93, 1.70))):
    place(b, bell(note(midi), 1.4, .22), st, pan=(-1) ** i * .7)
save("startup.wav", finish(b, 0.55, 0.9))

# ---- open: soft rising two-note chime ----
b = np.zeros((2, int(SR * 1.1)))
place(b, bell(note(81), .8, .5), 0.00, -.3); place(b, bell(note(88), .9, .45), 0.09, .3)
place(b, pad(note(57), .8, .10, .05, .5), 0.0)
save("open.wav", finish(b, 0.40, 0.4))

# ---- close: soft falling two-note chime ----
b = np.zeros((2, int(SR * 1.0)))
place(b, bell(note(88), .7, .45), 0.00, .3); place(b, bell(note(81), .8, .40), 0.09, -.3)
save("close.wav", finish(b, 0.32, 0.4))
print("sounds written to", os.path.abspath(OUT))
