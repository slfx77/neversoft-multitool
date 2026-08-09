// ApplyPsyqSymbols.java -- Ghidra pre-script to create functions from PSY-Q SYM symbols
// Run as -preScript before analysis so Ghidra has function boundaries.
//
// Usage: -preScript ApplyPsyqSymbols.java <symbols.txt>
//   where symbols.txt has lines: "0xADDRESS NAME"
//
// @category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.symbol.SourceType;

import java.io.BufferedReader;
import java.io.FileReader;

public class ApplyPsyqSymbols extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            printerr("Usage: ApplyPsyqSymbols.java <symbols.txt>");
            return;
        }

        String symbolsPath = args[0];
        println("ApplyPsyqSymbols: Loading symbols from " + symbolsPath);

        FunctionManager fm = currentProgram.getFunctionManager();
        int created = 0;
        int skipped = 0;
        int errors = 0;
        int total = 0;

        try (BufferedReader reader = new BufferedReader(new FileReader(symbolsPath))) {
            String line;
            while ((line = reader.readLine()) != null) {
                line = line.trim();
                if (line.isEmpty() || line.startsWith("#")) continue;

                total++;
                int spaceIdx = line.indexOf(' ');
                if (spaceIdx < 0) continue;

                String addrStr = line.substring(0, spaceIdx);
                String name = line.substring(spaceIdx + 1).trim();

                try {
                    long addrVal = Long.parseUnsignedLong(addrStr.replace("0x", "").replace("0X", ""), 16);
                    Address addr = toAddr(addrVal);

                    // Check if address is within loaded memory
                    if (currentProgram.getMemory().contains(addr)) {
                        Function existing = fm.getFunctionAt(addr);
                        if (existing == null) {
                            // Create function at this address
                            createFunction(addr, name);
                            created++;
                        } else {
                            // Rename existing function
                            existing.setName(name, SourceType.IMPORTED);
                            skipped++;
                        }
                    }
                } catch (Exception e) {
                    errors++;
                    if (errors <= 5) {
                        printerr("  Error processing: " + line + " -> " + e.getMessage());
                    }
                }
            }
        }

        println("ApplyPsyqSymbols: Done.");
        println("  Total symbols:  " + total);
        println("  Functions created: " + created);
        println("  Renamed existing:  " + skipped);
        println("  Errors:            " + errors);
    }
}
