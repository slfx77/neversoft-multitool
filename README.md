# Neversoft Image Tool

This is a combination of two previous tools I released: [Neversoft Bitmap Converter](https://github.com/slfx77/neversoft_bitmap_converter) and
[PSX Texture Extractor](https://github.com/slfx77/psx_texture_extractor).

## Features

- Extraction of textures from Neversoft PSX models from games released on the PS1, Dreamcast and PC. Possibly others.
- Conversion of RLE / BMR bitmap files used in Neversoft games on PS1 and PC. Possibly others.

Games tested:

- Apocalypse (PS1)
- Spider-Man (PS1, DC, PC)
- Tony Hawk's Pro Skater (PS1, DC)
- Tony Hawk's Pro Skater 2 (PS1, DC)

# Requirements

- [python3](https://www.python.org/)
- [pyqt6](https://pypi.org/project/PyQt6/) - can be installed with `pip install PyQt6`
- [pymorton](https://github.com/trevorprater/pymorton) - can be installed with `pip install pymorton`
- [pypng](https://github.com/drj11/pypng) - Included

# Usage Instructions

This program can be run by typing `py main.py` into a terminal in the directory it is cloned to.

## PSX Models

Select the folder containing the .psx files you wish to extract the textures from, and the folder to output the textures to. The default behavior will dump all textures in the
folder of your choice. By selecting the "Create Subdirectories" button, it will create a folder for each .psx file. Images will be output as PNGs.

## RLE / BMR Bitmaps

Select the folder containing the RLE / BMR files you wish to extract the textures from, the folder to output the images to, and optionally set the width. Images will be output as
PNGs. If the resulting images don't appear correct, you may try adjusting the width. The other valid widths I've encountered in testing are 640 and 768.

# Known bugs

- There is no handling for IO errors within the UI part of the code. As such, these may crash the program.
- PVR-T textures which are used in Xbox games such as THPS2X and possibly others are unsupported. The program tends to hang and use up a lot of CPU if you attempt to extract a file
  containing these.
- Dreamcast RLEs seemingly use an unsupported pixel format and won't appear correct when converted.

# To-Do List

- [ ] Set up multiprocessing for RLE conversion to speed up the process
- [ ] Update or remove .ui project file. It is out of date as I started to manually code the UI.
- [ ] Add support for Xbox PSX model extraction
- [ ] Determine if there's a way to calculate the width of RLE / BMR files. At the moment, the width has to be set manually.
- [ ] Add support for Dreamcast RLE files

# Credits

This program contains code from the following other projects:

- [io_thps_scene](https://github.com/denetii/io_thps_scene), a Blender plugin for multiple formats used in the Tony Hawk series of games as well as other games developed by
  Neversoft which use the same formats.
- [psx_extractor](https://github.com/krystalgamer/spidey-tools/tree/master/psx_extractor), an extractor intended for the PC version of Spider-Man. This is used for the decoding of
  PSX files with 16-bit textures. I've also created a standalone clone in Python that can be found [here](https://github.com/slfx77/psx_extract_py).
- [Rawtex](https://zenhax.com/viewtopic.php?t=7099), a multipurpose converter for raw texture files. Used to convert 16-bit textures with palette types 0x100-0x102 and 0xd01.
- [RLE-GIMP-Plugin](https://github.com/Daniel-McCarthy/RLE-GIMP-Plugin), a GIMP Plugin the support Neversoft RLE / BMR files. Supports saving in the format, alowing the in-game images to be modified.
- [pypng](https://github.com/drj11/pypng), used to write textures to PNG files.
