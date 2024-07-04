import os

from common import align, get_filename_without_extension, read8, read32, read_string  # Import all functions from the common module, typically custom utilities


# Credit to JayRedFox: https://github.com/JayFoxRox/thps2-tools/blob/master/extract-hed-wad.py
def extract_wad_file(self, wad_path, output_path):
    print(f"Extracting files from WAD {wad_path} to {output_path}.")

    archive_name = get_filename_without_extension(wad_path)
    hed_path = get_hed_file(self, wad_path)
    file_count = 0

    # Open the HED file which contains the directory information
    with open(hed_path, "rb") as f:

        # Move the cursor to the end of the file to determine its size
        f.seek(0, os.SEEK_END)
        file_size = f.tell()  # Store the size of the file
        f.seek(0)  # Reset the cursor to the beginning of the file

        # Open the WAD file which contains the actual data
        with open(wad_path, "rb") as fw:

            # Continue reading entries in the HED file until we reach the end minus 7 bytes (to prevent overflow)
            while f.tell() < file_size - 7:
                name = read_string(f)  # Read the name of the entry (a string)
                align(f, 4)  # Align the file cursor to a 4-byte boundary if necessary
                offset = read32(f)  # Read a 32-bit integer representing the data offset in the WAD file
                size = read32(f)  # Read a 32-bit integer representing the size of the data

                fw.seek(offset)  # Move the cursor of the WAD file to the offset of the data

                # Construct the path where the extracted file will be stored
                file_export_path = os.path.join(output_path, archive_name, name)

                # Create directories as needed based on the file path
                os.makedirs(os.path.dirname(file_export_path), exist_ok=True)

                # Open the destination file in write-binary mode and extract the data
                with open(file_export_path, "wb") as fo:
                    data = fw.read(size)  # Read the data chunk from the WAD file
                    fo.write(data)  # Write the data to the output file

                self.archive_ui.archive_file_table.setItem(file_count, 2, self.get_table_item("OK"))
                file_count += 1
                self.archive_ui.archive_progress_bar.setValue(round(file_count / self.number_of_files_in_archive * 100))

        # After processing all entries, read the final byte which should be a terminator
        terminator = read8(f)
        assert terminator == 0xFF  # Ensure the terminator is correct (0xFF), acting as a basic file integrity check
        self.archive_ui.archive_progress_bar.setValue(100)
        print("Extraction complete.")


def get_hed_file(_, wad_path):
    # Get the directory and base name of the file path
    directory = os.path.dirname(wad_path)
    base_name = os.path.basename(wad_path)

    # Replace the extension with '.HED'
    if "." in base_name:
        # Find the last dot and replace the extension after this with '.HED'
        hed_path = ".".join(base_name.split(".")[:-1]) + ".HED"
    else:
        # If there's no extension, just append '.HED'
        hed_path = base_name + ".HED"

    # Construct the full path to the .HED file
    hed_path = os.path.join(directory, hed_path)

    print(f"Checking if {hed_path} exists...")

    # Check if the file exists and print a message or raise an exception
    if os.path.isfile(hed_path):
        return hed_path
    else:
        raise Exception(f"{hed_path} not found.")


def get_hed_file_list(self, wad_path):
    hed_path = get_hed_file(self, wad_path)
    file_count = 0
    with open(hed_path, "rb") as reader:
        # Move the cursor to the end of the file to determine its size
        reader.seek(0, os.SEEK_END)
        file_size = reader.tell()  # Store the size of the file
        reader.seek(0)  # Reset the cursor to the beginning of the file

        # Continue reading entries in the HED file until we reach the end minus 7 bytes (to prevent overflow)
        while reader.tell() < file_size - 7:
            # Read the file information from the HED file
            name = read_string(reader)  # Read the file name as a string
            align(reader, 4)  # Align the file cursor to a 4-byte boundary if necessary
            read32(reader)  # Skip over the file offset
            size = read32(reader)  # Read the filesize in bytes

            # Add the file information to the table in the UI
            self.archive_ui.archive_file_table.setRowCount(file_count + 1)
            self.archive_ui.archive_file_table.setItem(file_count, 0, self.get_table_item(name))
            self.archive_ui.archive_file_table.setItem(file_count, 1, self.get_table_item(str(size), True))

            file_count += 1

        # After processing all entries, read the final byte which should be a terminator
        terminator = read8(reader)
        assert terminator == 0xFF  # Ensure the terminator is correct (0xFF), acting as a basic file integrity check
        self.number_of_files_in_archive = file_count - 1
