import os
import sys
import traceback
from concurrent.futures import ProcessPoolExecutor, as_completed
from multiprocessing import Manager
from traceback import format_exception

from PyQt6.QtCore import QThread, pyqtSignal

from .extract_psx import extract_textures

PRINT_OUTPUT = True
PRINT_TRACEBACK = True


class PSXWorker(QThread):
    # Define custom PyQt signals for progress, completion, and updating the file table
    update_progress_bar_signal = pyqtSignal()
    update_file_table_signal = pyqtSignal(int, int, str)
    extraction_complete_signal = pyqtSignal()

    def __init__(self, files, input_dir, output_dir, file_table, create_sub_dirs):
        super().__init__()
        # Initialize instance variables
        self.files = files
        self.input_dir = input_dir
        self.output_dir = output_dir
        self.file_table = file_table
        self.create_sub_dirs = create_sub_dirs

    # Run the worker thread
    def run(self):
        # Get the number of available CPU cores
        max_workers = os.cpu_count()

        # Use a Manager for inter-process communication
        with Manager() as manager:
            queue = manager.Queue()  # Create a queue for sharing data between processes
            # Use a ProcessPoolExecutor for parallel processing
            with ProcessPoolExecutor(max_workers=max_workers) as executor:
                # Submit each file for processing and store the resulting Future objects
                futures = [
                    executor.submit(process_file, queue, filename, self.input_dir, self.output_dir, self.files.index(filename), self.create_sub_dirs) for filename in self.files
                ]

                # Continuously check if all futures are done
                while True:
                    if all(f.done() for f in futures):
                        break

                    # Process items in the queue until it is empty
                    while not queue.empty():
                        signal_type, *args = queue.get()
                        if signal_type == "update_file_table_signal":
                            self.update_file_table_signal.emit(*args)
                        elif signal_type == "update_progress_bar_signal":
                            self.update_progress_bar_signal.emit()

                # Iterate through the completed futures and handle any exceptions
                for future in as_completed(futures):
                    try:
                        future.result()
                    except Exception as error:
                        if PRINT_TRACEBACK:
                            exc_type, exc_value, exc_traceback = sys.exc_info()
                            traceback_msg = "".join(format_exception(exc_type, exc_value, exc_traceback))
                            print(f"An error occurred in the process: {error}\nTraceback: {traceback_msg}")

        # Emit the extraction complete signal to inform the GUI that the process is done
        self.extraction_complete_signal.emit()


# Function to process a single file
def process_file(queue, filename, input_dir, output_dir, file_index, create_sub_dirs):
    output_strings = []
    separator = "\n"

    def update_file_table(row, cols):
        for col, text in cols.items():
            queue.put(("update_file_table_signal", row, col, text))

    try:
        extract_textures(filename, input_dir, output_dir, file_index, create_sub_dirs, output_strings, update_file_table)
        if PRINT_OUTPUT:
            output_strings.append(f"Finished extracting textures from {filename}\n")
    except Exception as error:
        if PRINT_OUTPUT:
            output_strings.append(f"An error occurred while trying to extract from {filename}. The error was: {error}\n")
        if PRINT_TRACEBACK:
            traceback.print_exc()
    finally:
        queue.put(("update_progress_bar_signal",))
        if PRINT_OUTPUT and len(output_strings) > 0:
            print(separator.join(output_strings))
