// DumpMemoryRange.java -- Ghidra post-script to dump raw bytes from memory
//
// Usage:
//   -postScript DumpMemoryRange.java <output.txt> <addr> <length> [addr2 length2 ...]
//
// Dumps bytes as hex + ASCII, similar to xxd.
//
// @category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;

import java.io.FileWriter;
import java.io.PrintWriter;

public class DumpMemoryRange extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3 || (args.length - 1) % 2 != 0) {
            printerr("Usage: DumpMemoryRange.java <output.txt> <addr> <length> [addr2 length2 ...]");
            return;
        }

        String outputPath = args[0];
        if (outputPath.startsWith("'") && outputPath.endsWith("'")) {
            outputPath = outputPath.substring(1, outputPath.length() - 1);
        }

        Memory memory = currentProgram.getMemory();
        PrintWriter out = new PrintWriter(new FileWriter(outputPath));
        out.println("// Memory dump from " + currentProgram.getName());
        out.println();

        for (int i = 1; i < args.length; i += 2) {
            String addrText = args[i].trim();
            String lenText = args[i + 1].trim();

            if (addrText.startsWith("0x") || addrText.startsWith("0X")) {
                addrText = addrText.substring(2);
            }

            long addrValue = Long.parseUnsignedLong(addrText, 16);
            int length = Integer.parseInt(lenText);
            Address address = toAddr(addrValue);

            out.printf("// Address: 0x%08x, Length: %d bytes%n", addrValue, length);

            byte[] data = new byte[length];
            int bytesRead = memory.getBytes(address, data);

            // Hex dump
            out.println("// Hex dump:");
            for (int row = 0; row < bytesRead; row += 16) {
                out.printf("// %08x: ", addrValue + row);
                StringBuilder ascii = new StringBuilder();
                for (int col = 0; col < 16 && row + col < bytesRead; col++) {
                    out.printf("%02x ", data[row + col] & 0xFF);
                    char c = (char) (data[row + col] & 0xFF);
                    ascii.append(c >= 32 && c < 127 ? c : '.');
                }
                out.printf(" %s%n", ascii);
            }
            out.println();

            // C array
            out.printf("// C array (uint8_t[%d]):%n", bytesRead);
            out.printf("static const uint8_t data_%08x[%d] = {%n    ", addrValue, bytesRead);
            for (int j = 0; j < bytesRead; j++) {
                out.printf("0x%02x", data[j] & 0xFF);
                if (j < bytesRead - 1) {
                    out.print(", ");
                    if ((j + 1) % 16 == 0) out.print("\n    ");
                }
            }
            out.println("\n};");
            out.println();

            println("DumpMemoryRange: dumped " + bytesRead + " bytes from 0x" +
                    Long.toHexString(addrValue));
        }

        out.close();
        println("DumpMemoryRange: output=" + outputPath);
    }
}
