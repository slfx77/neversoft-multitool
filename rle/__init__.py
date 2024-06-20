from PyQt6 import QtCore, QtWidgets


class RleUi:
    def __init__(self):
        self.setup_ui()

    def setup_ui(self):
        self.rle_tab = QtWidgets.QWidget()
        self.rle_tab.setObjectName("rle_tab")

        # Layout
        self.rle_tab_grid_layout = QtWidgets.QGridLayout(self.rle_tab)
        self.rle_tab_grid_layout.setContentsMargins(9, 9, 9, 9)
        self.rle_tab_grid_layout.setObjectName("rle_tab_grid_layout")
        self.rle_vertical_layout = QtWidgets.QVBoxLayout()
        self.rle_vertical_layout.setObjectName("rle_vertical_layout")

        # Input row
        self.rle_input_row = QtWidgets.QHBoxLayout()
        self.rle_input_row.setObjectName("rle_input_row")

        ## Input path label
        self.rle_input_label = QtWidgets.QLabel(parent=self.rle_tab)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Policy.Preferred)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.rle_input_label.sizePolicy().hasHeightForWidth())
        self.rle_input_label.setSizePolicy(size_policy)
        self.rle_input_label.setMinimumSize(QtCore.QSize(90, 0))
        self.rle_input_label.setObjectName("rle_input_label")
        self.rle_input_row.addWidget(self.rle_input_label)

        ## Input path
        self.rle_input_path = QtWidgets.QLineEdit(parent=self.rle_tab)
        self.rle_input_path.setEnabled(True)
        self.rle_input_path.setReadOnly(True)
        self.rle_input_path.setObjectName("rle_input_path")
        self.rle_input_row.addWidget(self.rle_input_path)

        ## Input path browse button
        self.rle_input_browse = QtWidgets.QPushButton(parent=self.rle_tab)
        self.rle_input_browse.setObjectName("rle_input_browse")
        self.rle_input_row.addWidget(self.rle_input_browse)

        self.rle_vertical_layout.addLayout(self.rle_input_row)

        # Output row
        self.rle_output_row = QtWidgets.QHBoxLayout()
        self.rle_output_row.setObjectName("rle_output_row")

        ## Output path label
        self.rle_output_label = QtWidgets.QLabel(parent=self.rle_tab)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Policy.Preferred)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.rle_output_label.sizePolicy().hasHeightForWidth())
        self.rle_output_label.setSizePolicy(size_policy)
        self.rle_output_label.setMinimumSize(QtCore.QSize(90, 0))
        self.rle_output_label.setObjectName("rle_output_label")
        self.rle_output_row.addWidget(self.rle_output_label)

        ## Output path
        self.rle_output_path = QtWidgets.QLineEdit(parent=self.rle_tab)
        self.rle_output_path.setReadOnly(True)
        self.rle_output_path.setObjectName("rle_output_path")
        self.rle_output_row.addWidget(self.rle_output_path)

        ## Output path browse button
        self.rle_output_browse = QtWidgets.QPushButton(parent=self.rle_tab)
        self.rle_output_browse.setObjectName("rle_output_browse")
        self.rle_output_row.addWidget(self.rle_output_browse)

        self.rle_vertical_layout.addLayout(self.rle_output_row)

        # Convert row
        self.rle_convert_row = QtWidgets.QHBoxLayout()
        self.rle_convert_row.setObjectName("rle_convert_row")

        ## Convert button
        self.rle_convert_button = QtWidgets.QPushButton(parent=self.rle_tab)
        self.rle_convert_button.setEnabled(False)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.MinimumExpanding, QtWidgets.QSizePolicy.Policy.Fixed)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.rle_convert_button.sizePolicy().hasHeightForWidth())
        self.rle_convert_button.setSizePolicy(size_policy)
        self.rle_convert_button.setMinimumSize(QtCore.QSize(453, 0))
        self.rle_convert_button.setCheckable(False)
        self.rle_convert_button.setObjectName("rle_convert_button")
        self.rle_convert_row.addWidget(self.rle_convert_button)

        ## RLE width label
        self.rle_width_label = QtWidgets.QLabel(parent=self.rle_tab)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Policy.Minimum)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.rle_width_label.sizePolicy().hasHeightForWidth())
        self.rle_width_label.setSizePolicy(size_policy)
        self.rle_width_label.setMinimumSize(QtCore.QSize(40, 0))
        self.rle_width_label.setMaximumSize(QtCore.QSize(40, 16777215))
        self.rle_width_label.setAlignment(QtCore.Qt.AlignmentFlag.AlignRight | QtCore.Qt.AlignmentFlag.AlignTrailing | QtCore.Qt.AlignmentFlag.AlignVCenter)
        self.rle_width_label.setObjectName("rle_width_label")
        self.rle_convert_row.addWidget(self.rle_width_label)

        ## RLE width selector
        self.rle_width_selector = QtWidgets.QSpinBox(parent=self.rle_tab)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Policy.Fixed)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.rle_width_selector.sizePolicy().hasHeightForWidth())
        self.rle_width_selector.setSizePolicy(size_policy)
        self.rle_width_selector.setMinimumSize(QtCore.QSize(92, 0))
        self.rle_width_selector.setMaximum(1024)
        self.rle_width_selector.setProperty("value", 512)
        self.rle_width_selector.setObjectName("rle_width_selector")
        self.rle_convert_row.addWidget(self.rle_width_selector)

        self.rle_vertical_layout.addLayout(self.rle_convert_row)

        # File table
        self.rle_file_table = QtWidgets.QTableWidget(parent=self.rle_tab)
        self.rle_file_table.setAutoScroll(True)
        self.rle_file_table.setTabKeyNavigation(True)
        self.rle_file_table.setProperty("showDropIndicator", True)
        self.rle_file_table.setAlternatingRowColors(True)
        self.rle_file_table.setShowGrid(True)
        self.rle_file_table.setColumnCount(2)
        self.rle_file_table.setObjectName("rle_file_table")
        self.rle_file_table.setRowCount(0)
        item = QtWidgets.QTableWidgetItem()
        self.rle_file_table.setHorizontalHeaderItem(0, item)
        item = QtWidgets.QTableWidgetItem()
        self.rle_file_table.setHorizontalHeaderItem(1, item)
        self.rle_file_table.horizontalHeader().setCascadingSectionResizes(True)
        self.rle_file_table.horizontalHeader().setStretchLastSection(True)
        self.rle_file_table.horizontalHeader().setDefaultSectionSize(146)
        self.rle_vertical_layout.addWidget(self.rle_file_table)

        # Progress bar
        self.rle_progress_bar = QtWidgets.QProgressBar(parent=self.rle_tab)
        self.rle_progress_bar.setProperty("value", 0)
        self.rle_progress_bar.setTextVisible(False)
        self.rle_progress_bar.setObjectName("rle_progress_bar")
        self.rle_vertical_layout.addWidget(self.rle_progress_bar)

        self.rle_tab_grid_layout.addLayout(self.rle_vertical_layout, 0, 0, 1, 1)

        # Pair labels to controls
        self.rle_input_label.setBuddy(self.rle_input_path)
        self.rle_output_label.setBuddy(self.rle_output_path)

        # Tab order
        self.rle_tab.setTabOrder(self.rle_input_path, self.rle_input_browse)
        self.rle_tab.setTabOrder(self.rle_input_browse, self.rle_output_path)
        self.rle_tab.setTabOrder(self.rle_output_path, self.rle_output_browse)
        self.rle_tab.setTabOrder(self.rle_output_browse, self.rle_convert_button)
        self.rle_tab.setTabOrder(self.rle_convert_button, self.rle_width_selector)
        self.rle_tab.setTabOrder(self.rle_width_selector, self.rle_file_table)

    def retranslate_ui(self):
        _translate = QtCore.QCoreApplication.translate
        self.rle_input_label.setText(_translate("main_window", "Input Directory"))
        self.rle_input_browse.setText(_translate("main_window", "Browse..."))
        self.rle_output_label.setText(_translate("main_window", "Output Directory"))
        self.rle_output_browse.setText(_translate("main_window", "Browse..."))
        self.rle_convert_button.setText(_translate("main_window", "Convert"))
        self.rle_width_label.setToolTip(_translate("main_window", "The width of the images being converted."))
        self.rle_width_label.setText(_translate("main_window", "Width"))
        self.rle_file_table.setSortingEnabled(True)
        item = self.rle_file_table.horizontalHeaderItem(0)
        item.setText(_translate("main_window", "File Name"))
        item = self.rle_file_table.horizontalHeaderItem(1)
        item.setText(_translate("main_window", "Status"))
