import os

import png
from neversoft_multitool.psx.psx_helpers import convert_16_bit_texture_for_pypng, fix_pixel_data


def write_to_png(filename, output_dir, create_sub_dirs, header, pixels):
    """Writes a texture to a PNG file."""
    filename_without_extension = "".join(filename.split(".")[0:-1])

    if create_sub_dirs:
        output_dir = os.path.join(output_dir, filename_without_extension)

    output_path = os.path.join(output_dir, f"{filename_without_extension}_{header.offset:#0{8}x}.png")

    if header.pal_size != 65536:
        write_image(output_path, header.width, header.height, fix_pixel_data(header.width, header.height, pixels))
    else:
        write_image(output_path, header.width, header.height, convert_16_bit_texture_for_pypng(header.pixel_format, header.width, pixels))


def write_image(output_path, width, height, final_image):
    """Writes a texture to a PNG file."""
    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    output_file = open(output_path, "wb")
    writer = png.Writer(width, height, greyscale=False, alpha=True)
    writer.write(output_file, final_image)
    output_file.close()
