// DumpSkaCallGraph.java -- Focused call graph/disassembly for THPS3 SKA runtime functions.
// @category Analysis
// @runtime Java

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

import java.io.File;
import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class DumpSkaCallGraph extends GhidraScript {
    private static final long[] TARGETS = {
        0x0022ff38L,
        0x00230f48L,
        0x00230f68L,
        0x00231048L,
        0x00285518L,
        0x00285620L,
        0x002864f0L,
        0x00286730L
    };

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        File outFile = args.length > 0 ? new File(args[0]) : new File("thps3_ska_callgraph.txt");
        if (outFile.getParentFile() != null) {
            outFile.getParentFile().mkdirs();
        }

        Set<Function> functions = new LinkedHashSet<>();
        for (long target : TARGETS) {
            Function function = getFunctionAt(addressOf(target));
            if (function != null) {
                functions.add(function);
                collectCallers(function, functions);
            }
        }

        try (PrintWriter out = new PrintWriter(new FileWriter(outFile))) {
            DecompInterface decompiler = new DecompInterface();
            decompiler.setOptions(new DecompileOptions());
            decompiler.openProgram(currentProgram);

            out.println("# THPS3 SKA focused call graph");
            out.println();
            for (long target : TARGETS) {
                Function function = getFunctionAt(addressOf(target));
                out.printf("target 0x%08X -> %s%n", target, function == null ? "<no function>" : describe(function));
            }
            out.println();

            for (Function function : functions) {
                out.println("============================================================");
                out.println(describe(function));
                out.println("body bytes: " + function.getBody().getNumAddresses());
                out.println();

                out.println("CALLERS:");
                ReferenceIterator refsTo = currentProgram.getReferenceManager().getReferencesTo(function.getEntryPoint());
                boolean hasCaller = false;
                while (refsTo.hasNext()) {
                    Reference ref = refsTo.next();
                    Function caller = getFunctionContaining(ref.getFromAddress());
                    out.printf("  %s from %s%n", caller == null ? "<no function>" : describe(caller), ref.getFromAddress());
                    hasCaller = true;
                }
                if (!hasCaller) {
                    out.println("  <none>");
                }
                out.println();

                out.println("CALLS OUT:");
                InstructionIterator instructions = currentProgram.getListing().getInstructions(function.getBody(), true);
                boolean hasCallOut = false;
                while (instructions.hasNext()) {
                    Instruction instruction = instructions.next();
                    for (Reference ref : instruction.getReferencesFrom()) {
                        Function callee = getFunctionAt(ref.getToAddress());
                        if (callee != null) {
                            out.printf("  %s -> %s%n", instruction.getAddress(), describe(callee));
                            hasCallOut = true;
                        }
                    }
                }
                if (!hasCallOut) {
                    out.println("  <none>");
                }
                out.println();

                out.println("DISASSEMBLY:");
                instructions = currentProgram.getListing().getInstructions(function.getBody(), true);
                while (instructions.hasNext()) {
                    Instruction instruction = instructions.next();
                    out.printf("  %s  %s%n", instruction.getAddress(), instruction);
                }
                out.println();

                out.println("DECOMPILE:");
                DecompileResults results = decompiler.decompileFunction(function, 60, monitor);
                if (results != null && results.decompileCompleted()) {
                    out.println(results.getDecompiledFunction().getC());
                } else {
                    out.println("<decompile failed>");
                }
                out.println();
            }

            decompiler.dispose();
        }

        println("Wrote " + outFile.getAbsolutePath());
    }

    private void collectCallers(Function function, Set<Function> functions) {
        ReferenceIterator refsTo = currentProgram.getReferenceManager().getReferencesTo(function.getEntryPoint());
        while (refsTo.hasNext()) {
            Reference ref = refsTo.next();
            Function caller = getFunctionContaining(ref.getFromAddress());
            if (caller != null) {
                functions.add(caller);
            }
        }
    }

    private Address addressOf(long offset) {
        return currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(offset);
    }

    private static String describe(Function function) {
        return function.getName() + " @ " + function.getEntryPoint();
    }
}
