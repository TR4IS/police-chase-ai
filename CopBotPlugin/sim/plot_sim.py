"""Draw a simulation run: map on the left, speed/gap/steering over time on the right.
Usage: plot_sim.py run.csv out.png [t_from t_to]"""
import csv
import sys

from PIL import Image, ImageDraw

path, out = sys.argv[1], sys.argv[2]
t_from = float(sys.argv[3]) if len(sys.argv) > 3 else 0
t_to = float(sys.argv[4]) if len(sys.argv) > 4 else 1e9

rows = list(csv.DictReader(open(path)))
units = sum(1 for k in rows[0] if k.startswith('x'))
COLORS = [(230, 60, 60), (60, 110, 240), (240, 160, 30), (160, 60, 220), (30, 170, 150)]


def num(r, k):
    v = r.get(k, '')
    return float(v) if v not in ('', None) else None


rows = [r for r in rows if t_from <= float(r['t']) <= t_to]
W, H = 1500, 900
img = Image.new('RGB', (W, H), 'white')
d = ImageDraw.Draw(img)

# Map, equal scale
xs = [float(r['px']) for r in rows]
zs = [float(r['pz']) for r in rows]
for u in range(units):
    for r in rows:
        x, z = num(r, f'x{u}'), num(r, f'z{u}')
        if x is not None:
            xs.append(x)
            zs.append(z)
minx, maxx, minz, maxz = min(xs), max(xs), min(zs), max(zs)
scale = min(760 / max(maxx - minx, 1), 860 / max(maxz - minz, 1))
mx = lambda x: 20 + (x - minx) * scale
mz = lambda z: 880 - (z - minz) * scale

line = [(mx(float(r['px'])), mz(float(r['pz']))) for r in rows]
d.line(line, fill=(40, 40, 40), width=5)
for u in range(units):
    pts = [(mx(num(r, f'x{u}')), mz(num(r, f'z{u}'))) for r in rows if num(r, f'x{u}') is not None]
    if len(pts) > 1:
        d.line(pts, fill=COLORS[u % 5], width=1)
d.text((20, 6), f'{path.split("/")[-1].split(chr(92))[-1]}  black=player  scale {scale:.2f}px/m', fill='black')


def chart(top, height, label, series, lo=None, hi=None):
    left, right = 800, W - 10
    d.rectangle((left, top, right, top + height), outline=(180, 180, 180))
    d.text((left + 4, top + 2), label, fill='black')
    vals = [v for _, s, _ in series for v in s if v is not None]
    if not vals:
        return
    a = lo if lo is not None else min(vals)
    b = hi if hi is not None else max(vals)
    b = max(b, a + 0.001)
    if a < 0 < b:
        y0 = top + height - (0 - a) / (b - a) * height
        d.line((left, y0, right, y0), fill=(210, 210, 210))
    d.text((right - 60, top + 2), f'{b:.1f}', fill=(120, 120, 120))
    d.text((right - 60, top + height - 12), f'{a:.1f}', fill=(120, 120, 120))
    ts = [float(r['t']) for r in rows]
    t0, t1 = ts[0], ts[-1]
    for color, s, w in series:
        pts = []
        for t, v in zip(ts, s):
            if v is None:
                continue
            pts.append((left + (t - t0) / max(t1 - t0, 0.01) * (right - left), top + height - (min(max(v, a), b) - a) / (b - a) * height))
        if len(pts) > 1:
            d.line(pts, fill=color, width=w)


speed = [((40, 40, 40), [float(r['pv']) * 3.6 for r in rows], 2)]
gap, accel, wheel, lat = [], [], [], []
for u in range(units):
    c = COLORS[u % 5]
    speed.append((c, [None if num(r, f'v{u}') is None else num(r, f'v{u}') * 3.6 for r in rows], 1))
    gap.append((c, [num(r, f'g{u}') for r in rows], 1))
    accel.append((c, [num(r, f'a{u}') for r in rows], 1))
    wheel.append((c, [num(r, f'w{u}') for r in rows], 1))
    lat.append((c, [num(r, f'l{u}') for r in rows], 1))
chart(10, 170, 'speed km/h', speed)
chart(190, 170, 'route gap to player m', gap, -30, 150)
chart(370, 170, 'accel m/s2', accel, -10, 6)
chart(550, 170, 'wheel angle rad', wheel, -0.45, 0.45)
chart(730, 160, 'lateral offset m', lat, -6, 6)
img.save(out)
