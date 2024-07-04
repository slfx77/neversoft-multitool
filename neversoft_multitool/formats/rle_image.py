import os
import struct

############
##   IO   ##
############


def get_file_size(file):
    original_pos = file.tell()
    file.seek(0, os.SEEK_END)
    end_pos = file.tell()

    file.seek(original_pos)
    return end_pos


def load_file(filename):
    return open(filename, "rb")


def read_int_little_endian(file):
    return struct.unpack("<I", file.read(4))[0]


#####################
##   Entry Point   ##
#####################


def convert(filename, width):
    file_extension = os.path.splitext(filename)
    if ".bmr" not in file_extension and ".rle" not in file_extension and ".BMR" not in file_extension and ".RLE" not in file_extension:
        raise Exception("File not supported. Expected .rle or .bmr, but got: '{0}'.".format(file_extension))

    input_file = load_file(filename)
    # Load raw format if has .bmr extension
    if ".bmr" in file_extension or ".BMR" in file_extension:
        return load_bmr(input_file, width)

    # Load encoded RLE format if has expected magic number
    if verify_file_is_rle(input_file):
        return load_rle(input_file, width)

    raise Exception('Failed to load image "{0}". No _RLE_16_ magic number found, therefore is an invalid RLE image'.format(input_file.name))


########################
##   Color Handling   ##
########################


def convert_rgba5551_to_rgba32(rgba5551_short):
    r = rgba5551_short & 0b11111
    g = (rgba5551_short >> 5) & 0b11111
    b = (rgba5551_short >> 10) & 0b11111

    r <<= 3
    g <<= 3
    b <<= 3

    return Color(r, g, b)


def convert_canvas_to_pypng(canvas):
    # Convert from canvas to RGB array.
    pixels = []
    pixel_row = []

    for row in canvas:
        for pixel in row:
            pixel_row += [pixel.r, pixel.g, pixel.b]
        pixels.append(pixel_row)
        pixel_row = []

    return pixels


class Color:
    def __init__(self, r, g, b):
        self.r = r
        self.g = g
        self.b = b

    def __eq__(self, other):
        return isinstance(other, Color) and (self.r == other.r) and (self.g == other.g) and (self.b == other.b)

    def __ne__(self, other):
        # Necessary in Python 2 for !=
        return not self.__eq__(other)


###################################
##   RLE & BMR Format Handling   ##
###################################


def verify_file_is_rle(file):
    magic_number_text = "_RLE_16_"
    first_8_bytes = file.read(8)
    return first_8_bytes.decode("utf-8") in magic_number_text


# Note: The width changes on a per game basis. There is no header data
# to determine the width for the image from. 512px is for Spider-Man 1 (PSX).


def load_bmr(file, width):
    file_size = get_file_size(file)
    canvas = []
    row = []

    # Read in and convert all colors to 32 bit RGBA
    for _ in range(0, file_size, 2):
        color_bytes = struct.unpack("<H", file.read(2))[0]
        color = convert_rgba5551_to_rgba32(color_bytes)
        row.append(color)

        # Start new row when we hit the width
        if len(row) >= width:
            canvas.append(row)
            row = []

    # If a row wasn't finished, add it to the image anyways.
    # if len(row) > 0:
    #     canvas.append(row)

    # Rotate pixels on wrong side of image if any are found
    canvas = unshift_columns(canvas)
    return convert_canvas_to_pypng(canvas)


class EncodedFlags:
    READ_NUM_COLORS = 0x00
    REPEAT_COLOR = 0x80


def load_rle(file, max_width):
    # Skip header / magic number
    header_length = 8
    file_size = get_file_size(file)
    file.seek(header_length)

    # Determine size of image
    decompressed_file_size = read_int_little_endian(file) - header_length
    total_rows = (decompressed_file_size / 2) / max_width

    canvas = []
    row = []
    row_len = 0
    quantity_bits = 0b0111111111111111

    # Read in, decode, & convert all colors to 32 bit RGBA
    while file.tell() + 1 < file_size and len(canvas) < total_rows:
        byte_1 = struct.unpack("<B", file.read(1))[0]
        byte_2 = struct.unpack("<B", file.read(1))[0]

        quantity = (byte_1 | (byte_2 << 8)) & quantity_bits
        flag = EncodedFlags.REPEAT_COLOR if ((byte_2 & 0x80) > 0) else EncodedFlags.READ_NUM_COLORS

        if flag == EncodedFlags.READ_NUM_COLORS:
            for _ in range(0, quantity):
                color_bytes = struct.unpack("<H", file.read(2))[0]
                color = convert_rgba5551_to_rgba32(color_bytes)
                row.append(color)
                row_len += 1

                if row_len >= max_width:
                    canvas.append(row)
                    row = []
                    row_len = 0

        elif flag == EncodedFlags.REPEAT_COLOR:
            color_bytes = struct.unpack("<H", file.read(2))[0]
            color = convert_rgba5551_to_rgba32(color_bytes)

            for _ in range(0, quantity):
                row.append(color)
                row_len += 1
                if row_len >= max_width:
                    canvas.append(row)
                    row = []
                    row_len = 0
        else:
            raise Exception("Unsupported flag type found at byte {0}. Flag found: {1}.".format(file.tell(), flag))

    # If a row wasn't finished, add it to the image anyways.
    # if row_len > 0:
    #     canvas.append(row)

    # Rotate pixels on wrong side of image if any are found
    canvas = unshift_columns(canvas)
    return convert_canvas_to_pypng(canvas)


def unshift_columns(canvas):
    blue_color = Color(0, 0, 144)
    blue_color2 = Color(0, 0, 208)

    first_row = canvas[0]
    row_len = len(first_row)
    last_pixel = first_row[row_len - 1]
    matches_blue = last_pixel == blue_color or last_pixel == blue_color2

    # This blue pixel indicates the column has been encoded
    # If it's not present, no shifting is present.
    if not matches_blue:
        return canvas

    # Move final two columns to start of the image
    # RLE encodes these columns to the end of the rows
    for i in range(0, len(canvas)):
        current_row = canvas[i]
        current_row.insert(0, current_row.pop(len(current_row) - 1))
        current_row.insert(0, current_row.pop(len(current_row) - 1))
        canvas[i] = current_row

    # Moves each pixel of the first two columns up
    # Pixels are shifted down and top pixels are encoded
    for i in range(0, len(canvas)):
        if i + 2 < len(canvas):
            current_row = canvas[i]
            next_row = canvas[i + 1]
            current_row[0] = next_row[0]
            current_row[1] = next_row[1]
            canvas[i] = current_row
    return canvas
