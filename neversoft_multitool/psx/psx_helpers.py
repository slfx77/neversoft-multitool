# Alpha is either 0 or 128 for some reason
argb1555_params = {
    "red_mask": 0x7C00,
    "green_mask": 0x3E0,
    "blue_mask": 0x1F,
    "alpha_mask": 0x8000,
    "red_max": 31,
    "green_max": 31,
    "blue_max": 31,
    "alpha_max": 1,
    "alpha_shift": 15,
    "red_shift": 10,
    "green_shift": 5,
}

# 565
rgb565_params = {
    "red_mask": 0xF800,
    "green_mask": 0x7E0,
    "blue_mask": 0x1F,
    "alpha_mask": 0,
    "red_max": 31,
    "green_max": 63,
    "blue_max": 31,
    "alpha_max": 0,
    "alpha_shift": 16,
    "red_shift": 11,
    "green_shift": 5,
}

# 4444
argb4444_params = {
    "red_mask": 0xF00,
    "green_mask": 0xF0,
    "blue_mask": 0xF,
    "alpha_mask": 0xF000,
    "red_max": 15,
    "green_max": 15,
    "blue_max": 15,
    "alpha_max": 15,
    "alpha_shift": 12,
    "red_shift": 8,
    "green_shift": 4,
}


def ps1_to_32bpp(color):
    """Converts a 16-bit PS1 color to a 32-bit RGBA color."""

    r = (color) & 0x1F
    g = (color >> 5) & 0x1F
    b = (color >> 10) & 0x1F

    # Fully transparent
    if r == 31 and g == 0 and b == 31:
        return [0, 0, 0, 0]

    return [int((r / 31) * 255), int((g / 31) * 255), int((b / 31) * 255), 255]


def get_16bpp_color_params(pixel_format):
    """Gets the color parameters for a 16-bit texture."""

    # 0x00 = ARGB1555 (bilevel translucent alpha 0,255)
    if pixel_format & 0xF == 0:
        return argb1555_params

    # 0x01 = RGB565 (no translucent)
    if pixel_format & 0xF == 1:
        return rgb565_params

    # 0x02 = ARGB4444 (translucent alpha 0-255)
    return argb4444_params

    # 0x03 - 0x06 are other palette types supported by the PVR format, but don't seem to be used by Neversoft


def convert_16bpp_to_32bpp(params, color):
    """Converts a 16-bit color to a 32-bit color."""

    r = (color & params["red_mask"]) >> params["red_shift"]
    g = (color & params["green_mask"]) >> params["green_shift"]
    b = color & params["blue_mask"]
    a = (color & params["alpha_mask"]) >> params["alpha_shift"]

    r = int((r / params["red_max"]) * 255)
    g = int((g / params["green_max"]) * 255)
    b = int((b / params["blue_max"]) * 255)

    a = 255 if params["alpha_max"] == 0 else int((a / params["alpha_max"]) * 255)

    return [r, g, b, a]


def convert_16_bit_texture_for_pypng(pixel_format, width, texture):
    """Converts a 16-bit texture to a 32-bit texture for use with PyPNG."""

    params = get_16bpp_color_params(pixel_format)

    pixels = []
    pixel_row = []

    for i in texture:
        pixel_row += convert_16bpp_to_32bpp(params, i)
        if len(pixel_row) == width * 4:
            pixels.append(pixel_row)
            pixel_row = []

    return pixels


# IO THPS Scene Image Correction


def fix_pixel_data(width, height, pixels):
    """Fixes the pixel data of a texture read by the IO THPS scene code."""
    initial_image = []
    for row in range(0, height):
        cur_row = []
        for col in reversed(range(row * width, (row + 1) * width)):
            cur_row.extend(pixels[col])
        shifted_right = shift_row_pixels(cur_row, 1)
        initial_image.append(shifted_right)
    shifted_down = shift_image_rows(initial_image, 1)
    return shift_image_column(shifted_down, 0, -1, height)


def shift_row_pixels(row_pixels, shift_amount):
    """Shifts the pixels in a row by a specified amount."""
    shifted_row = []
    shifted_row.extend(row_pixels[shift_amount * -4 :])
    shifted_row.extend(row_pixels[0 : shift_amount * -4])
    return shifted_row


def shift_image_rows(image_data, shift_amount):
    """Shifts the rows in an image by a specified amount."""
    shifted_image = image_data.copy()
    for _ in range(shift_amount):
        new_rows = []
        new_rows.append(shifted_image[-1])
        new_rows.extend(shifted_image[0:-1])
        shifted_image = new_rows
    return shifted_image


def shift_image_column(image_data, col_index, shift_amount, image_height):
    """Shifts a column in an image by a specified amount."""
    column_data = []
    col_start_index = col_index * 4
    for row_index in range(image_height):
        column_data.extend(image_data[row_index][col_start_index : col_start_index + 4])
    shifted_column = shift_row_pixels(column_data, shift_amount)
    new_image_data = []
    for row_index in range(image_height):
        if col_index != 0:
            new_image_data.append(image_data[row_index][0:col_start_index])
        else:
            new_image_data.append([])
        new_image_data[row_index].extend(shifted_column[row_index * 4 : row_index * 4 + 4])
        new_image_data[row_index].extend(image_data[row_index][col_start_index + 4 :])
    return new_image_data


# End IO THPS Scene Image Correction
