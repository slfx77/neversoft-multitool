// DumpFunctionPointerTable.java -- Ghidra post-script to dump a pointer table
// and resolve entries to functions when possible.
//
// Usage:
//   -postScript DumpFunctionPointerTable.java <output.txt> <table_addr> <count>
//
// Address arguments may be specified as hexadecimal with or without a 0x prefix.
//
// @category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolTable;

import java.io.FileWriter;
import java.io.PrintWriter;

public class DumpFunctionPointerTable extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            printerr("Usage: DumpFunctionPointerTable.java <output.txt> <table_addr> <count>");
            return;
        }

        String outputPath = args[0];
        if (outputPath.startsWith("'") && outputPath.endsWith("'")) {
            outputPath = outputPath.substring(1, outputPath.length() - 1);
        }

        long tableValue = parseHexAddress(args[1]);
        int count;
        try {
            count = Integer.parseInt(args[2]);
        } catch (NumberFormatException ex) {
            printerr("Invalid count: " + args[2]);
            return;
        }

        Address tableAddress = toAddr(tableValue);
        Memory memory = currentProgram.getMemory();
        FunctionManager functionManager = currentProgram.getFunctionManager();
        SymbolTable symbolTable = currentProgram.getSymbolTable();

        PrintWriter out = new PrintWriter(new FileWriter(outputPath));
        out.println("// Pointer table dump");
        out.println("// Program: " + currentProgram.getName());
        out.println("// Table address: " + tableAddress);
        out.println("// Entry count: " + count);
        out.println();

        for (int i = 0; i < count; i++) {
            Address entryAddress = tableAddress.add((long)i * 4L);
            int rawValue;
            try {
                rawValue = memory.getInt(entryAddress);
            } catch (Exception ex) {
                out.printf("[%02d] +0x%03X @ %s -> <read error: %s>%n",
                    i, i * 4, entryAddress, ex.getMessage());
                continue;
            }

            long pointerValue = rawValue & 0xffffffffL;
            String detail;
            if (pointerValue == 0) {
                detail = "NULL";
            } else {
                Address pointerAddress = toAddr(pointerValue);
                if (!memory.contains(pointerAddress)) {
                    detail = "outside memory";
                } else {
                    Function function = functionManager.getFunctionAt(pointerAddress);
                    if (function == null) {
                        function = functionManager.getFunctionContaining(pointerAddress);
                    }

                    if (function != null) {
                        detail = "function " + function.getName() + " @ " + function.getEntryPoint();
                    } else {
                        Symbol symbol = symbolTable.getPrimarySymbol(pointerAddress);
                        if (symbol != null) {
                            detail = "symbol " + symbol.getName() + " @ " + pointerAddress;
                        } else {
                            detail = "data/code @ " + pointerAddress;
                        }
                    }
                }
            }

            out.printf("[%02d] +0x%03X @ %s -> 0x%08X  %s%n",
                i, i * 4, entryAddress, pointerValue, detail);
        }

        out.close();
        println("DumpFunctionPointerTable: wrote " + outputPath);
    }

    private long parseHexAddress(String text) {
        String trimmed = text.trim();
        if (trimmed.startsWith("0x") || trimmed.startsWith("0X")) {
            trimmed = trimmed.substring(2);
        }

        return Long.parseUnsignedLong(trimmed, 16);
    }
}
