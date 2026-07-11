#!/usr/bin/env python3
"""Verify a PSX-animated GLB against engine ground truth + cross-part seam closure.

Reads the emitted glTF EXACTLY as a renderer would (node TRS + skin joints),
computes per-joint world transforms and skinned vertex world positions at a
given animation time, then:

  * diffs per-joint world rotation/origin against a decomp ground-truth JSON
    (frames -> [{slot, rot 3x3 /4096, worldOrigin|PoseT}]); and
  * measures cross-part "seam growth": triangle edges whose two vertices bind
    to DIFFERENT joints must keep their bind length under animation (stitched
    verts follow their source part). Growth >> 0 = wrong joint transform OR
    wrong stitch-source assignment.

The PSX->glTF axis map is (x,-y,-z); ground-truth rot is mapped R_gltf = M R M,
origin T_gltf = M (T/divisor).

Usage:
  python tools/diagnostics/psx_anim_verify.py <file.glb> \
      [--gt ground_truth.json] [--divisor 36] [--seam-frames 0,1] [--fps 30]
"""
import argparse
import json
import struct
import sys

import numpy as np

M = np.diag([1.0, -1.0, -1.0])
COMP = {5126: ('f', 4), 5123: ('H', 2), 5121: ('B', 1), 5125: ('I', 4)}
NCOMP = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}


def load_glb(path):
    with open(path, 'rb') as f:
        f.read(12)
        clen, _ = struct.unpack('<II', f.read(8))
        doc = json.loads(f.read(clen).decode().rstrip('\x00'))
        blen, _ = struct.unpack('<II', f.read(8))
        blob = f.read(blen)
    return doc, blob


def accessor(doc, blob, idx):
    acc = doc['accessors'][idx]
    bv = doc['bufferViews'][acc['bufferView']]
    base = bv.get('byteOffset', 0) + acc.get('byteOffset', 0)
    fmt, csz = COMP[acc['componentType']]
    n = NCOMP[acc['type']]
    stride = bv.get('byteStride') or csz * n
    out = np.empty((acc['count'], n))
    for i in range(acc['count']):
        out[i] = struct.unpack_from('<' + fmt * n, blob, base + i * stride)
    return out


def quat_to_mat(q):
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


class Scene:
    def __init__(self, glb):
        self.doc, self.blob = load_glb(glb)
        skin = self.doc['skins'][0]
        self.joints = skin['joints']
        self.n2b = {n: i for i, n in enumerate(self.joints)}
        self.nb = len(self.joints)
        self.bind_local = np.array(
            [self.doc['nodes'][n].get('translation', [0, 0, 0]) for n in self.joints])
        self.bind_rot = []
        for n in self.joints:
            r = self.doc['nodes'][n].get('rotation', [0, 0, 0, 1])
            self.bind_rot.append(quat_to_mat(r))
        node_parent = {}
        for i, nd in enumerate(self.doc['nodes']):
            for c in nd.get('children', []):
                node_parent[c] = i
        self.parents = []
        for n in self.joints:
            p = node_parent.get(n)
            self.parents.append(self.n2b.get(p, -1) if p is not None else -1)
        anim = self.doc['animations'][0]
        self.rc, self.tc = {}, {}
        for ch in anim['channels']:
            b = self.n2b.get(ch['target']['node'])
            if b is None:
                continue
            s = anim['samplers'][ch['sampler']]
            pair = (accessor(self.doc, self.blob, s['input']).flatten(),
                    accessor(self.doc, self.blob, s['output']))
            path = ch['target']['path']
            (self.rc if path == 'rotation' else self.tc if path == 'translation' else {})[b] = pair

    def world(self, t):
        Rw = [None] * self.nb
        Tw = [None] * self.nb

        def build(b):
            if Rw[b] is not None:
                return
            R = quat_to_mat(self.rc[b][1][int(np.argmin(np.abs(self.rc[b][0] - t)))]) \
                if b in self.rc else self.bind_rot[b]
            T = np.array(self.tc[b][1][int(np.argmin(np.abs(self.tc[b][0] - t)))]) \
                if b in self.tc else self.bind_local[b]
            p = self.parents[b]
            if p >= 0:
                build(p)
                Rw[b] = Rw[p] @ R
                Tw[b] = Rw[p] @ T + Tw[p]
            else:
                Rw[b], Tw[b] = R, T
        for b in range(self.nb):
            build(b)
        return Rw, Tw

    def skinned_vertices(self):
        P, J, tris, off = [], [], [], 0
        for mesh in self.doc['meshes']:
            for prim in mesh['primitives']:
                att = prim['attributes']
                if 'JOINTS_0' not in att:
                    continue
                pos = accessor(self.doc, self.blob, att['POSITION'])
                jnt = accessor(self.doc, self.blob, att['JOINTS_0'])
                wts = accessor(self.doc, self.blob, att['WEIGHTS_0'])
                idx = accessor(self.doc, self.blob, prim['indices']).astype(int).flatten()
                J.append(jnt[np.arange(len(jnt)), wts.argmax(axis=1)].astype(int))
                P.append(pos)
                tris.append(idx.reshape(-1, 3) + off)
                off += len(pos)
        return np.vstack(P), np.concatenate(J), np.vstack(tris)


def diff_gt(scene, gt, divisor, origin_key, fps):
    for fstr in sorted(gt['frames'], key=int):
        Rw, Tw = scene.world(int(fstr) / fps)
        wr = wt = 0.0
        for s in gt['frames'][fstr]:
            p = s['slot']
            if p >= scene.nb:
                continue
            R_gt = M @ (np.array(s['rot'], float) / 4096.0) @ M
            T_gt = M @ (np.array(s[origin_key], float) / divisor)
            wr = max(wr, np.degrees(np.arccos(np.clip((np.trace(Rw[p].T @ R_gt) - 1) / 2, -1, 1))))
            wt = max(wt, float(np.linalg.norm(Tw[p] - T_gt)))
        print(f"  GT frame {fstr:>3}: worst joint rot={wr:7.3f} deg  origin={wt:7.3f} units")


def seam(scene, frames, fps):
    P, J, T3 = scene.skinned_vertices()
    edges = set()
    for a, b, c in T3:
        for u, v in ((a, b), (b, c), (c, a)):
            if J[u] != J[v]:
                edges.add((min(u, v), max(u, v)))
    edges = list(edges)
    bl = np.array([np.linalg.norm(P[u] - P[v]) for u, v in edges])
    for f in frames:
        Rw, Tw = scene.world(f / fps)
        W = np.empty_like(P)
        for i in range(len(P)):
            j = J[i]
            W[i] = Rw[j] @ (P[i] - scene.bind_local[j]) + Tw[j]
        g = np.array([np.linalg.norm(W[u] - W[v]) for u, v in edges]) - bl
        print(f"  seam frame {f:>3}: mean={g.mean():7.2f} p90={np.percentile(g, 90):7.2f} "
              f"max={g.max():7.2f} (edges={len(edges)})")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('glb')
    ap.add_argument('--gt')
    ap.add_argument('--divisor', type=float, default=36.0)
    ap.add_argument('--seam-frames', default='0,1')
    ap.add_argument('--fps', type=float, default=30.0)
    args = ap.parse_args()

    scene = Scene(args.glb)
    print(args.glb)
    if args.gt:
        gt = json.load(open(args.gt))
        sample = gt['frames'][next(iter(gt['frames']))][0]
        origin_key = 'worldOrigin' if 'worldOrigin' in sample else 'PoseT'
        diff_gt(scene, gt, args.divisor, origin_key, args.fps)
    frames = [int(x) for x in args.seam_frames.split(',') if x != '']
    if frames:
        seam(scene, frames, args.fps)


if __name__ == '__main__':
    main()
