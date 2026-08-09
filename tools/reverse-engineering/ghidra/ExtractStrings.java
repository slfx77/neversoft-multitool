// ExtractStrings.java -- GHIDRA Java postScript for QBKey candidate extraction
//
// Extracts all referenced strings from an analyzed binary, filters for
// plausible Neversoft asset names, and writes them one-per-line to an output file.
//
// Usage (via analyzeHeadless):
//   analyzeHeadless <project> <name> -import <binary> \
//     -postScript ExtractStrings.java <output_path> \
//     -deleteProject
//
// @category QBKey

import ghidra.app.script.GhidraScript;
import ghidra.program.model.data.StringDataInstance;
import ghidra.program.model.listing.Data;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolTable;
import ghidra.program.util.DefinedStringIterator;

import java.io.*;
import java.util.*;
import java.util.regex.Pattern;

public class ExtractStrings extends GhidraScript {

    private static final int MIN_LEN = 3;
    private static final int MAX_LEN = 64;

    private static final Set<String> EXCLUDE_KEYWORDS = Set.of(
        "int", "void", "char", "long", "short", "unsigned", "signed", "const",
        "static", "return", "while", "break", "case", "else", "enum", "struct",
        "class", "this", "null", "true", "false", "bool", "for", "switch",
        "default", "typedef", "extern", "volatile", "register", "sizeof",
        "union", "goto", "continue", "inline", "float", "double", "string",
        "printf", "fprintf", "sprintf", "malloc", "calloc", "realloc", "free",
        "memcpy", "memset", "strlen", "strcpy", "strcat", "strcmp", "strncmp",
        "fopen", "fclose", "fread", "fwrite", "fseek", "ftell", "fgets",
        "abort", "exit", "assert", "include", "define", "ifdef", "ifndef",
        "endif", "pragma", "error", "warning", "main", "argc", "argv",
        "stdin", "stdout", "stderr", "eof", "nullptr",
        "virtual", "override", "public", "private", "protected", "friend",
        "template", "typename", "namespace", "using", "try", "catch", "throw",
        "new", "delete", "operator", "explicit", "mutable", "constexpr",
        "alignof", "decltype", "noexcept", "static_assert", "thread_local"
    );

    private static final Set<String> EXCLUDE_EXTENSIONS = Set.of(
        ".dll", ".lib", ".obj", ".pdb", ".sys", ".drv", ".ocx"
    );

    private static final String[] EXCLUDE_PREFIXES = {
        "microsoft", "windows", "kernel", "user", "gdi", "advapi",
        "hkey", "reg_", "clsid", "iid_"
    };

    // Must start with letter, then alphanumeric + underscore + dot + dash + slash
    private static final Pattern PLAUSIBLE_RE = Pattern.compile("^[A-Za-z][A-Za-z0-9_./\\-]*$");

    // ALL_CAPS_WITH_UNDERSCORES
    private static final Pattern MACRO_RE = Pattern.compile("^[A-Z_]+$");

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        String outputPath = args.length >= 1 ? args[0] : null;

        println("ExtractStrings.java: Starting string extraction...");

        // Collect from all three tiers
        Set<String> tier1 = collectDefinedStrings();
        println("  Tier 1 (defined strings): " + tier1.size());

        Set<String> tier2 = collectSymbolNames();
        println("  Tier 2 (symbol names): " + tier2.size());

        Set<String> tier3 = collectDataSectionStrings();
        println("  Tier 3 (data section scan): " + tier3.size());

        Set<String> allRaw = new HashSet<>();
        allRaw.addAll(tier1);
        allRaw.addAll(tier2);
        allRaw.addAll(tier3);
        println("  Combined raw: " + allRaw.size());

        // Filter and generate variants
        Set<String> finalNames = new TreeSet<>();
        for (String s : allRaw) {
            if (isPlausibleName(s)) {
                String low = s.toLowerCase();
                finalNames.add(low);

                // Also add stem (filename without extension)
                String stem = getStem(low);
                if (stem != null) {
                    finalNames.add(stem);
                }
            }
        }

        println("  After filtering: " + finalNames.size() + " candidate names");

        // Write output
        if (outputPath != null) {
            // Strip surrounding quotes if present (GHIDRA may pass them)
            if (outputPath.startsWith("'") && outputPath.endsWith("'")) {
                outputPath = outputPath.substring(1, outputPath.length() - 1);
            }

            File parent = new File(outputPath).getParentFile();
            if (parent != null && !parent.exists()) {
                parent.mkdirs();
            }

            try (PrintWriter writer = new PrintWriter(new FileWriter(outputPath))) {
                for (String name : finalNames) {
                    writer.println(name);
                }
            }
            println("  Wrote " + finalNames.size() + " names to " + outputPath);
        } else {
            for (String name : finalNames) {
                println(name);
            }
        }

        println("ExtractStrings.java: Done.");
    }

    /** Tier 1: Strings identified by GHIDRA's auto-analysis. */
    private Set<String> collectDefinedStrings() {
        Set<String> strings = new HashSet<>();
        for (Data data : DefinedStringIterator.forProgram(currentProgram)) {
            StringDataInstance sdi = StringDataInstance.getStringDataInstance(data);
            String s = sdi.getStringValue();
            if (s != null && !s.isEmpty()) {
                strings.add(s);
            }
        }
        return strings;
    }

    /** Tier 2: Symbol/label names (may contain asset name fragments). */
    private Set<String> collectSymbolNames() {
        Set<String> strings = new HashSet<>();
        SymbolTable st = currentProgram.getSymbolTable();
        for (Symbol symbol : st.getAllSymbols(true)) {
            String name = symbol.getName();
            if (name == null || name.isEmpty()) continue;

            // Skip auto-generated GHIDRA labels
            if (name.startsWith("FUN_") || name.startsWith("DAT_") ||
                name.startsWith("LAB_") || name.startsWith("PTR_") ||
                name.startsWith("s_") || name.startsWith("switchD_") ||
                name.startsWith("caseD_") || name.startsWith("sub_") ||
                name.startsWith("thunk_") || name.startsWith("EXTERNAL_") ||
                name.startsWith("entry") || name.startsWith("Ordinal_")) {
                continue;
            }
            strings.add(name);
        }
        return strings;
    }

    /** Tier 3: Scan all initialized memory blocks for null-terminated ASCII strings. */
    private Set<String> collectDataSectionStrings() throws Exception {
        Set<String> strings = new HashSet<>();

        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            // Scan all readable, initialized blocks (including executable ones,
            // since PS1 MIPS binaries have string data in code segments)
            if (!block.isInitialized() || !block.isRead()) continue;

            long size = block.getSize();
            if (size > 16 * 1024 * 1024) continue; // Skip blocks > 16MB

            byte[] buf = new byte[(int) size];
            block.getBytes(block.getStart(), buf);

            StringBuilder current = new StringBuilder();
            for (byte b : buf) {
                int c = b & 0xFF;
                if (c >= 0x20 && c <= 0x7E) {
                    current.append((char) c);
                } else {
                    if (current.length() >= MIN_LEN && current.length() <= MAX_LEN) {
                        strings.add(current.toString());
                    }
                    current.setLength(0);
                }
            }
            // Trailing string
            if (current.length() >= MIN_LEN && current.length() <= MAX_LEN) {
                strings.add(current.toString());
            }
        }

        return strings;
    }

    /** Returns true if s looks like a plausible Neversoft asset name. */
    private static boolean isPlausibleName(String s) {
        if (s == null || s.length() < MIN_LEN || s.length() > MAX_LEN) return false;
        if (!PLAUSIBLE_RE.matcher(s).matches()) return false;

        String low = s.toLowerCase();

        // C/C++ keywords
        if (EXCLUDE_KEYWORDS.contains(low)) return false;

        // Compiler builtins (__xxx)
        if (s.startsWith("__")) return false;

        // Reserved identifiers (_Uppercase)
        if (s.length() > 1 && s.charAt(0) == '_' && Character.isUpperCase(s.charAt(1)))
            return false;

        // GHIDRA auto-generated labels that may appear in memory
        if (low.startsWith("sub_") || low.startsWith("thunk_") || low.startsWith("fun_") ||
            low.startsWith("dat_") || low.startsWith("lab_") || low.startsWith("ptr_"))
            return false;

        // ALL_CAPS_MACRO names (5+ chars with underscore)
        if (s.length() >= 5 && s.contains("_") && MACRO_RE.matcher(s).matches())
            return false;

        // System file extensions
        int dot = s.lastIndexOf('.');
        if (dot >= 0) {
            String ext = s.substring(dot).toLowerCase();
            if (EXCLUDE_EXTENSIONS.contains(ext)) return false;
        }

        // Windows/system prefixes
        for (String prefix : EXCLUDE_PREFIXES) {
            if (low.startsWith(prefix)) return false;
        }

        // UNC paths and drive paths
        if (s.startsWith("\\\\")) return false;
        if (s.length() >= 3 && Character.isLetter(s.charAt(0)) && s.charAt(1) == ':' && s.charAt(2) == '\\')
            return false;

        return true;
    }

    /** Return filename without extension, or null if same as input. */
    private static String getStem(String name) {
        int dot = name.lastIndexOf('.');
        if (dot > 0) {
            String stem = name.substring(0, dot);
            if (stem.length() >= MIN_LEN) return stem;
        }
        return null;
    }
}
