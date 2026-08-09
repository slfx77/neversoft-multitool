// DumpInstructionsByAddress.java -- Ghidra post-script to dump instructions from addresses
//
// Usage:
//   -postScript DumpInstructionsByAddress.java <output.txt> <addr> <count> [addr2 count2 ...]
//
// Dumps disassembly lines starting at the requested address for the requested
// instruction count.
//
// @category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;

import java.io.FileWriter;
import java.io.PrintWriter;

public class DumpInstructionsByAddress extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3 || (args.length - 1) % 2 != 0) {
            printerr("Usage: DumpInstructionsByAddress.java <output.txt> <addr> <count> [addr2 count2 ...]");
            return;
        }

        String outputPath = args[0];
        if (outputPath.startsWith("'") && outputPath.endsWith("'")) {
            outputPath = outputPath.substring(1, outputPath.length() - 1);
        }

        Listing listing = currentProgram.getListing();
        PrintWriter out = new PrintWriter(new FileWriter(outputPath));
        out.println("// Instruction dumps");
        out.println("// Program: " + currentProgram.getName());
        out.println();

        for (int i = 1; i < args.length; i += 2) {
            String addrText = args[i].trim();
            int count = Integer.parseInt(args[i + 1].trim());

            if (addrText.startsWith("0x") || addrText.startsWith("0X")) {
                addrText = addrText.substring(2);
            }

            long addrValue = Long.parseUnsignedLong(addrText, 16);
            Address address = toAddr(addrValue);

            disassemble(address);

            out.println("// ========================================");
            out.printf("// Requested address: %08x%n", addrValue);
            out.printf("// Instruction count: %d%n", count);
            out.println("// ========================================");

            Instruction instruction = listing.getInstructionAt(address);
            if (instruction == null) {
                instruction = listing.getInstructionAfter(address.subtract(1));
            }

            int dumped = 0;
            while (instruction != null && dumped < count) {
                out.printf("%s: %s%n",
                    instruction.getAddress().toString(),
                    instruction.toString());
                instruction = instruction.getNext();
                dumped++;
            }

            out.println();
            println("DumpInstructionsByAddress: dumped " + dumped + " instructions from 0x" +
                Long.toHexString(addrValue));
        }

        out.close();
        println("DumpInstructionsByAddress: output=" + outputPath);
    }
}
