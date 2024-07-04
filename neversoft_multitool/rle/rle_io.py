import os

import png


def filter_rle_files(self, file_list):
    return filter(lambda file: file.split(".")[-1].upper() == "RLE" or file.split(".")[-1].upper() == "BMR", file_list)


def write_to_png(self, filename, img_width, img_height, pixels):
    self.rle_files_converted += 1
    filename_without_extension = "".join(filename.split(".")[0:-1])
    output_path = os.path.join(self.rle_output_dir, f"{filename_without_extension}.png")
    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    input_file = open(output_path, "wb")
    writer = png.Writer(img_width, img_height, greyscale=False, alpha=False)
    writer.write(input_file, pixels)
    input_file.close()
