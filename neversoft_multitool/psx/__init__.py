from PyQt6 import QtCore, QtWidgets


class PsxUi(object):
    def __init__(self):
        self.setup_ui()

    def setup_ui(self):
        self.psx_tab = QtWidgets.QWidget()
        self.psx_tab.setObjectName("psx_tab")

        # Layout
        self.psx_tab_grid_layout = QtWidgets.QGridLayout(self.psx_tab)
        self.psx_tab_grid_layout.setContentsMargins(9, 9, 9, 9)
        self.psx_tab_grid_layout.setObjectName("psx_tab_grid_layout")
        self.psx_vertical_layout = QtWidgets.QVBoxLayout()
        self.psx_vertical_layout.setObjectName("psx_vertical_layout")

        # Input row
        self.psx_input_row = QtWidgets.QHBoxLayout()
        self.psx_input_row.setObjectName("psx_input_row")

        ## Input path label
        self.psx_input_label = QtWidgets.QLabel(parent=self.psx_tab)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Policy.Preferred)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.psx_input_label.sizePolicy().hasHeightForWidth())
        self.psx_input_label.setSizePolicy(size_policy)
        self.psx_input_label.setMinimumSize(QtCore.QSize(90, 0))
        self.psx_input_label.setObjectName("psx_input_label")
        self.psx_input_row.addWidget(self.psx_input_label)

        ## Input path
        self.psx_input_path = QtWidgets.QLineEdit(parent=self.psx_tab)
        self.psx_input_path.setEnabled(True)
        self.psx_input_path.setReadOnly(True)
        self.psx_input_path.setObjectName("psx_input_path")
        self.psx_input_row.addWidget(self.psx_input_path)

        ## Input path browse button
        self.psx_input_browse = QtWidgets.QPushButton(parent=self.psx_tab)
        self.psx_input_browse.setObjectName("psx_input_browse")
        self.psx_input_row.addWidget(self.psx_input_browse)

        self.psx_vertical_layout.addLayout(self.psx_input_row)

        # Output row
        self.psx_output_row = QtWidgets.QHBoxLayout()
        self.psx_output_row.setObjectName("psx_output_row")

        ## Output path label
        self.psx_output_label = QtWidgets.QLabel(parent=self.psx_tab)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Policy.Preferred)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.psx_output_label.sizePolicy().hasHeightForWidth())
        self.psx_output_label.setSizePolicy(size_policy)
        self.psx_output_label.setMinimumSize(QtCore.QSize(90, 0))
        self.psx_output_label.setObjectName("psx_output_label")
        self.psx_output_row.addWidget(self.psx_output_label)

        ## Output path
        self.psx_output_path = QtWidgets.QLineEdit(parent=self.psx_tab)
        self.psx_output_path.setReadOnly(True)
        self.psx_output_path.setObjectName("psx_output_path")
        self.psx_output_row.addWidget(self.psx_output_path)

        ## Output path browse button
        self.psx_output_browse = QtWidgets.QPushButton(parent=self.psx_tab)
        self.psx_output_browse.setObjectName("psx_output_browse")
        self.psx_output_row.addWidget(self.psx_output_browse)

        self.psx_vertical_layout.addLayout(self.psx_output_row)

        # Extract row
        self.psx_extract_row = QtWidgets.QHBoxLayout()
        self.psx_extract_row.setObjectName("psx_extract_row")

        ## Extract button
        self.psx_extract_button = QtWidgets.QPushButton(parent=self.psx_tab)
        self.psx_extract_button.setEnabled(False)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.MinimumExpanding, QtWidgets.QSizePolicy.Policy.Fixed)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.psx_extract_button.sizePolicy().hasHeightForWidth())
        self.psx_extract_button.setSizePolicy(size_policy)
        self.psx_extract_button.setMinimumSize(QtCore.QSize(453, 0))
        self.psx_extract_button.setCheckable(False)
        self.psx_extract_button.setObjectName("psx_extract_button")
        self.psx_extract_row.addWidget(self.psx_extract_button)

        ## Toggle subdirectories checkbox
        self.psx_toggle_sub_dirs = QtWidgets.QCheckBox(parent=self.psx_tab)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Policy.Fixed)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.psx_toggle_sub_dirs.sizePolicy().hasHeightForWidth())
        self.psx_toggle_sub_dirs.setSizePolicy(size_policy)
        self.psx_toggle_sub_dirs.setMinimumSize(QtCore.QSize(135, 0))
        self.psx_toggle_sub_dirs.setToolTipDuration(-1)
        self.psx_toggle_sub_dirs.setObjectName("psx_toggle_sub_dirs")
        self.psx_extract_row.addWidget(self.psx_toggle_sub_dirs)

        self.psx_vertical_layout.addLayout(self.psx_extract_row)

        # File table
        self.psx_file_table = QtWidgets.QTableWidget(parent=self.psx_tab)
        self.psx_file_table.setAlternatingRowColors(True)
        self.psx_file_table.setColumnCount(4)
        self.psx_file_table.setObjectName("psx_file_table")
        self.psx_file_table.setRowCount(0)
        item = QtWidgets.QTableWidgetItem()
        self.psx_file_table.setHorizontalHeaderItem(0, item)
        item = QtWidgets.QTableWidgetItem()
        self.psx_file_table.setHorizontalHeaderItem(1, item)
        item = QtWidgets.QTableWidgetItem()
        self.psx_file_table.setHorizontalHeaderItem(2, item)
        item = QtWidgets.QTableWidgetItem()
        self.psx_file_table.setHorizontalHeaderItem(3, item)
        self.psx_file_table.horizontalHeader().setCascadingSectionResizes(True)
        self.psx_file_table.horizontalHeader().setStretchLastSection(True)
        self.psx_file_table.horizontalHeader().setDefaultSectionSize(146)
        self.psx_vertical_layout.addWidget(self.psx_file_table)

        # Progress bar
        self.psx_progress_bar = QtWidgets.QProgressBar(parent=self.psx_tab)
        self.psx_progress_bar.setProperty("value", 0)
        self.psx_progress_bar.setTextVisible(False)
        self.psx_progress_bar.setObjectName("psx_progress_bar")
        self.psx_vertical_layout.addWidget(self.psx_progress_bar)

        self.psx_tab_grid_layout.addLayout(self.psx_vertical_layout, 0, 0, 1, 1)

        # Pair labels to controls
        self.psx_input_label.setBuddy(self.psx_input_path)
        self.psx_output_label.setBuddy(self.psx_output_path)

        # Tab order
        self.psx_tab.setTabOrder(self.psx_input_path, self.psx_input_browse)
        self.psx_tab.setTabOrder(self.psx_input_browse, self.psx_output_path)
        self.psx_tab.setTabOrder(self.psx_output_path, self.psx_output_browse)
        self.psx_tab.setTabOrder(self.psx_output_browse, self.psx_extract_button)
        self.psx_tab.setTabOrder(self.psx_extract_button, self.psx_file_table)
        self.psx_tab.setTabOrder(self.psx_file_table, self.psx_toggle_sub_dirs)

    def retranslate_ui(self):
        _translate = QtCore.QCoreApplication.translate
        self.psx_input_label.setText(_translate("main_window", "Input Directory"))
        self.psx_input_browse.setText(_translate("main_window", "Browse..."))
        self.psx_output_label.setText(_translate("main_window", "Output Directory"))
        self.psx_output_browse.setText(_translate("main_window", "Browse..."))
        self.psx_extract_button.setText(_translate("main_window", "Extract"))
        self.psx_toggle_sub_dirs.setToolTip(_translate("main_window", "When selected, a subdirectory will be created for each PSX file."))
        self.psx_toggle_sub_dirs.setText(_translate("main_window", "Create Subdirectories"))
        self.psx_file_table.setSortingEnabled(True)
        item = self.psx_file_table.horizontalHeaderItem(0)
        item.setText(_translate("main_window", "File Name"))
        item = self.psx_file_table.horizontalHeaderItem(1)
        item.setText(_translate("main_window", "Number of Textures"))
        item = self.psx_file_table.horizontalHeaderItem(2)
        item.setText(_translate("main_window", "Textures Extracted"))
        item = self.psx_file_table.horizontalHeaderItem(3)
        item.setText(_translate("main_window", "Status"))
