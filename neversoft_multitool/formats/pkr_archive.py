import errno
import os
import struct
import zlib

from neversoft_multitool.formats.pkr_archive_header import FILE_COMPRESSED, FILE_UNCOMPRESSED, PKR3File, PKRDir, PKRDirHeader, PKRFile

# Constants
EXTRACT_BUF_SIZE = 0xFFFF


def setup_pkr_directories(fp):
    """Initialize and load PKR directories from a file."""
    pkr_header = read_pkr_file_header(fp)
    if not pkr_header:
        return None, None

    pkr_dir_header = read_pkr_dirs_header(fp, pkr_header)
    if not pkr_dir_header:
        print("Failed to get directory headers")
        return None, None

    pkr_dirs = allocate_pkr_directories(pkr_dir_header.num_dirs)
    if not pkr_dirs:
        return None, None

    if not load_pkr_directories(fp, pkr_dirs, pkr_dir_header):
        return None, None

    return pkr_dirs, pkr_dir_header


def read_pkr_file_header(fp):
    """Read and return the PKR3 file header from the file pointer."""
    try:
        header_format = "4sI"
        data = fp.read(struct.calcsize(header_format))
        if not data:
            print("Error reading the file.")
            return None

        magic_bytes, dir_offset = struct.unpack(header_format, data)
        if magic_bytes.decode("ascii").strip("\x00") != "PKR3":
            print("Invalid PKR3 Header.")
            return None

        return PKR3File(magic_bytes.decode("ascii").strip("\x00"), dir_offset)
    except Exception as e:
        print(f"Failed to read file header: {str(e)}")
        return None


def read_pkr_dirs_header(fp, pkr):
    """Read and return the PKR directory headers based on the file header info."""
    try:
        fp.seek(pkr.dir_offset, 0)
        dir_header_format = "III"
        data = fp.read(struct.calcsize(dir_header_format))
        if not data:
            print("Couldn't get the dirs of PKR")
            return None

        unk, num_dirs, num_files = struct.unpack(dir_header_format, data)
        return PKRDirHeader(unk, num_dirs, num_files)
    except Exception as e:
        print(f"Failed to read directory header: {str(e)}")
        return None


def allocate_pkr_directories(num_dirs):
    """Create a list of empty PKRDir objects based on the number of directories."""
    try:
        return [PKRDir("", 0, 0) for _ in range(num_dirs)]
    except Exception as e:
        print(f"Couldn't allocate space for the dirs: {str(e)}")
        return None


def load_pkr_directories(fp, pkr_dirs, pkr_dir_header):
    """Populate PKRDir objects with data from the file."""
    dir_format = "32sII"
    dir_size = struct.calcsize(dir_format)
    data = fp.read(dir_size * pkr_dir_header.num_dirs)

    if len(data) != dir_size * pkr_dir_header.num_dirs:
        print(f"Could only read {len(data) // dir_size} dirs.")
        return False

    for i in range(pkr_dir_header.num_dirs):
        offset = i * dir_size
        name_bytes, unk, num_files = struct.unpack_from(dir_format, data, offset)
        name = name_bytes.decode("ascii").strip("\x00")
        pkr_dirs[i].name = name
        pkr_dirs[i].unk = unk
        pkr_dirs[i].num_files = num_files

    return True


def create_extracted_directory():
    """Ensure the 'extracted' directory exists."""
    try:
        os.makedirs("extracted", exist_ok=True)
        return True
    except Exception as e:
        print(f"An error occurred creating extracted dir: {str(e)}")
        return False


def read_pkr_file(fp, file):
    """Read file metadata from the PKR archive into a PKRFile object."""
    file_format = "32sIIIII"
    size = struct.calcsize(file_format)
    data = fp.read(size)

    if len(data) == size:
        file.name, file.crc, file.compressed, file.file_offset, file.uncompressed_size, file.compressed_size = struct.unpack(file_format, data)
        return True
    else:
        return False


def create_directory(path):
    """Create a directory if it doesn't exist, handling potential errors."""
    try:
        os.makedirs(path, exist_ok=True)
        return True
    except OSError as e:
        if e.errno != errno.EEXIST:
            print(f"An error occurred while creating the directory: {os.strerror(e.errno)} ({e.errno:08X})")
            return False


def extract_directory(fp, cur_dir):
    """Extract all files within a specified directory from the PKR archive."""
    extracted_path = os.path.join("extracted", cur_dir.name)
    if not create_directory(extracted_path):
        return False

    for _ in range(cur_dir.num_files):
        extracted_file = PKRFile("", 0, 0, 0, 0, 0)
        if not read_pkr_file(fp, extracted_file):
            print("Error reading file...")
            return False

        # Process based on compression type
        if extracted_file.compressed == FILE_COMPRESSED:
            if not extract_compressed_file(fp, extracted_file, extracted_path):
                return False
        elif extracted_file.compressed == FILE_UNCOMPRESSED:
            if not extract_uncompressed_file(fp, extracted_file, extracted_path):
                return False
        else:
            print(f"Unknown compression type: {extracted_file.compressed:08X}... Quitting")
            return False

    return True


def extract_uncompressed_file(fp, file, path):
    """Extract an uncompressed file to disk, verifying its CRC checksum."""
    if is_already_extracted(file, path):
        return True

    if not read_file_data(fp, file):
        return False

    return write_data_to_disk(file, file.data, path)


def extract_compressed_file(fp, file, path):
    """Decompress and extract a compressed file to disk after verifying its CRC checksum."""
    if is_already_extracted(file, path):
        return True

    if not read_file_data(fp, file):
        return False

    decompressed_data = decompress_data(file)
    if decompressed_data is None:
        return False

    return write_data_to_disk(file, decompressed_data, path)


def read_file_data(fp, file):
    """Retrieve file data from the archive, adjusting file pointer as necessary."""
    try:
        original_fp = fp.tell()
        fp.seek(file.file_offset)
        file_size = file.uncompressed_size if file.compressed == FILE_UNCOMPRESSED else file.compressed_size

        data = fp.read(file_size)
        if len(data) != file_size:
            print(f"Could not read file {file.name}")
            return False

        file.data = data
        fp.seek(original_fp)
        return True
    except Exception as e:
        print(f"An error occurred: {str(e)}")
        return False


def write_data_to_disk(file, data, path):
    """Write data to disk at the specified path."""
    try:
        full_path = os.path.join(path, file.name.decode("utf-8").strip("\x00"))
        with open(full_path, "wb") as out:
            if not verify_crc(file, data):
                print(f"Invalid CRC for {file.name}")
                return False

            out.write(data)
        return True
    except IOError as e:
        print(f"Could not create the extracted file <{full_path}>\n{str(e)}")
        return False


def is_already_extracted(file, path):
    """Check if a file has already been extracted to the specified path."""
    full_path = os.path.join(path, file.name.decode("utf-8").strip("\x00"))
    return os.path.isfile(full_path)


def decompress_data(file):
    """Decompress file data using zlib and return the decompressed data if successful."""
    try:
        decompressed_data = zlib.decompress(file.data, bufsize=file.uncompressed_size)
        if len(decompressed_data) != file.uncompressed_size:
            print("Error decompressing the file :(")
            return None
        return decompressed_data
    except (MemoryError, zlib.error) as e:
        print(f"Error decompressing the file: {str(e)}")
        return None


def verify_crc(file, data):
    """Verify the CRC32 checksum of the data against the expected CRC."""
    return zlib.crc32(data) & 0xFFFFFFFF == file.crc


def setup_pkr_directories(fp):
    """Set up PKR directories by reading the file header and directory headers."""
    pkr_header = read_pkr_file_header(fp)
    if not pkr_header:
        return None, None

    pkr_dir_header = read_pkr_dirs_header(fp, pkr_header)
    if not pkr_dir_header:
        print("Failed to get directory headers")
        return None, None

    pkr_dirs = allocate_pkr_directories(pkr_dir_header.num_dirs)
    if not pkr_dirs:
        return None, None

    if not load_pkr_directories(fp, pkr_dirs, pkr_dir_header):
        return None, None

    return pkr_dirs, pkr_dir_header


def read_pkr_file_header(fp):
    """Reads the PKR3 file header from the file pointer."""
    try:
        data = fp.read(struct.calcsize("4sI"))
        if not data:
            print("Error reading the file.")
            return None

        magic_bytes, dir_offset = struct.unpack("4sI", data)
        magic_str = magic_bytes.decode("ascii").strip("\x00")

        if magic_str != "PKR3":
            print("Invalid PKR3 Header.")
            return None

        return PKR3File(magic_str, dir_offset)
    except Exception as e:
        print(f"Failed to read file header: {str(e)}")
        return None


def read_pkr_dirs_header(fp, pkr):
    """Reads the PKR directory headers from the file pointer."""
    try:
        fp.seek(pkr.dir_offset, 0)
        data = fp.read(struct.calcsize("III"))
        if not data:
            print("Couldn't get the dirs of PKR")
            return None

        unk, num_dirs, num_files = struct.unpack("III", data)
        print(f"There are {num_dirs} dirs and {num_files} files")

        return PKRDirHeader(unk, num_dirs, num_files)
    except Exception as e:
        print(f"Failed to read directory header: {str(e)}")
        return None


def allocate_pkr_directories(num_dirs):
    """Allocates space for the PKR directories."""
    try:
        return [PKRDir("", 0, 0) for _ in range(num_dirs)]
    except Exception as e:
        print(f"Couldn't allocate space for the dirs: {str(e)}")
        return None


def load_pkr_directories(fp, pkr_dirs, pkr_dir_header):
    """Loads the PKR directories from the file pointer."""
    dir_format = "32sII"
    dir_size = struct.calcsize(dir_format)
    data = fp.read(dir_size * pkr_dir_header.num_dirs)

    if len(data) != dir_size * pkr_dir_header.num_dirs:
        print(f"Could only read {len(data) // dir_size} dirs.")
        return False

    for i in range(pkr_dir_header.num_dirs):
        offset = i * dir_size
        name_bytes, unk, num_files = struct.unpack_from(dir_format, data, offset)
        name = name_bytes.decode("ascii").strip("\x00")
        pkr_dirs[i].name = name
        pkr_dirs[i].unk = unk
        pkr_dirs[i].num_files = num_files
        print(f"{name} has {num_files} files")

    return True


def extract_all_directories(fp, pkr_dirs, pkr_dir_header):
    """Extracts all directories from the PKR file."""
    if not create_extracted_directory():
        return

    for i in range(pkr_dir_header.num_dirs):
        dir_name = pkr_dirs[i].name
        print(f"Extracting {dir_name}")

        if not extract_directory(fp, pkr_dirs[i]):
            print("An error occurred")
            return


def create_extracted_directory():
    """Creates the extracted directory if it does not exist."""
    try:
        os.makedirs("extracted", exist_ok=True)
        return True
    except Exception as e:
        print(f"An error occurred creating extracted dir: {str(e)}")
        return False


def extract_pkr_file(self, filename, output_dir):
    """Main function to handle PKR file extraction."""

    try:
        with open(filename, "rb") as fp:
            pkr_dirs, pkr_dir_header = setup_pkr_directories(fp)
            if pkr_dirs and pkr_dir_header:
                extract_all_directories(fp, pkr_dirs, pkr_dir_header)
            else:
                return 1  # Return an error if the setup was not successful
    except FileNotFoundError:
        print(f"Error: File '{filename}' not found.")
        return 2
    except Exception as e:
        print(f"An unexpected error occurred: {str(e)}")
        return 3

    return 0
