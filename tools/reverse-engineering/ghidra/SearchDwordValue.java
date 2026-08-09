// SearchDwordValue.java -- Ghidra post-script to search mapped memory for
// one or more 32-bit little-endian values.
//
// Usage:
//   -postScript SearchDwordValue.java <output.txt> <value1> [value2 ...]
//
// Values may be specified with or without a 0x prefix.
//
// @category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolTable;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

public class SearchDwordValue extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("Usage: SearchDwordValue.java <output.txt> <value1> [value2 ...]");
            return;
        }

        String outputPath = stripQuotes(args[0]);
        List<Long> values = new ArrayList<>();
        for (int i = 1; i < args.length; i++) {
            values.add(parseHexAddress(args[i]));
        }

        Memory memory = currentProgram.getMemory();
        FunctionManager functionManager = currentProgram.getFunctionManager();
        SymbolTable symbolTable = currentProgram.getSymbolTable();

        try (PrintWriter out = new PrintWriter(new FileWriter(outputPath))) {
            out.println("// SearchDwordValue results");
            out.println("// Program: " + currentProgram.getName());
            out.println();

            for (long value : values) {
                byte[] needle = new byte[] {
                    (byte)(value & 0xff),
                    (byte)((value >> 8) & 0xff),
                    (byte)((value >> 16) & 0xff),
                    (byte)((value >> 24) & 0xff)
                };

                out.printf("// Value: 0x%08X%n", value);
                int hitCount = 0;

                for (MemoryBlock block : memory.getBlocks()) {
                    if (!block.isInitialized() || !block.isMapped()) {
                        continue;
                    }

                    long blockLength = block.getSize();
                    if (blockLength < 4) {
                        continue;
                    }

                    Address start = block.getStart();
                    int maxLen = (int)Math.min(blockLength, Integer.MAX_VALUE);
                    byte[] data = new byte[maxLen];
                    int bytesRead = memory.getBytes(start, data);
                    if (bytesRead < 4) {
                        continue;
                    }

                    for (int off = 0; off <= bytesRead - 4; off++) {
                        if (data[off] != needle[0] || data[off + 1] != needle[1] ||
                            data[off + 2] != needle[2] || data[off + 3] != needle[3]) {
                            continue;
                        }

                        Address hit = start.add(off);
                        String detail = describeHit(hit, functionManager, symbolTable);
                        out.printf("%s  %s%n", hit, detail);
                        hitCount++;
                    }
                }

                out.printf("// Hit count: %d%n%n", hitCount);
            }
        }

        println("SearchDwordValue: output=" + outputPath);
    }

    private String describeHit(Address hit, FunctionManager functionManager, SymbolTable symbolTable) {
        Function fn = functionManager.getFunctionContaining(hit);
        if (fn != null) {
            return "in function " + fn.getName() + " @ " + fn.getEntryPoint();
        }

        Symbol sym = symbolTable.getPrimarySymbol(hit);
        if (sym != null) {
            return "symbol " + sym.getName();
        }

        return "data";
    }

    private static String stripQuotes(String text) {
        if (text.startsWith("'") && text.endsWith("'") && text.length() >= 2) {
            return text.substring(1, text.length() - 1);
        }
        return text;
    }

    private long parseHexAddress(String text) {
        String trimmed = text.trim();
        if (trimmed.startsWith("0x") || trimmed.startsWith("0X")) {
            trimmed = trimmed.substring(2);
        }
        return Long.parseUnsignedLong(trimmed, 16);
    }
}
