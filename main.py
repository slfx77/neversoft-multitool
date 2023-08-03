import os
import sys
import traceback
from datetime import datetime

from PyQt6.QtCore import pyqtSlot
from PyQt6.QtWidgets import QApplication, QFileDialog, QMainWindow, QTableWidgetItem

from main_window_ui import MainWindowUi
from numeric_table_widget_item import NumericTableWidgetItem
from printer import Printer
from psx.psx_tab import PSXWorker
from rle.rle_helper import convert
from rle.rle_tab import filter_rle_files, write_to_png

# PSX PANEL SPECIFIC CODE STARTS HERE
PRINT_OUTPUT = True
PRINT_TRACEBACK = True


# PSX PANEL SPECIFIC CODE ENDS HERE


# Define main window class, inherits QMainWindow and UiMainWindow
class Window(QMainWindow, MainWindowUi):
    # PSX
    psx_input_dir = ""
    psx_output_dir = ""
    current_psx_files = []
    psx_files_processed = 0
    psx_create_sub_dirs = False
    # PSX

    # RLE
    printer = Printer()
    printer.on = True

    rle_input_dir = ""
    rle_output_dir = ""
    current_rle_files = []
    rle_files_converted = 0
    # RLE

    start_time = 0

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setup_ui(self)

    # PSX PANEL SPECIFIC CODE STARTS HERE
    # Open a directory picker and set the input directory path
    def psx_input_browse_clicked(self):
        dir_name = QFileDialog.getExistingDirectory(self, "Choose Directory", "")
        if dir_name:
            self.psx_input_dir = dir_name
            self.current_psx_files = []
            self.psx_ui.psx_file_table.setRowCount(0)
            self.get_psx_files(dir_name)

    # Filter files with .psx or .PSX extensions
    def filter_psx_files(self, file_list):
        return [f for f in file_list if f.lower().endswith((".psx", ".PSX"))]

    # Get .psx files from the chosen directory and update the GUI
    def get_psx_files(self, dir_name):
        self.psx_ui.psx_input_path.setText(dir_name)
        dir_files = [f for f in os.listdir(dir_name) if os.path.isfile(os.path.join(dir_name, f))]
        psx_files = list(self.filter_psx_files(dir_files))
        if len(psx_files) > 0:
            self.psx_ui.psx_file_table.setRowCount(len(psx_files))
            for row, file in enumerate(psx_files):
                self.current_psx_files.append(file)
                self.psx_ui.psx_file_table.setItem(row, 0, QTableWidgetItem(file))
            if self.psx_output_dir:
                self.psx_ui.psx_extract_button.setEnabled(True)
        else:
            self.psx_ui.psx_extract_button.setEnabled(False)

    # Open a directory picker and set the output directory path
    def psx_output_browse_clicked(self):
        dir_name = QFileDialog.getExistingDirectory(self, "Choose Directory", "")
        if dir_name:
            self.psx_output_dir = dir_name
            self.psx_ui.psx_output_path.setText(dir_name)
            if len(self.current_psx_files) > 0:
                self.psx_ui.psx_extract_button.setEnabled(True)
            else:
                self.psx_ui.psx_extract_button.setEnabled(False)

    # Clear the Textures Extracted and Status columns
    def psx_clear_columns(self):
        for row in range(self.psx_ui.psx_file_table.rowCount()):
            self.psx_ui.psx_file_table.setItem(row, 2, NumericTableWidgetItem(""))
            self.psx_ui.psx_file_table.setItem(row, 3, NumericTableWidgetItem(""))

    # Start the extraction process when the extract button is clicked
    def psx_extract_clicked(self):
        # Cleanup previous state
        self.psx_clear_columns()
        self.psx_ui.psx_progress_bar.setValue(0)
        self.psx_ui.psx_extract_button.setEnabled(False)
        self.psx_ui.psx_file_table.setSortingEnabled(False)
        self.psx_files_processed = 0

        # Start the extraction process
        self.start_time = datetime.now()
        self.worker = PSXWorker(self.current_psx_files, self.psx_input_dir, self.psx_output_dir, self.psx_ui.psx_file_table, self.psx_create_sub_dirs)
        self.worker.update_progress_bar_signal.connect(self.psx_update_progress_bar)
        self.worker.extraction_complete_signal.connect(self.psx_extraction_complete)
        self.worker.update_file_table_signal.connect(self.update_psx_file_table)
        self.worker.start()

    # Update the progress bar based on the number of files processed
    @pyqtSlot()
    def psx_update_progress_bar(self):
        self.psx_files_processed += 1
        progress = round(self.psx_files_processed / len(self.current_psx_files) * 100)
        self.psx_ui.psx_progress_bar.setValue(progress)

    # Update the file table in the GUI
    @pyqtSlot(int, int, str)
    def update_psx_file_table(self, row, col, text):
        self.psx_ui.psx_file_table.setItem(row, col, NumericTableWidgetItem(text)) if col in [1, 2] else self.psx_ui.psx_file_table.setItem(row, col, QTableWidgetItem(text))

    # Update the UI and display the time elapsed when the extraction is complete
    @pyqtSlot()
    def psx_extraction_complete(self):
        self.psx_ui.psx_progress_bar.setValue(100)
        self.psx_ui.psx_extract_button.setEnabled(True)
        self.psx_ui.psx_file_table.setSortingEnabled(True)
        self.status_bar.showMessage(f"Time elapsed: {(datetime.now() - self.start_time).total_seconds()}")

    # Toggle the create_sub_dirs boolean when the Create Subdirectories checkbox is clicked
    def psx_create_sub_dirs_clicked(self):
        self.psx_create_sub_dirs = not self.psx_create_sub_dirs

    # PSX PANEL SPECIFIC CODE ENDS HERE

    # RLE / BMR PANEL SPECIFIC CODE STARTS HERE
    def rle_input_browse_clicked(self):
        dir_name = QFileDialog.getExistingDirectory(self, "Choose Directory", "")
        if dir_name:
            self.rle_input_dir = dir_name
            self.current_rle_files = []
            self.rle_ui.rle_file_table.setRowCount(0)
            self.get_rle_files(dir_name)

    def get_rle_files(self, dir_name):
        self.rle_ui.rle_input_path.setText(dir_name)
        dir_files = [f for f in os.listdir(dir_name) if os.path.isfile(os.path.join(dir_name, f))]
        rle_files = list(filter_rle_files(self, dir_files))
        if len(rle_files) > 0:
            self.rle_ui.rle_file_table.setRowCount(len(rle_files))
            for row, file in enumerate(rle_files):
                self.current_rle_files.append(file)
                self.rle_ui.rle_file_table.setItem(row, 0, QTableWidgetItem(file))
            if self.rle_output_dir != "":
                self.rle_ui.rle_convert_button.setEnabled(True)
        else:
            self.rle_ui.rle_convert_button.setEnabled(False)

    def rle_output_browse_clicked(self):
        dir_name = QFileDialog.getExistingDirectory(self, "Choose Directory", "")
        if dir_name:
            self.rle_output_dir = dir_name
            self.rle_ui.rle_output_path.setText(dir_name)
            if len(self.current_rle_files) > 0:
                self.rle_ui.rle_convert_button.setEnabled(True)
            else:
                self.rle_ui.rle_convert_button.setEnabled(False)

    def rle_convert_clicked(self):
        self.rle_ui.rle_progress_bar.setValue(0)
        for index, filename in enumerate(self.current_rle_files):
            try:
                input_file = os.path.join(self.rle_input_dir, filename)
                width = self.rle_ui.rle_width_selector.value()
                pixels = convert(input_file, width)
                write_to_png(self, filename, width, len(pixels), pixels)
                self.rle_ui.rle_file_table.setItem(index, 1, QTableWidgetItem("OK"))
            except Exception as e:
                self.printer("An error ocurred while trying to convert {}. The error was: {}", filename, e)
                if PRINT_TRACEBACK:
                    traceback.print_exc()
                self.rle_ui.rle_file_table.setItem(index, 1, QTableWidgetItem("ERROR"))
            self.rle_ui.rle_progress_bar.setValue(round(index / len(self.current_rle_files) * 100))
        self.rle_ui.rle_progress_bar.setValue(100)

    # RLE / BMR PANEL SPECIFIC CODE ENDS HERE

    def tab_changed(self, num):
        print(f"Tab changed: {num}")


if __name__ == "__main__":
    app = QApplication(sys.argv)
    win = Window()
    win.show()
    sys.exit(app.exec())
