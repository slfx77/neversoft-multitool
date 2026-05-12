# SPDX-License-Identifier: MIT
"""Import a NeversoftMultitool stdin blend export payload and save a .blend file."""

import json
import io
import os
import struct
import sys
import zipfile

import bpy
from mathutils import Matrix, Vector


VERTEX_STRUCT = struct.Struct("<12f")
INDEX_STRUCT = struct.Struct("<i")

# Source data is glTF-style Y-up (PS2 worldzones are emitted with Y as height,
# X/Z forming the ground plane). Blender uses Z-up natively, so each imported
# node's world transform is pre-multiplied by this matrix:
#   X_blender = X_source
#   Y_blender = -Z_source
#   Z_blender = Y_source
# Equivalent to a +90 deg rotation about the X axis.
Y_UP_TO_Z_UP = Matrix((
    (1.0, 0.0, 0.0, 0.0),
    (0.0, 0.0, -1.0, 0.0),
    (0.0, 1.0, 0.0, 0.0),
    (0.0, 0.0, 0.0, 1.0),
))


def _input_args():
    if "--" in sys.argv:
        args = sys.argv[sys.argv.index("--") + 1 :]
    else:
        args = sys.argv[1:]
    if not args:
        raise RuntimeError("Missing export payload argument after --")
    return args


def _load_package():
    args = _input_args()
    if args[0] == "--stdin-zip":
        data = sys.stdin.buffer.read()
        package = zipfile.ZipFile(io.BytesIO(data), "r")
        manifest = json.loads(package.read("manifest.json").decode("utf-8"))
        return manifest, package, None

    path = args[0]
    with open(path, "r", encoding="utf-8") as handle:
        manifest = json.load(handle)
    return manifest, None, os.path.dirname(os.path.abspath(path))


def _read_package_bytes(package, package_dir, relative_path):
    if not relative_path:
        return None
    normalized = relative_path.replace("\\", "/")
    if package is not None:
        try:
            return package.read(normalized)
        except KeyError:
            return None
    path = os.path.join(package_dir, normalized.replace("/", os.sep))
    if not os.path.exists(path):
        return None
    with open(path, "rb") as handle:
        return handle.read()


def _json(value):
    return json.dumps(value, separators=(",", ":"), sort_keys=True)


def _clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()


def _ps2_alpha_fields(item):
    alpha = item.get("alpha")
    if alpha is None:
        return None
    alpha = int(alpha)
    alpha_byte = alpha & 0xFF
    return {
        "a": alpha_byte & 0x3,
        "b": (alpha_byte >> 2) & 0x3,
        "c": (alpha_byte >> 4) & 0x3,
        "d": (alpha_byte >> 6) & 0x3,
        "fix": (alpha >> 32) & 0xFF,
    }


def _ps2_material_alpha_fields(metadata):
    for item in metadata:
        if item.get("kind") != "ps2_gs":
            continue
        return _ps2_alpha_fields(item)
    return None


def _ps2_test_fields(item):
    test = item.get("test")
    if test is None:
        return None
    test = int(test)
    return {
        "ate": test & 0x1,
        "atst": (test >> 1) & 0x7,
        "aref": (test >> 4) & 0xFF,
        "afail": (test >> 12) & 0x3,
    }


def _ps2_alpha_test_discards_color(test_fields):
    if not test_fields:
        return False
    if not test_fields["ate"] or test_fields["atst"] == 1:
        return False
    return test_fields["afail"] in (0, 2)


def _ps2_alpha_mask_ref(test_fields):
    if not test_fields:
        return None
    aref = test_fields["aref"]
    if test_fields["atst"] == 6:
        aref = min(255, aref + 1)
    return aref


def _ps2_alpha_classification(item):
    """Decode the PS2 GS ALPHA register into a blend recipe.

    PS2 GS blend equation: Output = (A - B) * C / 0x80 + D.
    Field layout: A=bits[1:0], B=bits[3:2], C=bits[5:4], D=bits[7:6].
    A/B/D values: 0=Cs (source color), 1=Cd (destination color), 2=0 (zero).
    C values: 0=As (source alpha), 1=Ad (destination alpha), 2=FIX (constant).

    Common patterns:
      0x44 (A=0,B=1,C=0,D=1)  standard blend   (Cs-Cd)*As + Cd  =  Cs*As + Cd*(1-As)
      0x54 (A=0,B=1,C=1,D=1)  dst-alpha blend  (Cs-Cd)*Ad + Cd  =  Cs*Ad + Cd*(1-Ad)
      0x64 (A=0,B=1,C=2,D=1)  fixed blend     (Cs-Cd)*FIX + Cd =  Cs*(FIX/128) + Cd*(1-FIX/128)
      0x48 (A=0,B=2,C=0,D=1)  additive         (Cs-0)*As + Cd   =  Cs*As + Cd
      0x68 (A=0,B=2,C=2,D=1)  additive w/ FIX  (Cs-0)*FIX + Cd  =  Cs*FIX + Cd
      0x42 (A=2,B=0,C=0,D=1)  subtractive      (0-Cs)*As + Cd   =  Cd - Cs*As (clamped)
    """
    fields = _ps2_alpha_fields(item)
    if fields is None:
        return None
    a = fields["a"]
    b = fields["b"]
    c = fields["c"]
    d = fields["d"]
    if a == 0 and b == 2 and c in (0, 2) and d == 1:
        return "additive"
    if a == 2 and b == 0 and c in (0, 2) and d == 1:
        return "subtractive"
    # Standard alpha blend (Cs*As + Cd*(1-As)) only — `c` must be As (=0) so the
    # blend factor is per-pixel source alpha. Patterns like 0x54 (c=Ad,
    # destination-alpha blend) typically render opaque on PS2 because the
    # destination is opaque (e.g. grass over ground) and we can't sample
    # destination alpha in Blender; defer to the C# alpha-mode classification
    # for those cases.
    if a == 0 and b == 1 and c == 0 and d == 1:
        return "blend"
    if (
        a == 0
        and b == 1
        and c == 2
        and d == 1
        and fields["fix"] < _PS2_FIX_BLEND_OPAQUE_THRESHOLD
    ):
        return "blend"
    return None


def _metadata_blend_hint(metadata):
    hint = "opaque"
    alpha_ref = None
    for item in metadata:
        kind = item.get("kind")
        if kind == "rw_gs_alpha":
            if item.get("isAdditive"):
                return "additive", alpha_ref
            if item.get("isSubtractive"):
                return "subtractive", alpha_ref
            if item.get("isBlend"):
                hint = "blend"
        elif kind == "ps2_gs":
            test_fields = _ps2_test_fields(item)
            alpha_ref = _ps2_alpha_mask_ref(test_fields)
            if alpha_ref is None:
                alpha_ref = item.get("alphaRef")
            ps2_recipe = _ps2_alpha_classification(item)
            # PS2 ALPHA register definitively says blend mode → prefer that over
            # alpha-test mask classification. PS2 hardware does alpha test FIRST
            # (potentially discarding fully-empty pixels) then alpha-blends the
            # rest. For materials like water with ALPHA=0x44 + AREF=1, the alpha
            # test only discards alpha=0 pixels; the visual intent is alpha blend.
            if ps2_recipe in ("additive", "subtractive", "blend"):
                return ps2_recipe, alpha_ref
            # PS2 ALPHA is opaque-equivalent (0x00 / 0x0A / 0x1A). Check alpha
            # test for masking: TEST register bit 0 = ATE (Alpha Test Enable).
            if _ps2_alpha_test_discards_color(test_fields) and alpha_ref not in (None, 0):
                hint = "mask"
        elif kind == "ddm_blend":
            blend_mode = str(item.get("blendMode") or "").lower()
            if "add" in blend_mode or blend_mode in ("1", "3"):
                return "additive", alpha_ref
            if "sub" in blend_mode:
                return "subtractive", alpha_ref
            if blend_mode not in ("", "0", "opaque", "none"):
                hint = "blend"
        elif kind == "xbx_material":
            # PC/Xbox sort and alpha-cutoff fields are not sufficient evidence
            # of transparency in THAW worldzones. ModelDocument has already
            # combined texture alpha and pass blend state; trust AlphaMode.
            pass
    return hint, alpha_ref


def _set_if_present(target, name, value):
    if hasattr(target, name):
        try:
            setattr(target, name, value)
            return True
        except Exception:
            return False
    return False


def _set_blend_method(material, method):
    # Eevee Next (Blender 4.2+) split blend_method into surface_render_method;
    # older Blender uses blend_method. Set both when present because recent
    # builds can expose both while only surface_render_method drives viewport
    # behavior.
    _set_if_present(material, "blend_method", method)
    _set_if_present(material, "surface_render_method", method)


def _clear_default_nodes(material):
    nt = material.node_tree
    for node in list(nt.nodes):
        nt.nodes.remove(node)
    return nt


_WRAP_MODE_MAP = {
    "Repeat": "REPEAT",
    "ClampToEdge": "EXTEND",
    "MirrorRepeat": "MIRROR",
    "ClampToBorder": "CLIP",
}


def _new_image_node(node_tree, image, label=None, wrap_u="Repeat", wrap_v="Repeat"):
    node = node_tree.nodes.new("ShaderNodeTexImage")
    node.image = image
    if label:
        node.label = label
    # Apply texture wrap mode from manifest. Blender's TEX_IMAGE node has a single
    # `extension` property that applies to both U and V; if the manifest says U
    # and V differ we pick whichever is non-Repeat (CLAMP wins) since the common
    # PS2 case is "both clamped or both repeated".
    mode = wrap_u if wrap_u != "Repeat" else wrap_v
    node.extension = _WRAP_MODE_MAP.get(mode, "REPEAT")
    return node


_PRINCIPLED_BASE_COLOR = "Base Color"
_PRINCIPLED_ALPHA = "Alpha"
_PS2_FIX_BLEND_OPAQUE_THRESHOLD = 96


def _configure_unlit_principled(node, color):
    """Configure a Principled BSDF so it renders as unlit (texture color shows
    through emission, lit contribution suppressed). PS2 game textures bake their
    own shading.

    Using Principled BSDF rather than a bare Emission node is important because
    Eevee Next's surface_render_method (DITHERED for alpha-clip, BLENDED for
    alpha-blend) reads the `Alpha` input directly off Principled. A Mix Shader
    of Transparent+Emission does not expose alpha in a form Eevee's render
    method picks up reliably.
    """
    node.inputs[_PRINCIPLED_BASE_COLOR].default_value = (0.0, 0.0, 0.0, 1.0)
    if "Specular IOR Level" in node.inputs:
        node.inputs["Specular IOR Level"].default_value = 0.0
    elif "Specular" in node.inputs:
        node.inputs["Specular"].default_value = 0.0
    if "Metallic" in node.inputs:
        node.inputs["Metallic"].default_value = 0.0
    if "Roughness" in node.inputs:
        node.inputs["Roughness"].default_value = 1.0
    emission_input = node.inputs.get("Emission Color") or node.inputs.get("Emission")
    if emission_input is not None:
        emission_input.default_value = color
    if "Emission Strength" in node.inputs:
        node.inputs["Emission Strength"].default_value = 1.0
    if _PRINCIPLED_ALPHA in node.inputs:
        node.inputs[_PRINCIPLED_ALPHA].default_value = color[3]


_VERTEX_COLOR_LAYER = "Color"


def _scalar_to_color_socket(node_tree, scalar_socket):
    """Convert a scalar value socket into a 3-channel color socket so it can be
    fed into a MixRGB Color input."""
    combine = node_tree.nodes.new("ShaderNodeCombineColor")
    combine.mode = "RGB"
    node_tree.links.new(scalar_socket, combine.inputs["Red"])
    node_tree.links.new(scalar_socket, combine.inputs["Green"])
    node_tree.links.new(scalar_socket, combine.inputs["Blue"])
    return combine.outputs["Color"]


def _multiply_floats(node_tree, *sockets):
    """Multiply 2+ scalar sockets together via chained ShaderNodeMath nodes."""
    if len(sockets) < 2:
        raise ValueError("_multiply_floats needs at least 2 sockets")
    current = sockets[0]
    for nxt in sockets[1:]:
        node = node_tree.nodes.new("ShaderNodeMath")
        node.operation = "MULTIPLY"
        node_tree.links.new(current, node.inputs[0])
        node_tree.links.new(nxt, node.inputs[1])
        current = node.outputs["Value"]
    return current


def _ps2_modulate_color(node_tree, color_a_socket, color_b_socket):
    """PS2 GS MODULATE TFX (per-channel): out = clamp((Cv * Ct) >> 7, 0, 255).

    In normalized [0,1] space this is `clamp(2 * Cv * Ct, 0, 1)`. Reference:
    `pcsx2/GS/Renderers/SW/GSDrawScanlineCodeGenerator.all.cpp:198` —
    `modulate16<1>` is `psllw a, 2; pmulhw a, f` which on u8-in-u16 inputs
    computes `(a << 2) * f >> 16 = a * f / 16384`. With `f` loaded as
    `value << 7` (so 0..255 maps to 0..32640), the high 16 bits give
    `a*f/128` — exactly the GS `(Cv*Ct)>>7` formula.

    Implementation: `MixRGB(MULTIPLY)` then `MixRGB(ADD)` to itself, both
    with Clamp Result so values >1 saturate (matching PS2 hardware).
    """
    multiply = node_tree.nodes.new("ShaderNodeMixRGB")
    multiply.blend_type = "MULTIPLY"
    multiply.inputs["Fac"].default_value = 1.0
    multiply.use_clamp = True
    node_tree.links.new(color_a_socket, multiply.inputs["Color1"])
    node_tree.links.new(color_b_socket, multiply.inputs["Color2"])

    double_clamp = node_tree.nodes.new("ShaderNodeMixRGB")
    double_clamp.blend_type = "ADD"
    double_clamp.inputs["Fac"].default_value = 1.0
    double_clamp.use_clamp = True
    node_tree.links.new(multiply.outputs["Color"], double_clamp.inputs["Color1"])
    node_tree.links.new(multiply.outputs["Color"], double_clamp.inputs["Color2"])
    return double_clamp.outputs["Color"]


def _ps2_modulate_alpha(node_tree, alpha_a_socket, alpha_b_socket):
    """PS2 GS MODULATE alpha (TCC=1): same `clamp(2 * Cv_a * Ct_a, 0, 1)`.

    Visible knock-on effects of getting this right (vs naive Cv*Ct):
    - Shadow surfaces with VC.alpha=0.65 stop dithering: 2*0.65*1.0 → clamp 1.0.
    - Foliage/decals with mid texture alpha render solid: 2*0.5*1.0 → clamp 1.0.
    Both match how they look on PS2 hardware.
    """
    mul = node_tree.nodes.new("ShaderNodeMath")
    mul.operation = "MULTIPLY"
    mul.use_clamp = False
    node_tree.links.new(alpha_a_socket, mul.inputs[0])
    node_tree.links.new(alpha_b_socket, mul.inputs[1])

    double_clamp = node_tree.nodes.new("ShaderNodeMath")
    double_clamp.operation = "MULTIPLY"
    double_clamp.use_clamp = True  # clamps result to [0,1]
    node_tree.links.new(mul.outputs["Value"], double_clamp.inputs[0])
    double_clamp.inputs[1].default_value = 2.0
    return double_clamp.outputs["Value"]


def _wire_unlit_texture(
    node_tree,
    principled,
    tex,
    *,
    invert_color=False,
    alpha_source="alpha",
    premultiply_alpha=True,
    alpha_scale=1.0,
    color_scale=1.0,
    alpha_cutoff=None,
    use_vertex_alpha=True,
):
    """Wire a TEX_IMAGE to a Principled BSDF in unlit mode, modulating by the
    mesh's vertex colors so PS2 GS TFX=MODULATE (Cv * Tx) renders correctly.

    Three key parameters:

    * `invert_color` (subtractive): inverts (color * VC) before emission so
      bright source texels darken the destination via standard alpha blend.
    * `alpha_source` selects how the Alpha input is derived:
         "alpha"      - TEX.Alpha * VC.Alpha (default; opaque/mask/blend with
                        real alpha channel)
         "luminance"  - TEX.luminance * VC.Alpha (subtractive, or blend/additive
                        textures with chroma-key transparency)
         "opaque"     - alpha is fixed to 1.0
         "constant"   - alpha is a fixed scalar (PS2 C=FIX with no alpha test)
         "constant_coverage" - fixed scalar gated by TEX.Alpha coverage, ignoring
                        VC.Alpha (PS2 C=FIX plus alpha-test cutout)
    * `premultiply_alpha`: if True, pre-multiplies emission color by the final
      Alpha. Required for opaque/mask/blend/additive because Blender's Principled
      BSDF emits Emission Color *independently* of Alpha (so transparent texels
      otherwise still show their authored color). Should be False for the
      subtractive recipe: that recipe relies on the standard alpha-over BLEND
      formula `(1-Cs)*alpha + dst*(1-alpha)` to approximate `Cd - Cs*As`, and
      pre-multiplying the inverted color collapses bright pixels back to mid-tone.

    Topology summary:
        TEX.Color * VC.Color [-> Invert if subtractive] -> color_source
        alpha_source         -> Alpha (Math.Multiply chain)
        color_source [* alpha_as_color if premultiply] -> Emission Color
    """
    vc = node_tree.nodes.new("ShaderNodeVertexColor")
    vc.layer_name = _VERTEX_COLOR_LAYER

    color_mul = node_tree.nodes.new("ShaderNodeMixRGB")
    color_mul.blend_type = "MULTIPLY"
    color_mul.inputs["Fac"].default_value = 1.0
    node_tree.links.new(tex.outputs["Color"], color_mul.inputs["Color1"])
    node_tree.links.new(vc.outputs["Color"], color_mul.inputs["Color2"])

    color_source = color_mul.outputs["Color"]
    if invert_color:
        invert = node_tree.nodes.new("ShaderNodeInvert")
        invert.inputs["Fac"].default_value = 1.0
        node_tree.links.new(color_source, invert.inputs["Color"])
        color_source = invert.outputs["Color"]

    if color_scale != 1.0:
        scale_color = node_tree.nodes.new("ShaderNodeMixRGB")
        scale_color.blend_type = "MULTIPLY"
        scale_color.inputs["Fac"].default_value = 1.0
        scale_color.inputs["Color2"].default_value = (color_scale, color_scale, color_scale, 1.0)
        node_tree.links.new(color_source, scale_color.inputs["Color1"])
        color_source = scale_color.outputs["Color"]

    # Alpha source:
    alpha_value = None
    if alpha_source == "luminance":
        rgb_to_bw = node_tree.nodes.new("ShaderNodeRGBToBW")
        node_tree.links.new(tex.outputs["Color"], rgb_to_bw.inputs["Color"])
        alpha_value = (
            _multiply_floats(node_tree, rgb_to_bw.outputs["Val"], vc.outputs["Alpha"])
            if use_vertex_alpha
            else rgb_to_bw.outputs["Val"]
        )
    elif alpha_source == "opaque":
        alpha_value = None
    elif alpha_source == "constant":
        value = node_tree.nodes.new("ShaderNodeValue")
        value.outputs["Value"].default_value = alpha_scale
        alpha_value = value.outputs["Value"]
    elif alpha_source == "constant_coverage":
        value = node_tree.nodes.new("ShaderNodeValue")
        value.outputs["Value"].default_value = alpha_scale
        if "Alpha" in tex.outputs:
            coverage = tex.outputs["Alpha"]
            if alpha_cutoff is not None and alpha_cutoff > 0.0:
                compare = node_tree.nodes.new("ShaderNodeMath")
                compare.operation = "GREATER_THAN"
                node_tree.links.new(coverage, compare.inputs[0])
                compare.inputs[1].default_value = alpha_cutoff
                coverage = compare.outputs["Value"]
            alpha_value = _multiply_floats(node_tree, value.outputs["Value"], coverage)
        else:
            alpha_value = value.outputs["Value"]
    else:
        if "Alpha" in tex.outputs:
            alpha_value = (
                _multiply_floats(node_tree, tex.outputs["Alpha"], vc.outputs["Alpha"])
                if use_vertex_alpha
                else tex.outputs["Alpha"]
            )
        else:
            alpha_value = vc.outputs["Alpha"] if use_vertex_alpha else None

    if alpha_value is not None and alpha_scale != 1.0 and alpha_source not in ("constant", "constant_coverage"):
        scale_alpha = node_tree.nodes.new("ShaderNodeMath")
        scale_alpha.operation = "MULTIPLY"
        scale_alpha.use_clamp = True
        node_tree.links.new(alpha_value, scale_alpha.inputs[0])
        scale_alpha.inputs[1].default_value = alpha_scale
        alpha_value = scale_alpha.outputs["Value"]

    if premultiply_alpha and alpha_value is not None:
        # Pre-multiply emission color by the final alpha so transparent texels
        # emit nothing. Blender's Principled BSDF adds Emission on top of the
        # BSDF lobes and bypasses the Alpha input entirely; without this gate,
        # Alpha=0 pixels still show the texture color.
        alpha_color = _scalar_to_color_socket(node_tree, alpha_value)
        color_premul = node_tree.nodes.new("ShaderNodeMixRGB")
        color_premul.blend_type = "MULTIPLY"
        color_premul.inputs["Fac"].default_value = 1.0
        node_tree.links.new(color_source, color_premul.inputs["Color1"])
        node_tree.links.new(alpha_color, color_premul.inputs["Color2"])
        emission_color_socket = color_premul.outputs["Color"]
    else:
        emission_color_socket = color_source

    emission_input = principled.inputs.get("Emission Color") or principled.inputs.get("Emission")
    if emission_input is not None:
        node_tree.links.new(emission_color_socket, emission_input)

    if _PRINCIPLED_ALPHA in principled.inputs and alpha_value is not None:
        node_tree.links.new(alpha_value, principled.inputs[_PRINCIPLED_ALPHA])
    elif _PRINCIPLED_ALPHA in principled.inputs and alpha_source == "opaque":
        principled.inputs[_PRINCIPLED_ALPHA].default_value = 1.0


def _ps2_alpha_ref_to_coverage_cutoff(alpha_ref):
    if alpha_ref in (None, 0):
        return None
    try:
        ref = float(alpha_ref)
    except (TypeError, ValueError):
        return None
    if ref <= 0.0:
        return None
    # GS alpha-test references are integer 0..255 values. Use a half-step
    # threshold with Blender's GREATER_THAN node to approximate >= AREF.
    return max(0.0, min(1.0, (ref - 0.5) / 255.0))


def _build_opaque_shader(
    material,
    base_color,
    image,
    alpha_threshold=None,
    blend_recipe="opaque",
    unlit=True,
    wrap_u="Repeat",
    wrap_v="Repeat",
    constant_alpha=None,
    constant_alpha_ref=None,
    use_vertex_alpha=True,
):
    """Textured shader for opaque/mask/blend recipes."""
    nt = _clear_default_nodes(material)
    output = nt.nodes.new("ShaderNodeOutputMaterial")
    principled = nt.nodes.new("ShaderNodeBsdfPrincipled")
    if unlit:
        _configure_unlit_principled(principled, base_color)
        if constant_alpha is not None and _PRINCIPLED_ALPHA in principled.inputs:
            principled.inputs[_PRINCIPLED_ALPHA].default_value = constant_alpha
        if image is not None:
            tex = _new_image_node(nt, image, label="diffuse", wrap_u=wrap_u, wrap_v=wrap_v)
            # For BLEND recipe, if texture alpha is uniform but luminance varies,
            # use luminance as alpha so brightness-driven transparency works
            # (e.g. PS2 destination-alpha blend modes like 0x54 used for water,
            # puddles, and similar reflective overlays).
            if blend_recipe == "opaque":
                _wire_unlit_texture(nt, principled, tex, alpha_source="opaque", premultiply_alpha=False)
            elif constant_alpha is not None:
                alpha_source = "constant_coverage" if constant_alpha_ref not in (None, 0) else "constant"
                _wire_unlit_texture(
                    nt,
                    principled,
                    tex,
                    alpha_source=alpha_source,
                    alpha_scale=constant_alpha,
                    alpha_cutoff=_ps2_alpha_ref_to_coverage_cutoff(constant_alpha_ref),
                    use_vertex_alpha=use_vertex_alpha)
            elif blend_recipe == "blend" and _texture_has_uniform_alpha(image):
                _wire_unlit_texture(nt, principled, tex, alpha_source="luminance", use_vertex_alpha=use_vertex_alpha)
            else:
                _wire_unlit_texture(nt, principled, tex, use_vertex_alpha=use_vertex_alpha)
    else:
        principled.inputs[_PRINCIPLED_BASE_COLOR].default_value = base_color
        if _PRINCIPLED_ALPHA in principled.inputs:
            principled.inputs[_PRINCIPLED_ALPHA].default_value = base_color[3]
        if image is not None:
            tex = _new_image_node(nt, image, label="diffuse", wrap_u=wrap_u, wrap_v=wrap_v)
            nt.links.new(tex.outputs["Color"], principled.inputs[_PRINCIPLED_BASE_COLOR])
            if _PRINCIPLED_ALPHA in principled.inputs and "Alpha" in tex.outputs:
                nt.links.new(tex.outputs["Alpha"], principled.inputs[_PRINCIPLED_ALPHA])
    nt.links.new(principled.outputs["BSDF"], output.inputs["Surface"])

    if blend_recipe == "mask":
        _set_blend_method(material, "CLIP")
        if alpha_threshold is not None:
            try:
                material.alpha_threshold = max(0.0, min(1.0, float(alpha_threshold) / 255.0))
            except Exception:
                pass
    elif blend_recipe == "blend":
        _set_blend_method(material, "BLEND")
    else:
        _set_blend_method(material, "OPAQUE")


def _build_additive_shader(material, base_color, image, wrap_u="Repeat", wrap_v="Repeat", fix_scale=None):
    """PS2 GS additive: Cs*As/128 + Cd. True additive isn't expressible in Eevee
    Next's BLEND mode (no native add blend), so approximate by alpha-blending an
    emissive texture: bright source pixels overlay as bright, dark/transparent
    source pixels let the destination show through.

    Cycles renders this correctly via the emission contribution.
    """
    nt = _clear_default_nodes(material)
    output = nt.nodes.new("ShaderNodeOutputMaterial")
    principled = nt.nodes.new("ShaderNodeBsdfPrincipled")
    _configure_unlit_principled(principled, base_color)
    if image is not None:
        tex = _new_image_node(nt, image, label="additive_color", wrap_u=wrap_u, wrap_v=wrap_v)
        # If the texture has no real alpha variation, drive transparency from
        # luminance so dark/black-keyed pixels become invisible.
        alpha_source = "luminance" if _texture_has_uniform_alpha(image) else "alpha"
        scale = 1.0 if fix_scale is None else fix_scale
        _wire_unlit_texture(nt, principled, tex, alpha_source=alpha_source, alpha_scale=scale)
    nt.links.new(principled.outputs["BSDF"], output.inputs["Surface"])
    _set_blend_method(material, "BLEND")
    material.use_backface_culling = False


def _build_subtractive_shader(material, base_color, image, wrap_u="Repeat", wrap_v="Repeat", fix_scale=None):
    """PS2 GS subtractive: Cd - Cs*As/128. No native Eevee/Cycles equivalent.

    Approximate via Eevee BLEND with src.color=(0,0,0) and src.alpha=luminance
    of (texture * vertex_color), boosted ~2x and clamped. Eevee's alpha-over
    composites this as
        result = 0*alpha + Cd*(1-alpha) = Cd*(1-2*luminance)  (clamped)
    which scales the destination toward black proportionally to source
    brightness. PS2's true subtract `Cd - Cs*As` darkens by an *additive*
    amount, not multiplicative — so for darker destinations our (1-luminance)
    form under-darkens. Doubling the alpha shifts the curve so the darkening
    strength near typical mid-tone destinations (~0.5-0.7) lines up with PS2's
    additive subtract within ~0.05 luminance. Without this boost the road
    asphalt rendered ~50% too light: PS2's 0.64 - 0.36 = 0.28 vs our
    0.64*(1-0.36) = 0.41.
    """
    nt = _clear_default_nodes(material)
    output = nt.nodes.new("ShaderNodeOutputMaterial")
    principled = nt.nodes.new("ShaderNodeBsdfPrincipled")
    _configure_unlit_principled(principled, (0.0, 0.0, 0.0, 1.0))
    if image is not None:
        tex = _new_image_node(nt, image, label="subtractive_source", wrap_u=wrap_u, wrap_v=wrap_v)
        vc = nt.nodes.new("ShaderNodeVertexColor")
        vc.layer_name = _VERTEX_COLOR_LAYER

        # Modulate texture by vertex color (PS2 MODULATE TFX), then take
        # luminance for the darkening intensity.
        color_mul = nt.nodes.new("ShaderNodeMixRGB")
        color_mul.blend_type = "MULTIPLY"
        color_mul.inputs["Fac"].default_value = 1.0
        nt.links.new(tex.outputs["Color"], color_mul.inputs["Color1"])
        nt.links.new(vc.outputs["Color"], color_mul.inputs["Color2"])

        luminance = nt.nodes.new("ShaderNodeRGBToBW")
        nt.links.new(color_mul.outputs["Color"], luminance.inputs["Color"])

        # Modulate by VC alpha so geometry can fade the subtraction strength.
        alpha_value = _multiply_floats(nt, luminance.outputs["Val"], vc.outputs["Alpha"])
        # Boost factor brings the multiplicative `1-luminance` curve closer
        # to PS2's additive `Cd - Cs` for typical mid-tone destinations.
        # Combined with fix_scale (PS2 ALPHA register's FIX value, only set
        # for ALPHA=0x62 fixed-blend subtract; defaults to 1).
        boost = 2.0 * (fix_scale if fix_scale is not None else 1.0)
        if boost != 1.0:
            scale_alpha = nt.nodes.new("ShaderNodeMath")
            scale_alpha.operation = "MULTIPLY"
            scale_alpha.use_clamp = True
            nt.links.new(alpha_value, scale_alpha.inputs[0])
            scale_alpha.inputs[1].default_value = boost
            alpha_value = scale_alpha.outputs["Value"]
        if _PRINCIPLED_ALPHA in principled.inputs:
            nt.links.new(alpha_value, principled.inputs[_PRINCIPLED_ALPHA])
        # Emission Color stays at its configured default (0,0,0). Do NOT route
        # texture color into emission — that would defeat the darkening math.
    else:
        if _PRINCIPLED_ALPHA in principled.inputs:
            # Without a texture, fall back to base_color luminance as the strength.
            principled.inputs[_PRINCIPLED_ALPHA].default_value = (
                base_color[0] * 0.299 + base_color[1] * 0.587 + base_color[2] * 0.114
            ) * base_color[3]
    nt.links.new(principled.outputs["BSDF"], output.inputs["Surface"])
    _set_blend_method(material, "BLEND")
    material.use_backface_culling = False


def _apply_recipe(
    material,
    recipe,
    base_color,
    image,
    alpha_ref,
    unlit=True,
    wrap_u="Repeat",
    wrap_v="Repeat",
    ps2_alpha_fields=None,
    use_vertex_alpha=True,
):
    material["neversoft_viewport_blend_hint"] = recipe
    if alpha_ref is not None:
        material["neversoft_alpha_ref"] = alpha_ref
    constant_alpha = None
    fix_scale = None
    if ps2_alpha_fields and ps2_alpha_fields.get("c") == 2:
        fix_scale = max(0.0, min(1.0, float(ps2_alpha_fields.get("fix", 128)) / 128.0))
        if ps2_alpha_fields.get("a") == 0 and ps2_alpha_fields.get("b") == 1 and ps2_alpha_fields.get("d") == 1:
            constant_alpha = fix_scale
    if recipe == "additive":
        _build_additive_shader(material, base_color, image, wrap_u=wrap_u, wrap_v=wrap_v, fix_scale=fix_scale)
    elif recipe == "subtractive":
        _build_subtractive_shader(material, base_color, image, wrap_u=wrap_u, wrap_v=wrap_v, fix_scale=fix_scale)
    elif recipe == "mask":
        _build_opaque_shader(
            material,
            base_color,
            image,
            alpha_threshold=alpha_ref,
            blend_recipe="mask",
            unlit=unlit,
            wrap_u=wrap_u,
            wrap_v=wrap_v,
            use_vertex_alpha=use_vertex_alpha)
    elif recipe == "blend":
        _build_opaque_shader(
            material,
            base_color,
            image,
            blend_recipe="blend",
            unlit=unlit,
            wrap_u=wrap_u,
            wrap_v=wrap_v,
            constant_alpha=constant_alpha,
            constant_alpha_ref=alpha_ref,
            use_vertex_alpha=use_vertex_alpha)
    else:
        _build_opaque_shader(
            material,
            base_color,
            image,
            blend_recipe="opaque",
            unlit=unlit,
            wrap_u=wrap_u,
            wrap_v=wrap_v,
            use_vertex_alpha=use_vertex_alpha)


def _maybe_synthesize_alpha_from_luminance(image):
    """Many PS2 textures use a chroma-key convention where black RGB pixels are
    meant to be transparent, but the PNG decoder records alpha=1.0 throughout
    because the underlying GS pixel format had no alpha bits. Detect this case
    (uniform alpha AND visible black areas) and synthesize alpha from luminance
    so additive overlays, light textures, and decals render correctly in Blender.

    Uses NumPy foreach for speed (purely-Python pixel iteration is too slow with
    hundreds of textures).
    """
    if image is None or image.size[0] == 0 or image.size[1] == 0:
        return
    try:
        import numpy as np
    except ImportError:
        return
    n = image.size[0] * image.size[1]
    if n == 0 or image.channels != 4:
        return
    pixels = np.empty(n * 4, dtype=np.float32)
    try:
        image.pixels.foreach_get(pixels)
    except Exception:
        return
    alphas = pixels[3::4]
    if alphas.size == 0:
        return
    if float(alphas.max() - alphas.min()) > 0.01:
        return  # texture has real alpha variation; trust it
    # Luminance in BT.601 weights (matches typical PS2/PSX conversions).
    r = pixels[0::4]
    g = pixels[1::4]
    b = pixels[2::4]
    lum = r * 0.299 + g * 0.587 + b * 0.114
    if float(lum.min()) > 0.05:
        return  # texture has no black pixels; alpha=1 is genuinely opaque
    pixels[3::4] = np.clip(lum, 0.0, 1.0)
    image.pixels.foreach_set(pixels)
    image.update()


def _load_rgba_image(name, width, height, data, synthesize_alpha=True):
    if not data or width <= 0 or height <= 0:
        return None
    expected_len = width * height * 4
    if len(data) < expected_len:
        return None

    image = bpy.data.images.new(name=name or "texture", width=width, height=height, alpha=True)
    # Set the colorspace BEFORE writing pixel data. Setting
    # colorspace_settings.name AFTER foreach_set causes Blender to re-read pixel
    # data from the (non-existent) source file, zeroing out the in-memory
    # pixels and making every imported texture render as RGBA(0,0,0,0).
    try:
        image.colorspace_settings.name = "sRGB"
    except Exception:
        pass

    loaded = False
    try:
        import numpy as np

        pixels = np.frombuffer(data[:expected_len], dtype=np.uint8).reshape((height, width, 4))
        # ImageSharp writes PNG scanlines top-down. Blender pixel arrays are bottom-up,
        # so reverse rows to match the orientation produced by bpy.data.images.load().
        pixels = pixels[::-1, :, :].astype(np.float32) / 255.0
        image.pixels.foreach_set(pixels.reshape(expected_len))
        loaded = True
    except Exception:
        loaded = False

    if not loaded:
        values = [0.0] * expected_len
        out = 0
        for y in range(height - 1, -1, -1):
            row = y * width * 4
            for offset in range(row, row + width * 4, 4):
                values[out] = data[offset] / 255.0
                values[out + 1] = data[offset + 1] / 255.0
                values[out + 2] = data[offset + 2] / 255.0
                values[out + 3] = data[offset + 3] / 255.0
                out += 4
        image.pixels.foreach_set(values)
    image.update()
    # Run alpha-from-luminance synthesis BEFORE packing so any alpha rewrites
    # are captured in the packed PNG.
    if synthesize_alpha:
        _maybe_synthesize_alpha_from_luminance(image)
    # Force the in-memory pixel data to be encoded into a packed PNG so the
    # data survives the .blend save/reload cycle. `image.pack()` on a freshly-
    # created image (no source filepath) does NOT reliably preserve pixel data
    # — the saved .blend ends up referencing an empty packed_file and every
    # texture renders as RGBA(0,0,0,0), which makes the entire model invisible.
    # Saving to a temp PNG first gives pack() a real file to encode.
    import tempfile
    fd, temp_path = tempfile.mkstemp(suffix=".png", prefix="nsmt_blend_tex_")
    os.close(fd)
    try:
        image.filepath_raw = temp_path
        image.file_format = "PNG"
        image.save()
        image.pack()
    except Exception:
        pass
    finally:
        try:
            os.remove(temp_path)
        except Exception:
            pass
    # Detach the now-stale filepath so Blender doesn't try to find the temp
    # file later (it's been deleted, but the packed_file still has the data).
    image.filepath = ""
    image.filepath_raw = ""
    return image


def _load_image(package, package_dir, texture_entry, synthesize_alpha=True):
    if not texture_entry:
        return None

    rgba_path = texture_entry.get("RgbaPath")
    if rgba_path:
        data = _read_package_bytes(package, package_dir, rgba_path)
        # _load_rgba_image runs alpha synthesis and packs the image internally
        # because Blender's pack() on a freshly-created image needs a real
        # source file to encode pixel data — see comment there for details.
        return _load_rgba_image(
            texture_entry.get("Name"),
            int(texture_entry.get("Width") or 0),
            int(texture_entry.get("Height") or 0),
            data,
            synthesize_alpha=synthesize_alpha,
        )

    relative_path = texture_entry.get("PngPath")
    if not relative_path or package is not None:
        return None
    path = os.path.join(package_dir, relative_path.replace("/", os.sep))
    if not os.path.exists(path):
        return None
    image = bpy.data.images.load(path, check_existing=True)
    if synthesize_alpha:
        _maybe_synthesize_alpha_from_luminance(image)
    # The package directory lives in the temp tree and is deleted after the
    # Blender helper finishes, so pack the file into the .blend now. Otherwise
    # the saved scene references a missing path and Blender can't display the
    # texture.
    try:
        image.pack()
    except Exception:
        pass
    return image


def _texture_mean_alpha(image):
    """Returns the mean alpha of the texture, or None if not computable.

    Used to distinguish "mostly-opaque solid object" (high mean) from "sparse
    decal/cutout" (low mean). Two materials with identical PS2 ALPHA=0x44 +
    AREF=1 register state can be either kind — a semi-truck cutout (mean ~0.93)
    versus a Skillz Inn sign with feathered text on transparent background
    (mean ~0.29). The first wants MASK/DITHERED rendering for proper depth
    write; the second wants BLEND so coplanar decal stacks composite cleanly.
    """
    if image is None or image.size[0] == 0 or image.size[1] == 0 or image.channels != 4:
        return None
    try:
        import numpy as np
        n = image.size[0] * image.size[1]
        sample_step = max(1, n // 1000)
        px = np.empty(n * 4, dtype=np.float32)
        image.pixels.foreach_get(px)
        alphas = px[3::4][::sample_step][:1000]
        return float(alphas.mean())
    except Exception:
        return None


def _texture_has_uniform_alpha(image):
    """Returns True iff the image's alpha channel doesn't carry meaningful
    per-pixel transparency information — used by blend / additive shader builders
    to decide whether to drive alpha from the texture's alpha channel or from
    luminance.

    Two cases count as "uniform":
    * Strictly uniform (alpha range < 0.01): the texture has no real alpha at all.
    * Near-uniform compared to luminance variation (alpha range tiny while lum
      varies widely): the alpha channel is just compression noise; the actual
      shape information lives in the RGB. Many PS2 additive overlays (lamp glow,
      litter, ground lights) have alpha ranges like 0.96..1.00 with black RGB
      backgrounds — without this heuristic those backgrounds render solid black.
    """
    if image is None or image.size[0] == 0 or image.size[1] == 0 or image.channels != 4:
        return True
    try:
        import numpy as np
        n = image.size[0] * image.size[1]
        if n == 0:
            return True
        sample_step = max(1, n // 1000)
        px = np.empty(n * 4, dtype=np.float32)
        image.pixels.foreach_get(px)
        alphas = px[3::4][::sample_step][:1000]
        alpha_range = float(alphas.max() - alphas.min())
        if alpha_range < 0.01:
            return True
        r = px[0::4][::sample_step][:1000]
        g = px[1::4][::sample_step][:1000]
        b = px[2::4][::sample_step][:1000]
        lum = r * 0.299 + g * 0.587 + b * 0.114
        lum_range = float(lum.max() - lum.min())
        # If luminance varies substantially (>2x alpha variation) AND alpha is
        # nearly-uniform (< ~0.1 range), treat as uniform-alpha so the shader
        # uses luminance for transparency instead of trusting the noisy alpha.
        if alpha_range < 0.15 and lum_range > 2.0 * alpha_range:
            return True
        return False
    except Exception:
        return True


def _make_materials(manifest, package, package_dir):
    textures = manifest.get("Textures", [])
    source_kind = str(manifest.get("SourceKind", ""))
    is_xbx_scene = source_kind == "XbxScene"
    synthesize_texture_alpha = not is_xbx_scene
    materials = []
    for index, entry in enumerate(manifest.get("Materials", [])):
        material = bpy.data.materials.new(entry.get("Name") or f"material_{index:04d}")
        material.use_nodes = True

        base_color = entry.get("BaseColor") or [1.0, 1.0, 1.0, 1.0]
        base_color = tuple(float(v) for v in (base_color + [1.0, 1.0, 1.0, 1.0])[:4])
        material.diffuse_color = base_color

        image = None
        wrap_u = "Repeat"
        wrap_v = "Repeat"
        texture_index = entry.get("TextureIndex")
        if texture_index is not None and 0 <= texture_index < len(textures):
            tex_entry = textures[texture_index]
            image = _load_image(package, package_dir, tex_entry, synthesize_alpha=synthesize_texture_alpha)
            wrap_u = tex_entry.get("WrapU", "Repeat") or "Repeat"
            wrap_v = tex_entry.get("WrapV", "Repeat") or "Repeat"

        metadata = entry.get("NativeMetadata", [])
        hint, alpha_ref = _metadata_blend_hint(metadata)
        alpha_mode = str(entry.get("AlphaMode", "")).lower()
        ps2_alpha_fields = _ps2_material_alpha_fields(metadata)
        # ModelDocument has texture and vertex-alpha context that the raw GS
        # ALPHA register does not. If it downgraded a standard source-alpha
        # blend to opaque/mask, trust that decision so fully-opaque 0x44 draws do
        # not enter Blender's transparent sorting path.
        if hint == "blend" and alpha_mode in ("opaque", "mask"):
            hint = alpha_mode
            if alpha_mode == "mask":
                alpha_ref = entry.get("AlphaCutoff", alpha_ref)
        elif hint == "opaque":
            if alpha_mode == "mask":
                hint = "mask"
                alpha_ref = entry.get("AlphaCutoff", alpha_ref)
            elif alpha_mode == "blend":
                # ModelDocument decides whether C=Ad stays opaque, gets a baked
                # synthetic alpha texture, or falls back to source-alpha blend via
                # THAW_DEST_ALPHA=blend. Trust that document-level decision here.
                hint = "blend"
        # Downgrade hint=blend to mask only for mostly-opaque solid objects
        # (e.g. semi-trucks, glass panels, foliage cards). Sparse cutout decals
        # (signs, posters, shadows — mean alpha < ~0.6) stay in BLEND so they
        # don't trigger Eevee Next's DITHERED depth pre-pass, which would
        # occlude coplanar overlay decals (e.g. the Skillz Inn sign + star
        # are coplanar at exactly the same Z; if the star renders DITHERED its
        # depth pre-pass writes depth across the whole triangle even at
        # alpha=0 areas, hiding the sign behind it).
        if hint == "blend" and alpha_mode == "mask" and image is not None and not _texture_has_uniform_alpha(image):
            mean_alpha = _texture_mean_alpha(image)
            if mean_alpha is not None and mean_alpha > 0.6:
                hint = "mask"
                alpha_ref = entry.get("AlphaCutoff", alpha_ref)

        # PS2 game textures bake their own lighting; the IR flags this with Unlit=true.
        # Use Emission shader for unlit materials so Blender doesn't apply its own
        # specular/roughness that would wash everything out with a hazy overlay.
        unlit = bool(entry.get("Unlit", True))
        _apply_recipe(
            material,
            hint,
            base_color,
            image,
            alpha_ref,
            unlit=unlit,
            wrap_u=wrap_u,
            wrap_v=wrap_v,
            ps2_alpha_fields=ps2_alpha_fields,
            use_vertex_alpha=not is_xbx_scene,
        )

        material["neversoft_native_metadata"] = _json(metadata)
        material["neversoft_alpha_mode"] = entry.get("AlphaMode", "Opaque")
        material["neversoft_alpha_cutoff"] = entry.get("AlphaCutoff", 0.5)
        if texture_index is not None and 0 <= texture_index < len(textures):
            tex = textures[texture_index]
            material["neversoft_texture_name"] = tex.get("Name", "")
            material["neversoft_texture_wrap_u"] = tex.get("WrapU", "")
            material["neversoft_texture_wrap_v"] = tex.get("WrapV", "")
            checksum = tex.get("NativeChecksum")
            if checksum is not None:
                # Blender custom properties only accept signed C int; uint32 checksums
                # (e.g. 0xCCAA1B47) overflow. Store as hex string so callers can parse it.
                material["neversoft_texture_checksum"] = f"0x{int(checksum) & 0xFFFFFFFF:08X}"

        materials.append(material)
    return materials


def _read_vertices(package, package_dir, primitive):
    data = _read_package_bytes(package, package_dir, primitive["VertexBuffer"])
    if data is None:
        data = b""

    vertices = []
    normals = []
    colors = []
    uvs = []
    for offset in range(0, len(data), VERTEX_STRUCT.size):
        px, py, pz, nx, ny, nz, r, g, b, a, u, v = VERTEX_STRUCT.unpack_from(data, offset)
        vertices.append((px, py, pz))
        normals.append((nx, ny, nz))
        colors.append((r, g, b, a))
        uvs.append((u, v))
    return vertices, normals, colors, uvs


def _read_faces(package, package_dir, primitive):
    data = _read_package_bytes(package, package_dir, primitive["IndexBuffer"])
    if data is None:
        data = b""

    raw_indices = [
        INDEX_STRUCT.unpack_from(data, offset)[0]
        for offset in range(0, len(data), INDEX_STRUCT.size)
    ]
    return [
        tuple(raw_indices[i : i + 3])
        for i in range(0, len(raw_indices) - 2, 3)
    ]


def _assign_uvs(mesh, uvs):
    if not uvs:
        return
    uv_layer = mesh.uv_layers.new(name="UVMap")
    for polygon in mesh.polygons:
        for loop_index in polygon.loop_indices:
            vertex_index = mesh.loops[loop_index].vertex_index
            if 0 <= vertex_index < len(uvs):
                u, v = uvs[vertex_index]
                # The IR carries glTF-convention UVs (V=0 at top of texture). Blender's
                # UV space has V=0 at the bottom, so counter-flip V here. Without this
                # the texture renders upside down.
                uv_layer.data[loop_index].uv = (u, 1.0 - v)


def _assign_colors(mesh, colors):
    if not colors:
        return
    try:
        color_layer = mesh.color_attributes.new(name="Color", type="BYTE_COLOR", domain="CORNER")
    except Exception:
        return
    for polygon in mesh.polygons:
        for loop_index in polygon.loop_indices:
            vertex_index = mesh.loops[loop_index].vertex_index
            if 0 <= vertex_index < len(colors):
                color_layer.data[loop_index].color = colors[vertex_index]
    try:
        mesh.color_attributes.active_color = color_layer
    except Exception:
        pass


def _matrix_from_manifest(values):
    values = list(values or [])
    if len(values) != 16:
        return Y_UP_TO_Z_UP.copy()
    # Manifest transforms come from System.Numerics.Matrix4x4. Transpose from
    # its row-vector layout so Blender receives translation in the final column,
    # then pre-multiply by the Y-up -> Z-up axis swap so the worldzone stands
    # upright in Blender's native Z-up viewport.
    base = Matrix(
        (
            (values[0], values[4], values[8], values[12]),
            (values[1], values[5], values[9], values[13]),
            (values[2], values[6], values[10], values[14]),
            (values[3], values[7], values[11], values[15]),
        )
    )
    return Y_UP_TO_Z_UP @ base


def _worldzone_leaf_metadata(primitive):
    for item in primitive.get("NativeMetadata", []):
        if item.get("kind") == "ps2_worldzone_leaf":
            return item
    return None


def _worldzone_billboard_metadata(primitive):
    for item in primitive.get("NativeMetadata", []):
        if item.get("kind") == "ps2_worldzone_billboard":
            return item
    return None


def _get_or_create_billboard_target():
    """Return the scene's active camera, creating a placeholder Empty if none
    exists. Cached on the scene so subsequent billboards reuse the same target."""
    scene = bpy.context.scene
    stored = scene.get("neversoft_billboard_target_name")
    if stored:
        target = bpy.data.objects.get(stored)
        if target is not None:
            return target
    if scene.camera is not None:
        target = scene.camera
    else:
        target = bpy.data.objects.new("NeversoftBillboardTarget", None)
        target.empty_display_type = 'PLAIN_AXES'
        target.empty_display_size = 1.0
        bpy.context.collection.objects.link(target)
    scene["neversoft_billboard_target_name"] = target.name
    return target


def _get_or_create_billboard_collection():
    """Return (and lazily create) a "NeversoftBillboards" collection so users
    can hide/show all imported billboard quads in one click."""
    scene = bpy.context.scene
    coll = bpy.data.collections.get("NeversoftBillboards")
    if coll is None:
        coll = bpy.data.collections.new("NeversoftBillboards")
        scene.collection.children.link(coll)
    return coll


def _apply_billboard_constraint(obj, mesh, billboard_meta):
    """Attach a Track-To constraint so the quad faces the active camera while
    staying upright. The mesh comes in already centered on the pivot AND
    pre-scaled by the converter's coordinateScale (ModelDocumentGeometryAdapter
    runs LocalizePs2Vertices on every primitive, including billboards). The
    node transform set on the obj puts its origin at pivotCenter * scale in
    Blender world space — exactly where the rotation pivot needs to be.

    Track-To replaces the rotation part of obj.matrix_world, so we bake the
    Y_UP_TO_Z_UP axis swap into the mesh vertices and strip it from
    obj.matrix_world (keeping just the translation). That way the constraint
    is free to orient the obj per-frame without disturbing the axis swap.

    P1 diagnostic on z_sm found every Format-B billboard is axis-aligned with
    axis=(0,1,0) in PS2 space, which becomes world Z up after Y_UP_TO_Z_UP.
    After the bake, the quad's face normal is mesh-local -Y and its vertical
    edge is mesh-local +Z, so track_axis='TRACK_NEGATIVE_Y' + up_axis='UP_Z'
    keeps the quad facing the target with rotation locked to world Z."""
    mesh.transform(Y_UP_TO_Z_UP)
    # obj.matrix_world.translation == Y_UP_TO_Z_UP @ (pivotCenter_ps2 * scale)
    # — the obj origin in Blender world. Replace matrix_world with just that
    # translation so the constraint controls all rotation.
    translation = obj.matrix_world.translation.copy()
    obj.matrix_world = Matrix.Translation(translation)

    target = _get_or_create_billboard_target()
    constraint = obj.constraints.new(type='TRACK_TO')
    constraint.target = target
    constraint.track_axis = 'TRACK_NEGATIVE_Y'
    constraint.up_axis = 'UP_Z'

    obj["neversoft_billboard_kind"] = billboard_meta.get("billboardKind", "ScreenAligned")
    obj["neversoft_billboard_anchor"] = list(billboard_meta.get("anchor", [0.0, 0.0, 0.0]))
    obj["neversoft_billboard_size"] = list(billboard_meta.get("size", [0.0, 0.0]))
    obj["neversoft_billboard_pivot"] = list(billboard_meta.get("pivot", [0.0, 0.0, 0.0]))
    obj["neversoft_billboard_axis"] = list(billboard_meta.get("axis", [0.0, 0.0, 0.0]))


def _object_build_items(manifest):
    meshes = manifest.get("Meshes", [])
    items = []
    has_worldzone_order = False
    for node_index, node in enumerate(manifest.get("Nodes", [])):
        mesh_index = node.get("MeshIndex")
        if mesh_index is None or mesh_index < 0 or mesh_index >= len(meshes):
            continue
        mesh_entry = meshes[mesh_index]
        for prim_index, primitive in enumerate(mesh_entry.get("Primitives", [])):
            leaf_meta = _worldzone_leaf_metadata(primitive)
            if leaf_meta is not None and leaf_meta.get("renderOrder") is not None:
                has_worldzone_order = True
            items.append((node_index, node, mesh_entry, prim_index, primitive, leaf_meta))

    if has_worldzone_order:
        items.sort(
            key=lambda item: (
                int(item[5].get("renderOrder", 0x7FFFFFFF)) if item[5] else 0x7FFFFFFF,
                int(item[5].get("leafIndex", item[0])) if item[5] else item[0],
                item[0],
                item[3],
            )
        )
    return items


def _make_objects(manifest, package, package_dir, materials):
    collection = bpy.context.collection
    created = []

    for node_index, node, mesh_entry, prim_index, primitive, leaf_meta in _object_build_items(manifest):
        vertices, normals, colors, uvs = _read_vertices(package, package_dir, primitive)
        faces = _read_faces(package, package_dir, primitive)
        if not vertices or not faces:
            continue

        mesh_name = f"{mesh_entry.get('Name', 'mesh')}_{prim_index:03d}"
        mesh = bpy.data.meshes.new(mesh_name)
        mesh.from_pydata(vertices, [], faces)
        mesh.update(calc_edges=True)

        material_index = primitive.get("MaterialIndex", -1)
        if 0 <= material_index < len(materials):
            mesh.materials.append(materials[material_index])
            for polygon in mesh.polygons:
                polygon.material_index = 0

        _assign_uvs(mesh, uvs)
        _assign_colors(mesh, colors)
        try:
            mesh.normals_split_custom_set_from_vertices(normals)
            mesh.use_auto_smooth = True
        except Exception:
            pass

        object_name = node.get("Name") or f"node_{node_index:04d}"
        if len(mesh_entry.get("Primitives", [])) > 1:
            object_name = f"{object_name}_{prim_index:03d}"
        obj = bpy.data.objects.new(object_name, mesh)
        obj.matrix_world = _matrix_from_manifest(node.get("Transform"))
        obj["neversoft_mesh_metadata"] = _json(mesh_entry.get("NativeMetadata", []))
        obj["neversoft_primitive_metadata"] = _json(primitive.get("NativeMetadata", []))
        obj["neversoft_node_metadata"] = _json(node.get("NativeMetadata", []))
        if leaf_meta is not None:
            if leaf_meta.get("renderOrder") is not None:
                obj["neversoft_render_order"] = int(leaf_meta["renderOrder"])
            if leaf_meta.get("leafIndex") is not None:
                obj["neversoft_leaf_index"] = int(leaf_meta["leafIndex"])

        billboard_meta = _worldzone_billboard_metadata(primitive)
        if billboard_meta is not None:
            _apply_billboard_constraint(obj, mesh, billboard_meta)
            _get_or_create_billboard_collection().objects.link(obj)
        else:
            collection.objects.link(obj)
        created.append(obj)

    return created


def _apply_scene_metadata(manifest):
    scene = bpy.context.scene
    scene["neversoft_exporter"] = "NeversoftMultitool"
    scene["neversoft_source_kind"] = manifest.get("SourceKind", "")
    scene["neversoft_native_metadata"] = _json(manifest.get("NativeMetadata", []))


def main():
    manifest, package, package_dir = _load_package()
    blend_path = manifest["BlendPath"]

    _clear_scene()
    _apply_scene_metadata(manifest)
    materials = _make_materials(manifest, package, package_dir)
    _make_objects(manifest, package, package_dir, materials)

    output_dir = os.path.dirname(blend_path)
    if output_dir:
        os.makedirs(output_dir, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=blend_path, compress=True)


if __name__ == "__main__":
    main()
