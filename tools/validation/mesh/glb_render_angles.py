"""Eevee-render a GLB from several angles into one contact sheet.

Written because flat/front-only checks have repeatedly passed on models that
were inside-out, rainbow-coloured or depth-sorted wrong -- the defects only
show textured, in perspective, from an angle. Eevee honours the glTF importer's
real alpha modes (OPAQUE / MASK writes depth, BLEND does not), which a
Workbench or wireframe pass does not.

Run:
    blender -b --factory-startup --python glb_render_angles.py -- <in.glb> <out.png> [size] [views]

Renders front / three-quarter / side / top-three-quarter into a 2x2 grid.
`views` overrides the angles as "az,el;az,el;..." (e.g. "0,89" for top-down).
"""

import math
import os
import sys

import bpy
from mathutils import Vector

# (azimuth, elevation) in degrees, orbiting the model's centre.
VIEWS = [(0, 0), (35, 15), (85, 5), (35, 55)]


def load(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=path)


def scene_bounds():
    """World bounds from each object's own bound_box.

    Deliberately NOT via evaluated_get()/to_mesh(): in background mode the
    depsgraph is not necessarily evaluated after an import, and a 555-object
    level came back with no bounds at all, which framed the camera on a point
    and rendered a blank background.
    """
    lo, hi = Vector((1e30,) * 3), Vector((-1e30,) * 3)
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        for corner in obj.bound_box:
            world = obj.matrix_world @ Vector(corner)
            for axis in range(3):
                lo[axis] = min(lo[axis], world[axis])
                hi[axis] = max(hi[axis], world[axis])
    return lo, hi


def setup_scene(size):
    scene = bpy.context.scene
    sun_data = bpy.data.lights.new("angle_sun", type="SUN")
    sun_data.energy = 3.0
    sun = bpy.data.objects.new("angle_sun", sun_data)
    bpy.context.collection.objects.link(sun)
    sun.rotation_euler = (0.9, 0.0, -0.4)

    if scene.world is None:
        scene.world = bpy.data.worlds.new("angle_world")
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes.get("Background")
    if background:
        background.inputs[0].default_value = (0.14, 0.15, 0.17, 1.0)

    for engine in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
        try:
            scene.render.engine = engine
            break
        except Exception:  # noqa: BLE001 - engine name varies by Blender version
            continue
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.render.resolution_x = size
    scene.render.resolution_y = size
    scene.render.image_settings.file_format = "PNG"
    return scene


def place_camera(scene, centre, extent, azimuth, elevation):
    camera_data = bpy.data.cameras.new("angle_cam")
    camera_data.type = "PERSP"
    camera_data.lens = 50.0
    radius = extent * 2.4
    # Game units, not metres: a level spans ~14,000, so Blender's default
    # 0.1..100 clip range puts the whole scene behind the far plane and renders
    # a blank background.
    camera_data.clip_start = max(extent * 1e-4, 1e-3)
    camera_data.clip_end = radius * 8.0
    camera = bpy.data.objects.new("angle_cam", camera_data)
    bpy.context.collection.objects.link(camera)
    az, el = math.radians(azimuth), math.radians(elevation)
    offset = Vector((
        math.sin(az) * math.cos(el),
        -math.cos(az) * math.cos(el),
        math.sin(el),
    )) * radius
    camera.location = centre + offset

    direction = (centre - camera.location).normalized()
    camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    scene.camera = camera
    return camera


def contact_sheet(tiles, out_path, size):
    """Compose the rendered tiles into a 2x2 grid using Blender's own image API."""
    grid = bpy.data.images.new("sheet", width=size * 2, height=size * 2, alpha=False)
    buffer = [0.0] * (size * 2 * size * 2 * 4)
    for index, tile_path in enumerate(tiles):
        tile = bpy.data.images.load(tile_path)
        pixels = list(tile.pixels)
        col, row = index % 2, 1 - index // 2
        for y in range(size):
            src = y * size * 4
            dst = ((row * size + y) * size * 2 + col * size) * 4
            buffer[dst:dst + size * 4] = pixels[src:src + size * 4]
        bpy.data.images.remove(tile)
    grid.pixels = buffer
    grid.filepath_raw = out_path
    grid.file_format = "PNG"
    grid.save()


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    glb_path = os.path.abspath(argv[0])
    out_png = os.path.abspath(argv[1])
    size = int(argv[2]) if len(argv) > 2 else 400
    views = VIEWS
    if len(argv) > 3:
        views = [tuple(float(v) for v in spec.split(",")) for spec in argv[3].split(";")]
    os.makedirs(os.path.dirname(out_png), exist_ok=True)

    load(glb_path)
    scene = setup_scene(size)
    lo, hi = scene_bounds()
    centre = (lo + hi) / 2
    extent = max(max(hi - lo), 1e-4)

    tiles = []
    for index, (azimuth, elevation) in enumerate(views):
        place_camera(scene, centre, extent, azimuth, elevation)
        tile_path = f"{os.path.splitext(out_png)[0]}_view{index}.png"
        scene.render.filepath = tile_path
        bpy.ops.render.render(write_still=True)
        tiles.append(tile_path)

    if len(tiles) == 1:
        os.replace(tiles[0], out_png)
    else:
        contact_sheet(tiles, out_png, size)
        for tile_path in tiles:
            os.remove(tile_path)
    print("RENDER_DONE " + out_png)


main()
