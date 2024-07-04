from common import get_file_extension


def is_archive_file(self, file_path):
    return get_file_extension(file_path) in ["WAD", "PKR", "PRE"]


def extract_pre_file(self, file_path):
    print(f"Extracting PRE File from {file_path}")
