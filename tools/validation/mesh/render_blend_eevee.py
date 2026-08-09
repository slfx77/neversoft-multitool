# Eevee-render a PSX-exported .blend (or a GLB imported into a fresh scene)
# with an auto-framed front camera, for A/B brightness comparison against the
# app's glb-render output. Workbench ignores node graphs (see memory lesson),
# so this uses Eevee, which evaluates the real material node trees.
#
# Run: blender -b --factory-startup --python render_blend_eevee.py -- <file> <out.png> [view_transform]
#   view_transform: Standard (default) | AgX | Filmic
import bpy, sys, os
from mathutils import Vector


def load(path):
    if os.path.splitext(path)[1].lower() == ".blend":
        bpy.ops.wm.open_mainfile(filepath=path)
    else:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.gltf(filepath=path)


def scene_bounds():
    dg = bpy.context.evaluated_depsgraph_get()
    lo, hi = Vector((1e30,) * 3), Vector((-1e30,) * 3)
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        ev = obj.evaluated_get(dg)
        me = ev.to_mesh()
        mw = ev.matrix_world
        for v in me.vertices:
            w = mw @ v.co
            for i in range(3):
                lo[i] = min(lo[i], w[i]); hi[i] = max(hi[i], w[i])
        ev.to_mesh_clear()
    return lo, hi


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    path, out_png = argv[0], argv[1]
    view_transform = argv[2] if len(argv) > 2 else "Standard"
    load(path)
    scene = bpy.context.scene

    lo, hi = scene_bounds()
    center = (lo + hi) / 2
    size = max(hi - lo)

    cam_data = bpy.data.cameras.new("ab_cam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = size * 1.15
    cam = bpy.data.objects.new("ab_cam", cam_data)
    bpy.context.collection.objects.link(cam)
    # Front view: -Y in Blender Z-up (both the .blend export and the glTF
    # importer put the model facing -Y after the Y-up -> Z-up lift).
    cam.location = center + Vector((0.0, -size * 3.0, 0.0))
    cam.rotation_euler = (1.5707963, 0.0, 0.0)
    scene.camera = cam

    # A sun so lit (non-emission) materials from the glTF importer are visible.
    # The exported .blend's unlit emission shaders ignore it.
    sun_data = bpy.data.lights.new("ab_sun", type="SUN")
    sun_data.energy = 3.0
    sun = bpy.data.objects.new("ab_sun", sun_data)
    bpy.context.collection.objects.link(sun)
    sun.rotation_euler = (0.9, 0.0, -0.4)

    if scene.world is None:
        scene.world = bpy.data.worlds.new("ab_world")
    scene.world.use_nodes = True
    bg = scene.world.node_tree.nodes.get("Background")
    if bg:
        bg.inputs[0].default_value = (0.239, 0.239, 0.239, 1.0)
        bg.inputs[1].default_value = 1.0

    for engine in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
        try:
            scene.render.engine = engine
            break
        except Exception:
            continue
    scene.view_settings.view_transform = view_transform
    scene.view_settings.look = "None"
    scene.render.resolution_x = 512
    scene.render.resolution_y = 512
    scene.render.filepath = out_png
    scene.render.image_settings.file_format = "PNG"
    bpy.ops.render.render(write_still=True)
    print("RENDER_DONE " + out_png)


main()
