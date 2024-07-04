from neversoft_multitool.psx.psx_helpers import ps1_to_32bpp


def extract_4bit_texture(reader, header, palette_4bit):
    """Extracts a 4-bit texture from a PSX file."""

    pad_width = (header.width + 0x3) & ~0x3
    pad_width >>= 1
    real_len = (pad_width * header.height) + get_padding_amount(header, pad_width)
    pal_indices = reader.read(real_len)

    # Find the palette and build the image
    for pal in palette_4bit:
        if pal["tex_id"] == header.hash:
            pixels = [None] * (header.width * header.height)
            for y in range(header.height):
                for x in range(header.width):
                    color_index = (pal_indices[y * pad_width + (x >> 1)] >> ((x & 0x1) * 4)) & 0xF
                    color = pal["color_data"][color_index]
                    pixel = ps1_to_32bpp(color)
                    pixels[y * header.width - x] = pixel
            break
    return pixels


def extract_8bit_texture(reader, header, palette_8bit):
    """Extracts an 8-bit texture from a PSX file."""

    pad_width = (header.width + 0x1) & ~0x1
    real_len = (pad_width * header.height) + get_padding_amount(header, pad_width)
    pal_indices = reader.read(real_len)

    # Find the palette and build the image
    for pal in palette_8bit:
        if pal["tex_id"] == header.hash:
            pixels = [None] * (header.width * header.height)
            for y in range(header.height):
                for x in range(header.width):
                    color_index = (pal_indices[y * pad_width + x]) & 0xFF
                    color = pal["color_data"][color_index]
                    pixel = ps1_to_32bpp(color)
                    pixels[y * header.width - x] = pixel
            break
    return pixels


def get_padding_amount(header, pad_width):
    if header.height % 2 != 0:
        return 2 if pad_width % 4 != 0 else 0
    return 0
