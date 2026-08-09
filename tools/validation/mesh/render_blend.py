# Render one framed viewport image of a .blend's model at a given clip+frame,
# for before/after visual comparison of the PSX Blender export. Workbench engine
# with textured shading (the game materials are unlit emission) — fast, no light
# rig needed. Camera auto-frames the deformed bounds so before (huge) and after
# (metres) both fill the frame.
#
# Run: blender -b --factory-startup --python render_blend.py -- <blend> <clip_substr> <frame> <out.png>
import bpy, sys, os
from mathutils import Vector


def deformed_bounds():
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
    argv = sys.argv[sys.argv.index("--") + 1:]
    path, clip_sub, frame_s, out = argv[0], argv[1], int(argv[2]), argv[3]
    bpy.ops.wm.open_mainfile(filepath=path)
    scene = bpy.context.scene
    arm = next((o for o in scene.objects if o.type == "ARMATURE"), None)
    act = next((a for a in bpy.data.actions if clip_sub.lower() in a.name.lower()), None)
    if arm and act:
        ad = arm.animation_data or arm.animation_data_create()
        ad.action = act
        try:
            if hasattr(act, "slots") and len(act.slots):
                ad.action_slot = act.slots[0]
        except Exception:
            pass
    scene.frame_set(frame_s)

    lo, hi = deformed_bounds()
    center = (lo + hi) / 2.0
    size = max(max(hi - lo), 1e-4)

    cam_data = bpy.data.cameras.new("Cam")
    cam = bpy.data.objects.new("Cam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    d = size * 1.7
    cam.location = center + Vector((d * 0.35, -d, d * 0.25))
    cam.rotation_euler = (center - cam.location).to_track_quat("-Z", "Y").to_euler()
    cam_data.clip_start = max(size / 1000.0, 1e-4)
    cam_data.clip_end = d * 20.0

    scene.render.engine = "BLENDER_WORKBENCH"
    sh = scene.display.shading
    sh.light = "STUDIO"
    sh.color_type = "TEXTURE"
    sh.show_shadows = False
    scene.render.resolution_x = 760
    scene.render.resolution_y = 900
    scene.render.filepath = out
    bpy.ops.render.render(write_still=True)
    print(f"RENDERED {os.path.basename(out)} size={size:.2f} center=({center.x:.2f},{center.y:.2f},{center.z:.2f})")


main()
