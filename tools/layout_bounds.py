"""Print a converted layout's overall bounds and each part's bounds (Unity axes, metres, EverRail size).

usage: py -I tools/layout_bounds.py <cache dir> <layout id>
"""
import json
import pathlib
import struct
import sys


def mesh_bounds(path):
    data = path.read_bytes()
    vc, = struct.unpack_from("<i", data, 4)
    lo, hi = [1e9] * 3, [-1e9] * 3
    for i in range(vc):
        p = struct.unpack_from("<3f", data, 12 + i * 32)
        lo = [min(a, b) for a, b in zip(lo, p)]
        hi = [max(a, b) for a, b in zip(hi, p)]
    return lo, hi


def main():
    cache, lay = pathlib.Path(sys.argv[1]), sys.argv[2]
    j = json.loads((cache / "layouts" / f"{lay}.json").read_text())
    total_lo, total_hi = [1e9] * 3, [-1e9] * 3
    for part in j["parts"]:
        m = part["Matrix"]
        r, u, f, o = m[0:3], m[3:6], m[6:9], m[9:12]
        lo, hi = mesh_bounds(cache / "meshes" / "native" / f"{part['Mesh']}.7rm")
        plo, phi = [1e9] * 3, [-1e9] * 3
        for cx in (lo[0], hi[0]):
            for cy in (lo[1], hi[1]):
                for cz in (lo[2], hi[2]):
                    w = [o[k] + r[k] * cx + u[k] * cy + f[k] * cz for k in range(3)]
                    plo = [min(a, b) for a, b in zip(plo, w)]
                    phi = [max(a, b) for a, b in zip(phi, w)]
        total_lo = [min(a, b) for a, b in zip(total_lo, plo)]
        total_hi = [max(a, b) for a, b in zip(total_hi, phi)]
        print(f"{part['Mesh']:34s} x {plo[0]:7.2f}..{phi[0]:7.2f}  y {plo[1]:6.2f}..{phi[1]:6.2f}  z {plo[2]:7.2f}..{phi[2]:7.2f}")
    print(f"{'TOTAL':34s} x {total_lo[0]:7.2f}..{total_hi[0]:7.2f}  y {total_lo[1]:6.2f}..{total_hi[1]:6.2f}  z {total_lo[2]:7.2f}..{total_hi[2]:7.2f}")


if __name__ == "__main__":
    main()
