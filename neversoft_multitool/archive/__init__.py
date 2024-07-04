from PyQt6 import QtCore, QtWidgets


class ArchiveUi(object):
    def __init__(self):
        self.setup_ui()

    def setup_ui(self):
        self.archive_tab = QtWidgets.QWidget()
        self.archive_tab.setObjectName("archive_tab")

        # Layout
        self.archive_tab_grid_layout = QtWidgets.QGridLayout(self.archive_tab)
        self.archive_tab_grid_layout.setContentsMargins(9, 9, 9, 9)
        self.archive_tab_grid_layout.setObjectName("archive_tab_grid_layout")
        self.archive_vertical_layout = QtWidgets.QVBoxLayout()
        self.archive_vertical_layout.setObjectName("archive_vertical_layout")

        # Input row
        self.archive_input_row = QtWidgets.QHBoxLayout()
        self.archive_input_row.setObjectName("archive_input_row")

        ## Input path label
        self.archive_input_label = QtWidgets.QLabel(parent=self.archive_tab)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Policy.Preferred)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.archive_input_label.sizePolicy().hasHeightForWidth())
        self.archive_input_label.setSizePolicy(size_policy)
        self.archive_input_label.setMinimumSize(QtCore.QSize(90, 0))
        self.archive_input_label.setObjectName("archive_input_label")
        self.archive_input_row.addWidget(self.archive_input_label)

        ## Input archive
        self.archive_input_path = QtWidgets.QLineEdit(parent=self.archive_tab)
        self.archive_input_path.setEnabled(True)
        self.archive_input_path.setReadOnly(True)
        self.archive_input_path.setObjectName("archive_input_path")
        self.archive_input_row.addWidget(self.archive_input_path)

        ## Input archive browse button
        self.archive_input_browse = QtWidgets.QPushButton(parent=self.archive_tab)
        self.archive_input_browse.setObjectName("archive_input_browse")
        self.archive_input_row.addWidget(self.archive_input_browse)

        self.archive_vertical_layout.addLayout(self.archive_input_row)

        # Output row
        self.archive_output_row = QtWidgets.QHBoxLayout()
        self.archive_output_row.setObjectName("archive_output_row")

        ## Output path label
        self.archive_output_label = QtWidgets.QLabel(parent=self.archive_tab)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Policy.Preferred)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.archive_output_label.sizePolicy().hasHeightForWidth())
        self.archive_output_label.setSizePolicy(size_policy)
        self.archive_output_label.setMinimumSize(QtCore.QSize(90, 0))
        self.archive_output_label.setObjectName("archive_output_label")
        self.archive_output_row.addWidget(self.archive_output_label)

        ## Output path
        self.archive_output_path = QtWidgets.QLineEdit(parent=self.archive_tab)
        self.archive_output_path.setReadOnly(True)
        self.archive_output_path.setObjectName("archive_output_path")
        self.archive_output_row.addWidget(self.archive_output_path)

        ## Output path browse button
        self.archive_output_browse = QtWidgets.QPushButton(parent=self.archive_tab)
        self.archive_output_browse.setObjectName("archive_output_browse")
        self.archive_output_row.addWidget(self.archive_output_browse)

        self.archive_vertical_layout.addLayout(self.archive_output_row)

        # Extract row
        self.archive_extract_row = QtWidgets.QHBoxLayout()
        self.archive_extract_row.setObjectName("archive_extract_row")

        ## Extract button
        self.archive_extract_button = QtWidgets.QPushButton(parent=self.archive_tab)
        self.archive_extract_button.setEnabled(False)
        size_policy = QtWidgets.QSizePolicy(QtWidgets.QSizePolicy.Policy.MinimumExpanding, QtWidgets.QSizePolicy.Policy.Fixed)
        size_policy.setHorizontalStretch(0)
        size_policy.setVerticalStretch(0)
        size_policy.setHeightForWidth(self.archive_extract_button.sizePolicy().hasHeightForWidth())
        self.archive_extract_button.setSizePolicy(size_policy)
        self.archive_extract_button.setMinimumSize(QtCore.QSize(453, 0))
        self.archive_extract_button.setCheckable(False)
        self.archive_extract_button.setObjectName("archive_extract_button")
        self.archive_extract_row.addWidget(self.archive_extract_button)

        self.archive_vertical_layout.addLayout(self.archive_extract_row)

        # File table
        self.archive_file_table = QtWidgets.QTableWidget(parent=self.archive_tab)
        self.archive_file_table.setAutoScroll(True)
        self.archive_file_table.setTabKeyNavigation(True)
        self.archive_file_table.setProperty("showDropIndicator", True)
        self.archive_file_table.setAlternatingRowColors(True)
        self.archive_file_table.setShowGrid(True)
        self.archive_file_table.setColumnCount(3)
        self.archive_file_table.setObjectName("archive_file_table")
        self.archive_file_table.setRowCount(0)
        item = QtWidgets.QTableWidgetItem()
        self.archive_file_table.setHorizontalHeaderItem(0, item)
        item = QtWidgets.QTableWidgetItem()
        self.archive_file_table.setHorizontalHeaderItem(1, item)
        item = QtWidgets.QTableWidgetItem()
        self.archive_file_table.setHorizontalHeaderItem(2, item)
        self.archive_file_table.horizontalHeader().setCascadingSectionResizes(True)
        self.archive_file_table.horizontalHeader().setStretchLastSection(True)
        self.archive_file_table.horizontalHeader().setDefaultSectionSize(146)
        self.archive_vertical_layout.addWidget(self.archive_file_table)

        # Progress bar
        self.archive_progress_bar = QtWidgets.QProgressBar(parent=self.archive_tab)
        self.archive_progress_bar.setProperty("value", 0)
        self.archive_progress_bar.setTextVisible(False)
        self.archive_progress_bar.setObjectName("archive_progress_bar")
        self.archive_vertical_layout.addWidget(self.archive_progress_bar)

        self.archive_tab_grid_layout.addLayout(self.archive_vertical_layout, 0, 0, 1, 1)

        # Pair labels to controls
        self.archive_input_label.setBuddy(self.archive_input_path)
        self.archive_output_label.setBuddy(self.archive_output_path)

        # Tab order
        self.archive_tab.setTabOrder(self.archive_input_path, self.archive_input_browse)
        self.archive_tab.setTabOrder(self.archive_input_browse, self.archive_output_path)
        self.archive_tab.setTabOrder(self.archive_output_path, self.archive_output_browse)
        self.archive_tab.setTabOrder(self.archive_output_browse, self.archive_extract_button)
        self.archive_tab.setTabOrder(self.archive_extract_button, self.archive_file_table)

    def retranslate_ui(self):
        _translate = QtCore.QCoreApplication.translate
        self.archive_input_label.setText(_translate("main_window", "Input Archive"))
        self.archive_input_browse.setText(_translate("main_window", "Browse..."))
        self.archive_output_label.setText(_translate("main_window", "Output Directory"))
        self.archive_output_browse.setText(_translate("main_window", "Browse..."))
        self.archive_extract_button.setText(_translate("main_window", "Extract"))
        self.archive_file_table.setSortingEnabled(True)
        item = self.archive_file_table.horizontalHeaderItem(0)
        item.setText(_translate("main_window", "File Name"))
        item = self.archive_file_table.horizontalHeaderItem(1)
        item.setText(_translate("main_window", "Size (bytes)"))
        item = self.archive_file_table.horizontalHeaderItem(2)
        item.setText(_translate("main_window", "Status"))
