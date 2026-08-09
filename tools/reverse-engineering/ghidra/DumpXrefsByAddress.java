// DumpXrefsByAddress.java -- Ghidra post-script to dump direct xrefs to one or more addresses.
//
// Usage:
//   -postScript DumpXrefsByAddress.java <output.txt> <addr1> [addr2 ...]
//
// @category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.Iterator;

public class DumpXrefsByAddress extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("Usage: DumpXrefsByAddress.java <output.txt> <addr1> [addr2 ...]");
            return;
        }

        String outputPath = args[0];
        if (outputPath.startsWith("'") && outputPath.endsWith("'")) {
            outputPath = outputPath.substring(1, outputPath.length() - 1);
        }

        ReferenceManager referenceManager = currentProgram.getReferenceManager();
        FunctionManager functionManager = currentProgram.getFunctionManager();

        try (PrintWriter out = new PrintWriter(new FileWriter(outputPath))) {
            out.println("// Xrefs to target addresses");
            out.println("// Program: " + currentProgram.getName());
            out.println();

            for (int i = 1; i < args.length; i++) {
                String text = args[i].trim();
                if (text.startsWith("0x") || text.startsWith("0X")) {
                    text = text.substring(2);
                }

                long addrValue = Long.parseUnsignedLong(text, 16);
                Address target = toAddr(addrValue);

                out.println("// ========================================");
                out.printf("// Target: %08x%n", addrValue);

                Function targetFunction = functionManager.getFunctionAt(target);
                if (targetFunction == null) {
                    targetFunction = functionManager.getFunctionContaining(target);
                }
                if (targetFunction != null) {
                    out.println("// Function at target: " + targetFunction.getName() + " @ " +
                            targetFunction.getEntryPoint());
                }
                out.println("// ========================================");

                Iterator<Reference> refs = referenceManager.getReferencesTo(target);
                int count = 0;
                while (refs.hasNext()) {
                    Reference ref = refs.next();
                    Address from = ref.getFromAddress();
                    Function fromFunction = functionManager.getFunctionContaining(from);
                    String fnInfo = fromFunction == null
                            ? "<no containing function>"
                            : fromFunction.getName() + " @ " + fromFunction.getEntryPoint();
                    out.println(from + " -> " + target + "  " + ref.getReferenceType() + "  in " + fnInfo);
                    count++;
                }

                out.println("// Ref count: " + count);
                out.println();
                println("DumpXrefsByAddress: " + target + " refs=" + count);
            }
        }

        println("DumpXrefsByAddress: output=" + outputPath);
    }
}
