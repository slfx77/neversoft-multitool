from PyQt6 import QtCore, QtWidgets
from psx import PsxUi
from rle import RleUi


class MainWindowUi(object):
    def setup_ui(self, main_window):
        main_window.setObjectName("main_window")
        main_window.resize(640, 480)
        self.main_layout = QtWidgets.QWidget(main_window)
        self.main_layout.setObjectName("main_layout")
        self.grid_layout = QtWidgets.QGridLayout(self.main_layout)
        self.grid_layout.setObjectName("grid_layout")
        self.tabs = QtWidgets.QTabWidget(self.main_layout)
        self.tabs.setAutoFillBackground(False)
        self.tabs.setObjectName("tabs")
       
        # PSX Panel Code
        self.psx_ui = PsxUi()
        self.psx_tab = self.psx_ui.psx_tab
        self.psx_ui.retranslate_ui()
        self.tabs.addTab(self.psx_tab, "")
        
        # RLE Panel Code
        self.rle_ui = RleUi()
        self.rle_tab = self.rle_ui.rle_tab
        self.rle_ui.retranslate_ui()
        self.tabs.addTab(self.rle_tab, "")

        self.grid_layout.addWidget(self.tabs, 0, 0, 1, 1)
        main_window.setCentralWidget(self.main_layout)
        self.status_bar = QtWidgets.QStatusBar(main_window)
        self.status_bar.setObjectName("status_bar")
        main_window.setStatusBar(self.status_bar)
        self.retranslate_ui(main_window)
        self.tabs.setCurrentIndex(0)
        self.psx_tab.setTabOrder(self.tabs, self.psx_ui.psx_input_path)
        self.psx_ui.psx_output_browse.clicked.connect(main_window.psx_output_browse_clicked)  # type: ignore
        self.psx_ui.psx_extract_button.clicked.connect(main_window.psx_extract_clicked)  # type: ignore
        self.psx_ui.psx_input_browse.clicked.connect(main_window.psx_input_browse_clicked)  # type: ignore
        self.psx_ui.psx_toggle_sub_dirs.stateChanged["int"].connect(main_window.psx_create_sub_dirs_clicked)  # type: ignore
        self.rle_ui.rle_input_browse.clicked.connect(main_window.rle_input_browse_clicked)  # type: ignore
        self.rle_ui.rle_output_browse.clicked.connect(main_window.rle_output_browse_clicked)  # type: ignore
        self.rle_ui.rle_convert_button.clicked.connect(main_window.rle_convert_clicked)  # type: ignore
        self.tabs.currentChanged["int"].connect(main_window.tab_changed)  # type: ignore
        QtCore.QMetaObject.connectSlotsByName(main_window)

    def retranslate_ui(self, main_window):
        _translate = QtCore.QCoreApplication.translate
        main_window.setWindowTitle(_translate("main_window", "Neversoft Image Tool v1.0"))
        self.tabs.setTabText(self.tabs.indexOf(self.psx_tab), _translate("main_window", "PSX"))
        self.tabs.setTabText(self.tabs.indexOf(self.rle_tab), _translate("main_window", "RLE / BMR"))
