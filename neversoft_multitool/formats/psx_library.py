import os
import struct
from math import log2

from neversoft_multitool.formats.psx_ps1_texture import extract_4bit_texture, extract_8bit_texture
from neversoft_multitool.formats.psx_pvr_texture import extract_16bit_texture
from neversoft_multitool.formats.psx_texture_header import PSXTextureHeader
from neversoft_multitool.psx.psx_io import write_to_png

PRINT_OUTPUT = True
FANCY_OUTPUT = False


def extract_textures(filename, input_dir, output_dir, file_index, create_sub_dirs, output_strings, update_file_table):
    """Extracts textures from a PSX file."""

    tex_names = []
    tex_hashes = {}
    textures_written = 0

    extraction_functions = {
        16: lambda reader, header: extract_4bit_texture(reader, header, palette_4bit),
        256: lambda reader, header: extract_8bit_texture(reader, header, palette_8bit),
        65536: lambda reader, header: extract_16bit_texture(reader, header, output_strings),
    }

    input_file = os.path.join(input_dir, filename)

    try:
        with open(input_file, "rb") as reader:
            # Read the file header and determine the number of objects, pointer to tagged chunks
            magic = reader.read(4)
            assert magic in (b"\x04\x00\x02\x00", b"\x03\x00\x02\x00", b"\x06\x00\x02\x00")

            skip_model_data(reader, output_strings)
            tex_names = read_texture_info(reader, output_strings)

            # -------------------------------------------------------------------------------
            # Direct reading from the PSX file - Mostly complete. PVR-T Textures unsupported
            # -------------------------------------------------------------------------------
            palette_4bit = read_palettes(reader, output_strings, 16)
            palette_8bit = read_palettes(reader, output_strings, 256)

            num_actual_tex = struct.unpack("<I", reader.read(4))[0]
            if num_actual_tex == 4294967295:
                raise Exception("Unable to parse PSX texture library, PVR-T?")
            update_file_table(file_index, {1: str(num_actual_tex)})
            if PRINT_OUTPUT:
                output_strings.append(f"Num actual textures: {num_actual_tex}")

            print_current_position(reader, output_strings)

            # Skip unknown data
            for i in range(num_actual_tex):
                reader.read(4)

            print_current_position(reader, output_strings)

            if num_actual_tex > 0 and FANCY_OUTPUT:
                output_strings.append(f"Extracting {num_actual_tex} textures from {filename}...")
                output_strings.append("| Texture | Width | Height | Bit-depth | Palette |  Offset  |")

            for i in range(num_actual_tex):
                header = get_texture_header(reader, output_strings, i + 1)
                tex_hashes[i] = tex_names[i]  # tex_hash

                # Now read the raw texture data
                pixels = []

                extraction_function = extraction_functions.get(header.pal_size)
                if extraction_function:
                    if PRINT_OUTPUT:
                        cur_texture = reader.tell()
                        output_strings.append(f"Image data starts at: {hex(cur_texture)}")
                    pixels = extraction_function(reader, header)
                elif PRINT_OUTPUT:
                    output_strings.append(f"Unsupported palette size ({header.pal_size}) for texture {i + 1}")

                if PRINT_OUTPUT:
                    output_strings.append(f"{header.index}: Finished reading texture. I am at: {hex(reader.tell())}")

                write_to_png(filename, output_dir, create_sub_dirs, header, pixels)
                textures_written += 1
    except Exception as error:
        report_status(num_actual_tex, update_file_table, file_index, textures_written)
        raise error
    finally:
        report_status(num_actual_tex, update_file_table, file_index, textures_written)


def skip_model_data(reader, output_strings):
    """Skip over model data to get to the texture information."""

    (ptr_meta, obj_count) = struct.unpack("<II", reader.read(8))
    if PRINT_OUTPUT:
        output_strings.append(f"Num objects: {obj_count}")

    # "Objects" are 36 bytes. Skip over them for reading textures.
    for _ in range(obj_count):
        # Skip over object data
        reader.read(36)

    # Determine number of meshes (we need to skip over the mesh name list before texture info)
    mesh_count = struct.unpack("<I", reader.read(4))[0]
    if PRINT_OUTPUT:
        output_strings.append(f"Num meshes: {mesh_count}")

    # Skip to the tagged chunks, find the textures
    reader.seek(ptr_meta)
    chunk_count = -1
    while True:
        magic = reader.read(4)
        chunk_count += 1
        if magic != b"\xFF\xFF\xFF\xFF":
            if PRINT_OUTPUT:
                output_strings.append(f"SKIPPED CHUNK: 0x{ magic.hex()}")
            unk_length = struct.unpack("<I", reader.read(4))[0]
            reader.read(unk_length)
            if chunk_count > 16:
                # There should not be this many tagged chunks, must be a file error
                raise Exception("Unable to parse PSX texture library, cannot find texture data")
        else:
            if PRINT_OUTPUT:
                output_strings.append("END OF TAGGED CHUNKS")
            break

    # Now we are at the model names list - if there are any models
    for _ in range(mesh_count):
        reader.read(4)


def read_texture_info(reader, output_strings):
    """Read texture information from the PSX file."""

    # Print position where texture data starts
    print_current_position(reader, output_strings)

    # Read number of textures
    num_tex = struct.unpack("<I", reader.read(4))[0]
    if PRINT_OUTPUT:
        output_strings.append(f"Num textures: {num_tex}")

    tex_names = []
    for _ in range(num_tex):
        tex_names.append(struct.unpack("<I", reader.read(4))[0])
    return tex_names


def read_palettes(reader, output_strings, num_colors):
    """Read palettes from the PSX file."""

    num_textures = struct.unpack("<I", reader.read(4))[0]
    if PRINT_OUTPUT:
        output_strings.append(f"Num {num_colors}-color tex: {num_textures}")
    palettes = []
    for _ in range(num_textures):
        this_pal = {"tex_id": struct.unpack("<I", reader.read(4))[0]}
        this_pal["color_data"] = struct.unpack(f"{num_colors}H", reader.read(num_colors * 2))
        palettes.append(this_pal)
    return palettes


def print_current_position(reader, output_strings):
    if PRINT_OUTPUT:
        output_strings.append(f"I am at: {hex(reader.tell())}")


def get_texture_header(reader, output_strings, num_tex):
    """Read the texture header from the PSX file."""

    header = PSXTextureHeader()
    header.offset = reader.tell()

    header.unk = struct.unpack("<I", reader.read(4))[0]
    header.pal_size = struct.unpack("<I", reader.read(4))[0]
    header.hash = struct.unpack("<I", reader.read(4))[0]
    header.index = struct.unpack("<I", reader.read(4))[0]
    header.width = struct.unpack("<H", reader.read(2))[0]
    header.height = struct.unpack("<H", reader.read(2))[0]

    # 16-bit textures have additional information in their headers
    if header.pal_size == 65536:
        header.pixel_format = struct.unpack("<I", reader.read(4))[0]
        header.size = struct.unpack("<I", reader.read(4))[0]

    header.texture_offset = reader.tell()

    if FANCY_OUTPUT:
        bit_depth_string = f"{int(log2(header.pal_size)): >2}-bit"
        palette_string = f"{header.pixel_format:#0{3}x}" if header.pal_size == 65536 else "N/A"
        output_strings.append(f"| {num_tex: >7} | {header.width: >5} | {header.height: >6} | {bit_depth_string: >9} | {palette_string: >7} | {header.offset:#0{8}x} |")

    return header


def report_status(num_actual_tex, update_file_table, file_index, textures_written):
    """Updates the PSX file table with the number of textures extracted and the status of the extraction for the current file."""

    if num_actual_tex > 0:
        update_file_table(file_index, {2: str(textures_written), 3: "OK" if num_actual_tex == textures_written else "ERROR"})
    else:
        update_file_table(file_index, {2: "0", 3: "SKIPPED"})
