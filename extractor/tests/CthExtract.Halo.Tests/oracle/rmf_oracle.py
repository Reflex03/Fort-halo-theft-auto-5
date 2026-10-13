"""Independent oracle for MeshBuilder's transform chain (critique D-H1).

usage: python3 -I rmf_oracle.py <dir containing the Reclaimer RMF importer package "Reclaimer"> <scene.rmf>

Reads an RMF file written by Reclaimer's own SceneWriter with Reclaimer's own pure-Python reader (the one the Halo
field note used through Blender) and prints, as JSON, every triangle of every region/permutation in Halo world units:
vertex · mesh vertex_transform (decompression) · permutation transform · placement transform, and
uv · texture_transform; i.e. what the Blender/3ds Max importers place in the scene before their own axis/unit
conversion. Uses only the reader and MeshParams (index unstripping); nothing from Blender.
"""
import json
import sys

sys.path.insert(0, sys.argv[1])
from Reclaimer.src.SceneReader import SceneReader  # noqa: E402
from Reclaimer.src.ViewportInterface import MeshParams  # noqa: E402


def mul(a, b):
    return tuple(tuple(sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)) for i in range(4))


def apply(v, m):
    x, y, z = v
    return tuple(x * m[0][j] + y * m[1][j] + z * m[2][j] + m[3][j] for j in range(3))


def apply_uv(v, m):
    u, w = v[0], v[1]
    return (u * m[0][0] + w * m[1][0] + m[3][0], u * m[0][1] + w * m[1][1] + m[3][1])


def placements(group):
    for p in group.child_objects:
        yield p
    for g in group.child_groups:
        yield from placements(g)


scene = SceneReader.open_scene(sys.argv[2])
out = []
for placement in placements(scene.root_node):
    model = placement.object
    for region in model.regions:
        for perm in region.permutations:
            world = mul(perm.transform, placement.transform)
            for mi in range(perm.mesh_index, perm.mesh_index + perm.mesh_count):
                mesh = model.meshes[mi]
                params = MeshParams(scene, mesh, None, (0, mi, -1), "oracle")
                full = mul(params.vertex_transform, world)
                pos = params.vertex_buffer.position_channels[0]
                uvs = params.vertex_buffer.texcoord_channels[0] if params.vertex_buffer.texcoord_channels else None
                tris = []
                for t in params.chain_triangles():
                    tri = []
                    for i in t:
                        p = apply(tuple(pos[i])[:3], full)
                        uv = apply_uv(tuple(uvs[i]), params.texture_transform) if uvs is not None else (0.0, 0.0)
                        tri.append([p[0], p[1], p[2], uv[0], uv[1]])
                    tris.append(tri)
                out.append({"region": region.name, "permutation": perm.name, "mesh": mi, "triangles": tris})
print(json.dumps(out))
