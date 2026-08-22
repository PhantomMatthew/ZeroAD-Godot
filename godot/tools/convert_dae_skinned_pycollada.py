#!/usr/bin/env python3
"""DAE→GLB 直转器(蒙皮网格专用,pycollada 纯 Python,无需 Blender)。

本机 Blender 无 Collada 时的骨骼网格兜底:读 DAE 的 skin 控制器
(关节名/逆绑定矩阵/顶点权重)+ 场景关节局部矩阵,产出 glTF skin。
顶点用裸坐标(C++ CommonConvert 语义,不应用 <unit>)。

用法: python3 convert_dae_skinned_pycollada.py <in.dae> <out.glb> [...]
"""
import json, struct, sys
import collada
import numpy as np

CT = {np.dtype('float32'): 5126, np.dtype('uint16'): 5123, np.dtype('uint32'): 5125}

def convert(dae_path, glb_path):
    d = collada.Collada(dae_path, ignore=[collada.DaeBrokenRefError, collada.DaeMalformedError, collada.DaeIncompleteError])
    sk = d.controllers[0]
    pr = sk.geometry.primitives[0]
    verts = np.array(pr.vertex, dtype=np.float32)
    idx = np.array(pr.vertex_index, dtype=np.uint32).flatten()
    normals = None
    if pr.normal is not None and pr.normal_index is not None:
        normals = np.array(pr.normal, dtype=np.float32)[np.array(pr.normal_index, dtype=np.int64).flatten()]

    joints_flat = list(sk.weight_joints)
    bind_src = sk.sourcebyid[sk.joint_matrix_source]
    ibm = np.array(bind_src.data, dtype=np.float32).reshape(-1, 4, 4)

    node_mats = {}
    def walk(n):
        nid = getattr(n, 'id', None)
        if nid:
            m = getattr(n, 'matrix', None)
            node_mats[nid] = np.array(m, dtype=np.float32).reshape(4, 4) if m is not None else np.eye(4, dtype=np.float32)
        for c in getattr(n, 'children', []):
            walk(c)
    for sn in d.scene.nodes:
        walk(sn)

    nodes = [{'name': 'root', 'children': []}]
    joint_node_idx = {}
    for jn in joints_flat:
        nid = jn if jn in node_mats else next((c for c in node_mats if c.endswith(jn)), None)
        local = node_mats.get(nid, np.eye(4, dtype=np.float32))
        n_idx = len(nodes)
        nodes.append({'name': jn, 'matrix': local.flatten().tolist()})
        nodes[0]['children'].append(n_idx)
        joint_node_idx[jn] = n_idx

    vwi = sk.vertex_weight_index
    vcounts = list(sk.vcounts)
    weights_all = np.array([float(np.atleast_1d(w)[0]) for w in sk.weights], dtype=np.float32)
    joints0 = np.zeros((len(verts), 4), dtype=np.uint16)
    weights0 = np.zeros((len(verts), 4), dtype=np.float32)
    p = 0
    for vi, cnt in enumerate(vcounts):
        for k in range(min(cnt, 4)):
            joints0[vi, k] = int(vwi[p * 2]); weights0[vi, k] = weights_all[int(vwi[p * 2 + 1])]; p += 1
    s = weights0.sum(axis=1, keepdims=True); s[s == 0] = 1
    weights0 /= s

    blob = bytearray(); accessors = []; bufferViews = []
    def acc(arr, type_, mins=None, maxs=None):
        arr = np.ascontiguousarray(arr)
        off = len(blob); blob.extend(arr.tobytes())
        bufferViews.append({'buffer': 0, 'byteOffset': off, 'byteLength': arr.nbytes})
        a = {'bufferView': len(bufferViews) - 1, 'componentType': CT[arr.dtype], 'count': arr.shape[0], 'type': type_}
        if mins is not None: a['min'] = mins; a['max'] = maxs
        accessors.append(a); return len(accessors) - 1
    pos_acc = acc(verts, 'VEC3', verts.min(axis=0).tolist(), verts.max(axis=0).tolist())
    attrs = {'POSITION': pos_acc, 'JOINTS_0': acc(joints0, 'VEC4'), 'WEIGHTS_0': acc(weights0, 'VEC4')}
    if normals is not None and len(normals) == len(verts):
        attrs['NORMAL'] = acc(normals, 'VEC3')
    idx_acc = acc(idx.astype(np.uint32), 'SCALAR')

    root_mat = np.eye(4, dtype=np.float32)
    def find_ctl(n):
        if getattr(n, 'controller', None) is sk: return n
        for c in getattr(n, 'children', []):
            r = find_ctl(c)
            if r is not None: return r
    for sn in d.scene.nodes:
        cn = find_ctl(sn)
        if cn is not None:
            m = getattr(cn, 'matrix', None)
            if m is not None: root_mat = np.array(m, dtype=np.float32).reshape(4, 4)
            break

    mesh_node = len(nodes)
    nodes.append({'name': 'mesh', 'mesh': 0, 'skin': 0, 'matrix': root_mat.flatten().tolist()})
    nodes[0]['children'].append(mesh_node)
    gltf = {'asset': {'version': '2.0'}, 'scenes': [{'nodes': [0]}], 'scene': 0, 'nodes': nodes,
            'meshes': [{'primitives': [{'attributes': attrs, 'indices': idx_acc, 'mode': 4}]}],
            'skins': [{'joints': [joint_node_idx[jn] for jn in joints_flat], 'inverseBindMatrices': acc(ibm, 'MAT4')}],
            'accessors': accessors, 'bufferViews': bufferViews, 'buffers': [{'byteLength': len(blob)}]}
    payload = json.dumps(gltf, separators=(',', ':')).encode()
    payload += b' ' * ((4 - len(payload) % 4) % 4)
    binchunk = bytes(blob) + b' ' * ((4 - len(blob) % 4) % 4)
    with open(glb_path, 'wb') as f:
        f.write(struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(payload) + 8 + len(binchunk)))
        f.write(struct.pack('<II', len(payload), 0x4E4F534A)); f.write(payload)
        f.write(struct.pack('<II', len(binchunk), 0x004E4942)); f.write(binchunk)
    return f'{len(verts)}v/{len(joints_flat)}j'

if __name__ == '__main__':
    for i in range(1, len(sys.argv), 2):
        print(sys.argv[i], '->', convert(sys.argv[i], sys.argv[i + 1]))
