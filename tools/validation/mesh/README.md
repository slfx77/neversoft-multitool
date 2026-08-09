# Mesh validation

This directory contains reusable GLB inspectors, material snapshot diffs,
Blender render checks, and the THAW PS2 texture regression harness.

Most inspectors use only Python 3. Install Pillow and NumPy for texture/image
and accessor checks. The `render_*.py` and `glb_render_angles.py` scripts run
inside Blender and require its bundled `bpy` module.

Minimal verification from the repository root:

```powershell
python tools/validation/mesh/analyze_glb_geometry.py --help
python tools/validation/mesh/glb_material_diff_sweep.py --help
```

Pass an explicit GLB or corpus path to each tool. The material diff reports all
changes; `diff --fail-on-diff` provides an exact-equality gate. Generated CSVs,
renders, and extracted corpora belong under `TestOutput/`.
