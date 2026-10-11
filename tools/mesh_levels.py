"""Print the floor (upward-facing) and ceiling (downward-facing) surface area of a .7rm mesh by height.

usage: py -I tools/mesh_levels.py <mesh.7rm>
"""
import collections
import struct
import sys


def main():
    data = open(sys.argv[1], "rb").read()
    assert data[:4] in (b"7RM1", b"7RM2"), "not a 7rm mesh"
    v2 = data[:4] == b"7RM2"
    vc, sc = struct.unpack_from("<ii", data, 4)
    off = 12
    pos = []
    for _ in range(vc):
        pos.append(struct.unpack_from("<3f", data, off))
        off += 32
    up, down = collections.Counter(), collections.Counter()
    for _ in range(sc):
        for _ in range(2):  # slot, texture (.NET 7-bit length prefixed strings)
            n, shift = 0, 0
            while True:
                b = data[off]; off += 1
                n |= (b & 0x7F) << shift; shift += 7
                if b < 0x80:
                    break
            off += n
        off += 16 + (1 if v2 else 0)
        ic, = struct.unpack_from("<i", data, off); off += 4
        idx = struct.unpack_from(f"<{ic}i", data, off); off += 4 * ic
        for t in range(0, ic, 3):
            a, b, c = (pos[i] for i in idx[t:t + 3])
            ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
            vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
            nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
            area = (nx * nx + ny * ny + nz * nz) ** 0.5 / 2
            if area == 0:
                continue
            h = round((a[1] + b[1] + c[1]) / 3 * 4) / 4
            if ny / (2 * area) > 0.9:
                up[h] += area
            elif ny / (2 * area) < -0.9:
                down[h] += area
    print("height  up-facing m2  down-facing m2")
    for h in sorted(set(up) | set(down)):
        if up[h] > 2 or down[h] > 2:
            print(f"{h:6.2f}  {up[h]:12.1f}  {down[h]:12.1f}")


if __name__ == "__main__":
    main()
