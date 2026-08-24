// DecompileFunctionRange.java -- Ghidra post-script to decompile every function whose
// entry point falls inside an address range.
//
// Useful when a subsystem occupies a contiguous span of a stripped binary and the
// function starts are not known in advance -- decompile the whole span and read it,
// rather than discovering entry points one xref at a time.
//
// Usage:
//   -postScript DecompileFunctionRange.java <output.c> <startAddr> <endAddr>
//
// Addresses may be specified as hexadecimal with or without a 0x prefix.
//
// @category Analysis

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.decompiler.DecompiledFunction;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;

import java.io.FileWriter;
import java.io.PrintWriter;

public class DecompileFunctionRange extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            printerr("Usage: DecompileFunctionRange.java <output.c> <startAddr> <endAddr>");
            return;
        }

        Address start = parse(args[1]);
        Address end = parse(args[2]);

        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);

        int decompiled = 0;
        try (PrintWriter out = new PrintWriter(new FileWriter(args[0]))) {
            out.println("// Ghidra decompilation of every function entry in ["
                    + start + ", " + end + "]");
            out.println("// Program: " + currentProgram.getName());
            out.println();

            FunctionIterator functions = currentProgram.getFunctionManager()
                    .getFunctions(start, true);
            while (functions.hasNext() && !monitor.isCancelled()) {
                Function function = functions.next();
                Address entry = function.getEntryPoint();
                if (entry.compareTo(end) > 0) {
                    break;
                }

                out.println("// ========================================");
                out.println("// Function: " + function.getName() + " @ " + entry);
                out.println("// Size: " + function.getBody().getNumAddresses() + " bytes");
                out.println("// ========================================");
                out.println();

                DecompileResults results = decompiler.decompileFunction(function, 60, monitor);
                DecompiledFunction decompiledFunction =
                        results == null ? null : results.getDecompiledFunction();
                if (decompiledFunction == null) {
                    out.println("// DECOMPILATION FAILED"
                            + (results == null ? "" : ": " + results.getErrorMessage()));
                } else {
                    out.println(decompiledFunction.getC());
                    decompiled++;
                }
                out.println();
            }
        } finally {
            decompiler.dispose();
        }

        println("Decompiled " + decompiled + " functions to " + args[0]);
    }

    private Address parse(String text) {
        String value = text.startsWith("0x") || text.startsWith("0X") ? text.substring(2) : text;
        return currentProgram.getAddressFactory().getDefaultAddressSpace()
                .getAddress(Long.parseLong(value, 16));
    }
}
