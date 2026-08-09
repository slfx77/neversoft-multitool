// DecompileFunctionsByAddress.java -- Ghidra post-script to decompile one or more
// functions by address from the currently loaded program.
//
// Usage:
//   -postScript DecompileFunctionsByAddress.java <output.c> <addr1> [addr2 ...]
//
// Address arguments may be specified as hexadecimal with or without a 0x prefix.
//
// @category Analysis

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.decompiler.DecompiledFunction;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;

import java.io.FileWriter;
import java.io.PrintWriter;

public class DecompileFunctionsByAddress extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("Usage: DecompileFunctionsByAddress.java <output.c> <addr1> [addr2 ...]");
            return;
        }

        String outputPath = args[0];
        if (outputPath.startsWith("'") && outputPath.endsWith("'")) {
            outputPath = outputPath.substring(1, outputPath.length() - 1);
        }

        FunctionManager functionManager = currentProgram.getFunctionManager();
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);

        PrintWriter out = new PrintWriter(new FileWriter(outputPath));
        out.println("// Targeted Ghidra decompilation by function address");
        out.println("// Program: " + currentProgram.getName());
        out.println();

        int success = 0;
        int failed = 0;

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
                failed++;
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
                failed++;
                continue;
            }

            out.println("// Function: " + function.getName() + " @ " + function.getEntryPoint());
            out.println("// Size: " + function.getBody().getNumAddresses() + " bytes");
            out.println("// ========================================");

            println("Decompiling " + function.getName() + " @ " + function.getEntryPoint());
            DecompileResults result = decompiler.decompileFunction(function, 180, monitor);
            if (result != null) {
                DecompiledFunction decompiledFunction = result.getDecompiledFunction();
                if (decompiledFunction != null) {
                    out.println(decompiledFunction.getC());
                    out.println();
                    success++;
                    continue;
                }

                out.println("// DECOMPILE ERROR: " + result.getErrorMessage());
                out.println();
                failed++;
                continue;
            }

            out.println("// DECOMPILE ERROR: null result");
            out.println();
            failed++;
        }

        out.close();
        decompiler.dispose();

        println("DecompileFunctionsByAddress: success=" + success + ", failed=" + failed +
                ", output=" + outputPath);
    }
}
