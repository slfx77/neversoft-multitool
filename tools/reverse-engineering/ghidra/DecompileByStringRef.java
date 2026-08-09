// DecompileByStringRef.java -- Find functions referencing a string and decompile them.
//
// Usage:
//   -postScript DecompileByStringRef.java <output.c> <string1> [string2 ...]
//
// @category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.StringDataInstance;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.ReferenceManager;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class DecompileByStringRef extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("Usage: DecompileByStringRef.java <output.c> <string1> [string2 ...]");
            return;
        }

        String outputPath = args[0];
        if (outputPath.startsWith("'") && outputPath.endsWith("'")) {
            outputPath = outputPath.substring(1, outputPath.length() - 1);
        }

        Set<Address> functionEntries = new LinkedHashSet<>();
        Listing listing = currentProgram.getListing();
        ReferenceManager refMgr = currentProgram.getReferenceManager();
        FunctionManager fnMgr = currentProgram.getFunctionManager();

        try (PrintWriter out = new PrintWriter(new FileWriter(outputPath))) {
            out.println("// Decompilation of functions referencing target strings");
            out.println("// Program: " + currentProgram.getName());
            out.println();

            for (int i = 1; i < args.length; i++) {
                String needle = args[i];
                if (needle.startsWith("'") && needle.endsWith("'")) {
                    needle = needle.substring(1, needle.length() - 1);
                }
                out.println("// === Searching for string: \"" + needle + "\" ===");
                println("Searching for string: \"" + needle + "\"");

                int hits = 0;
                int totalCallers = 0;

                // Walk all defined data; check those that are strings
                DataIterator dataIt = listing.getDefinedData(true);
                while (dataIt.hasNext() && !monitor.isCancelled()) {
                    Data data = dataIt.next();
                    if (!data.hasStringValue()) continue;
                    Object val = data.getValue();
                    if (val == null) continue;
                    String s = val.toString();
                    if (!s.contains(needle)) continue;
                    hits++;
                    Address strAddr = data.getAddress();
                    out.printf("//   String at %s: \"%s\"%n", strAddr, s);
                    println(String.format("  String at %s: \"%s\"", strAddr, s));

                    // Find xrefs to this string
                    ReferenceIterator refs = refMgr.getReferencesTo(strAddr);
                    while (refs.hasNext()) {
                        Reference ref = refs.next();
                        Address fromAddr = ref.getFromAddress();
                        Function caller = fnMgr.getFunctionContaining(fromAddr);
                        if (caller == null) continue;
                        if (functionEntries.add(caller.getEntryPoint())) {
                            totalCallers++;
                            out.printf("//     Caller: %s @ %s%n", caller.getName(), caller.getEntryPoint());
                        }
                    }
                }
                out.printf("//   Found %d strings, %d unique callers%n", hits, totalCallers);
                out.println();
            }

            // Decompile all unique callers
            DecompInterface decompiler = new DecompInterface();
            decompiler.openProgram(currentProgram);
            decompiler.setSimplificationStyle("decompile");

            int decompiled = 0;
            for (Address entry : functionEntries) {
                Function fn = fnMgr.getFunctionAt(entry);
                if (fn == null) continue;
                out.println("// ========================================");
                out.printf("// %s @ %s (size: %d bytes)%n", fn.getName(), entry, fn.getBody().getNumAddresses());
                out.println("// ========================================");
                out.println();

                DecompileResults result = decompiler.decompileFunction(fn, 60, monitor);
                if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                    out.println(result.getDecompiledFunction().getC());
                    decompiled++;
                } else {
                    out.println("// Decompilation FAILED: " + result.getErrorMessage());
                }
                out.println();
                if (monitor.isCancelled()) break;
            }

            out.printf("// Done: %d functions decompiled%n", decompiled);
            println(String.format("Done: %d functions decompiled to %s", decompiled, outputPath));
        }
    }
}
