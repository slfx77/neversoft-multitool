#!/usr/bin/env python3
"""PSY-Q C++ name demangler for GCC 2.91 (V2 ABI) mangled symbols.

Parses the PSY-Q symbol mangling format used in PlayStation 1 development
(GCC 2.91.66-psx / SN Systems ProDG). Handles free functions, class methods,
pointer/reference types, named types, const qualifiers, and repeat parameters.

Usage:
    # Single name
    python demangle_psyq.py "CheckSum_Calculate__FUiUci"

    # Batch: process symbols.txt, output demangled file
    python demangle_psyq.py --symbols symbols.txt --output symbols_demangled.txt

    # Stdin pipe
    echo "M3d_BuildTransform__FP6CSuper" | python demangle_psyq.py --stdin
"""

import argparse
import re
import sys


class DemangleError(Exception):
    """Raised when a mangled name cannot be parsed."""


def _parse_number(s, pos):
    """Parse a decimal number starting at pos. Returns (number, new_pos)."""
    start = pos
    while pos < len(s) and s[pos].isdigit():
        pos += 1
    if pos == start:
        raise DemangleError(f"Expected digit at position {pos} in '{s}'")
    return int(s[start:pos]), pos


def _parse_type(s, pos, params):
    """Parse a single type from the mangled string.

    Args:
        s: The mangled parameter string (after __F or __<N><Class>).
        pos: Current parse position.
        params: List of already-parsed parameter types (for T<N> back-references).

    Returns:
        (type_string, new_pos)
    """
    if pos >= len(s):
        raise DemangleError(f"Unexpected end of string at position {pos}")

    ch = s[pos]

    # Pointer: P<type>
    if ch == 'P':
        inner, pos = _parse_type(s, pos + 1, params)
        if inner.endswith('*'):
            return f"{inner}*", pos
        return f"{inner} *", pos

    # Reference: R<type>
    if ch == 'R':
        inner, pos = _parse_type(s, pos + 1, params)
        if inner.endswith('*') or inner.endswith('&'):
            return f"{inner}&", pos
        return f"{inner} &", pos

    # Const: C<type>
    if ch == 'C':
        inner, pos = _parse_type(s, pos + 1, params)
        return f"const {inner}", pos

    # Unsigned prefix: U followed by type character
    if ch == 'U':
        if pos + 1 >= len(s):
            raise DemangleError(f"Unexpected end after 'U' at position {pos}")
        next_ch = s[pos + 1]
        if next_ch == 'i':
            return "unsigned int", pos + 2
        elif next_ch == 'c':
            return "unsigned char", pos + 2
        elif next_ch == 's':
            return "unsigned short", pos + 2
        elif next_ch == 'l':
            return "unsigned long", pos + 2
        else:
            raise DemangleError(f"Unknown unsigned type 'U{next_ch}' at position {pos}")

    # Repeat parameter: T<digit>
    if ch == 'T':
        idx, pos = _parse_number(s, pos + 1)
        if idx >= len(params):
            raise DemangleError(f"Back-reference T{idx} but only {len(params)} params parsed")
        return params[idx], pos

    # N-ary repeat: N<count><digit> — repeat param <digit> type <count> times
    if ch == 'N':
        count, pos = _parse_number(s, pos + 1)
        idx, pos = _parse_number(s, pos)
        if idx >= len(params):
            raise DemangleError(f"Back-reference N{count}{idx} but only {len(params)} params parsed")
        return params[idx], pos  # Caller handles the repeat count

    # Simple types
    if ch == 'i':
        return "int", pos + 1
    if ch == 'c':
        return "char", pos + 1
    if ch == 's':
        return "short", pos + 1
    if ch == 'l':
        return "long", pos + 1
    if ch == 'b':
        return "bool", pos + 1
    if ch == 'v':
        return "void", pos + 1
    if ch == 'f':
        return "float", pos + 1
    if ch == 'd':
        return "double", pos + 1
    if ch == 'e':
        return "...", pos + 1

    # Named type: <length><name> (e.g., 7Texture, 6CSuper)
    if ch.isdigit():
        length, pos = _parse_number(s, pos)
        if pos + length > len(s):
            raise DemangleError(f"Named type length {length} exceeds string at position {pos}")
        name = s[pos:pos + length]
        return name, pos + length

    raise DemangleError(f"Unknown type character '{ch}' at position {pos}")


def _parse_params(s, pos, params=None):
    """Parse all parameters from position. Returns list of type strings."""
    if params is None:
        params = []
    while pos < len(s):
        ch = s[pos]
        # Handle N-ary repeat specially: N<count><digit> produces multiple params
        if ch == 'N':
            count, npos = _parse_number(s, pos + 1)
            idx, npos = _parse_number(s, npos)
            if idx >= len(params):
                raise DemangleError(f"Back-reference N{count}{idx} but only {len(params)} params")
            for _ in range(count):
                params.append(params[idx])
            pos = npos
            continue
        type_str, pos = _parse_type(s, pos, params)
        params.append(type_str)
    return params


def demangle(mangled):
    """Demangle a PSY-Q GCC 2.91 C++ mangled name.

    Args:
        mangled: The mangled symbol name (e.g., "CheckSum_Calculate__FUiUci").

    Returns:
        Demangled signature string, or None if the name is not mangled.
    """
    # Find the __ separator. Search from the right to handle names containing __
    # but the mangling always uses the LAST __ that is followed by F or a digit.
    idx = -1
    search_start = 0
    while True:
        found = mangled.find("__", search_start)
        if found == -1:
            break
        after = found + 2
        if after < len(mangled):
            ch = mangled[after]
            if ch == 'F' or ch.isdigit():
                idx = found
        search_start = found + 1

    if idx == -1:
        return None  # Not a mangled name

    func_name = mangled[:idx]
    suffix = mangled[idx + 2:]

    class_name = None
    param_str = suffix

    if suffix and suffix[0] == 'F':
        # Free function: __F<params>
        param_str = suffix[1:]
    elif suffix and suffix[0].isdigit():
        # Class method: __<N><ClassName><params>
        length, pos = _parse_number(suffix, 0)
        if pos + length > len(suffix):
            return None  # Malformed
        class_name = suffix[pos:pos + length]
        param_str = suffix[pos + length:]
    else:
        return None  # Unknown format

    # Parse parameters
    if not param_str or param_str == 'v':
        params = []
    else:
        try:
            params = _parse_params(param_str, 0)
        except DemangleError:
            return None  # Can't parse — return None rather than crash

    # Format output
    param_list = ", ".join(params) if params else "void"
    if class_name:
        return f"{class_name}::{func_name}({param_list})"
    else:
        return f"{func_name}({param_list})"


def process_symbols_file(input_path, output_path):
    """Process a symbols.txt file and write demangled output.

    Output format: 0xADDRESS  MangledName  // demangled signature
    Non-mangled symbols are passed through unchanged.
    """
    lines = []
    with open(input_path) as f:
        for line in f:
            line = line.rstrip('\n')
            if not line:
                lines.append("")
                continue
            parts = line.split(None, 1)
            if len(parts) != 2:
                lines.append(line)
                continue
            addr, name = parts
            demangled = demangle(name)
            if demangled:
                lines.append(f"{addr} {name}  // {demangled}")
            else:
                lines.append(line)

    with open(output_path, 'w') as f:
        f.write('\n'.join(lines) + '\n')

    # Count demangled
    total = sum(1 for l in lines if l.strip())
    demangled_count = sum(1 for l in lines if '//' in l)
    print(f"Processed {total} symbols, demangled {demangled_count}")


def main():
    parser = argparse.ArgumentParser(
        description="PSY-Q C++ name demangler (GCC 2.91 V2 ABI)",
        epilog="Examples:\n"
               "  python demangle_psyq.py 'CheckSum_Calculate__FUiUci'\n"
               "  python demangle_psyq.py --symbols symbols.txt --output demangled.txt\n"
               "  echo 'M3d_BuildTransform__FP6CSuper' | python demangle_psyq.py --stdin",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("name", nargs="?", help="Single mangled name to demangle")
    parser.add_argument("--symbols", help="Path to symbols.txt for batch processing")
    parser.add_argument("--output", help="Output path for demangled symbols (requires --symbols)")
    parser.add_argument("--stdin", action="store_true", help="Read mangled names from stdin (one per line)")

    args = parser.parse_args()

    if args.symbols:
        output = args.output or args.symbols.replace(".txt", "_demangled.txt")
        process_symbols_file(args.symbols, output)
        print(f"Output: {output}")
    elif args.stdin:
        for line in sys.stdin:
            name = line.strip()
            if not name:
                continue
            result = demangle(name)
            if result:
                print(f"{name}  // {result}")
            else:
                print(name)
    elif args.name:
        result = demangle(args.name)
        if result:
            print(result)
        else:
            print(f"Not a mangled name: {args.name}", file=sys.stderr)
            sys.exit(1)
    else:
        parser.print_help()
        sys.exit(1)


if __name__ == "__main__":
    main()
