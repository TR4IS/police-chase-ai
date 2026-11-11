"""Print a run as a table over time. Usage: sim_table.py run.csv [every_seconds] [t_from] [t_to]"""
import csv
import sys

path = sys.argv[1]
every = float(sys.argv[2]) if len(sys.argv) > 2 else 5
t_from = float(sys.argv[3]) if len(sys.argv) > 3 else 0
t_to = float(sys.argv[4]) if len(sys.argv) > 4 else 1e9
rows = list(csv.DictReader(open(path)))
units = sum(1 for k in rows[0] if k.startswith('x'))


def num(r, k):
    v = r.get(k, '')
    return float(v) if v not in ('', None) else None


next_t = t_from
print('    t  player | ' + ' | '.join(f'u{u}: km/h  gap  acc  lat' for u in range(units)))
for r in rows:
    t = float(r['t'])
    if t < next_t or t > t_to:
        continue
    next_t = t + every
    line = f"{t:5.1f} {float(r['pv']) * 3.6:7.0f} | "
    parts = []
    for u in range(units):
        v = num(r, f'v{u}')
        if v is None:
            parts.append(' ' * 22)
            continue
        parts.append(f"{v * 3.6:7.0f} {num(r, f'g{u}'):5.0f} {num(r, f'a{u}'):5.1f} {num(r, f'l{u}'):4.1f}")
    print(line + ' | '.join(parts))
