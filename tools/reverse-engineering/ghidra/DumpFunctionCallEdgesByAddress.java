// DumpFunctionCallEdgesByAddress.java -- dump incoming/outgoing function calls for one or more function addresses.
//
// Usage:
//   -postScript DumpFunctionCallEdgesByAddress.java <output.txt> <addr1> [addr2 ...]
//
// Address arguments may be specified as hexadecimal with or without a 0x prefix.
//
// @category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Set;

public class DumpFunctionCallEdgesByAddress extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("Usage: DumpFunctionCallEdgesByAddress.java <output.txt> <addr1> [addr2 ...]");
            return;
        }

        String outputPath = args[0];
        if (outputPath.startsWith("'") && outputPath.endsWith("'")) {
            outputPath = outputPath.substring(1, outputPath.length() - 1);
        }

        FunctionManager functionManager = currentProgram.getFunctionManager();

        try (PrintWriter out = new PrintWriter(new FileWriter(outputPath))) {
            out.println("// Function call edges by target address");
            out.println("// Program: " + currentProgram.getName());
            out.println();

            for (int i = 1; i < args.length; i++) {
                String text = args[i].trim();
                if (text.startsWith("0x") || text.startsWith("0X")) {
                    text = text.substring(2);
                }

                long addrValue;
                try {
                    addrValue = Long.parseUnsignedLong(text, 16);
                } catch (NumberFormatException ex) {
                    out.println("// ========================================");
                    out.println("// INVALID ADDRESS ARGUMENT: " + args[i]);
                    out.println("// ========================================");
                    out.println();
                    continue;
                }

                Address address = toAddr(addrValue);
                Function function = functionManager.getFunctionAt(address);
                if (function == null) {
                    function = functionManager.getFunctionContaining(address);
                }

                out.println("// ========================================");
                out.println("// Requested address: 0x" + Long.toHexString(addrValue));
                if (function == null) {
                    out.println("// NO FUNCTION FOUND");
                    out.println("// ========================================");
                    out.println();
                    continue;
                }

                out.println("// Function: " + function.getName() + " @ " + function.getEntryPoint());
                out.println("// ========================================");

                dumpFunctions(out, "Incoming", function.getCallingFunctions(monitor));
                dumpFunctions(out, "Outgoing", function.getCalledFunctions(monitor));
                out.println();
            }
        }

        println("DumpFunctionCallEdgesByAddress: output=" + outputPath);
    }

    private void dumpFunctions(PrintWriter out, String label, Set<Function> functions) {
        List<Function> list = new ArrayList<>(functions);
        Collections.sort(list, Comparator.comparing(Function::getEntryPoint));
        out.println(label + " (" + list.size() + "):");
        for (Function function : list) {
            out.println("  " + function.getName() + " @ " + function.getEntryPoint());
        }
    }
}
