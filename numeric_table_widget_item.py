from PyQt6.QtWidgets import QTableWidgetItem


class NumericTableWidgetItem(QTableWidgetItem):
    def __lt__(self, other):
        self_value = int(self.text()) if self.text() else 0
        other_value = int(other.text()) if other.text() else 0
        return self_value < other_value
