#!/usr/bin/env python3
"""Recover THUG2's bundled same-build unprotected executable from CD3.

The supplied scene-release CD3 contains ``CRACK/THUG2.EXE`` as an ordinary
ISO9660 file.  It is a five-section, same-build executable with the game's
original entry point and complete import table; it is not an output produced by
the SafeDisc emulator.  This command verifies the exact known CD3 image and
protected executable, validates the bundled executable before writing it, and
refuses to overwrite any existing path.

Usage::

    python tools/diagnostics/thug2_cd3_recover.py \
        path/to/rld-thuc.bin path/to/protected/THUG2.exe output/THUG2.exe
"""

from __future__ import annotations

import argparse
import hashlib
from dataclasses import dataclass
from pathlib import Path

import pefile

from iso9660_reader import DiscImage


IMAGE_DIRECTORY_ENTRY_IMPORT = 1
HASH_CHUNK_SIZE = 1024 * 1024


class RecoveryError(ValueError):
    """Raised when an input does not match the known recovery source."""


@dataclass(frozen=True)
class SectionIdentity:
    name: str
    virtual_address: int
    virtual_size: int
    raw_offset: int
    raw_size: int
    characteristics: int


@dataclass(frozen=True)
class PeIdentity:
    sha256: str
    size: int
    timestamp: int
    image_base: int
    entry_rva: int
    image_size: int
    import_rva: int
    import_size: int
    sections: tuple[SectionIdentity, ...]
    imports: tuple[tuple[str, int], ...] | None = None


@dataclass(frozen=True)
class RecoveryIdentity:
    disc_sha256: str
    volume_id: str
    volume_sectors: int
    embedded_path: str
    embedded_lba: int
    protected: PeIdentity
    recovered: PeIdentity


CORE_SECTIONS = (
    SectionIdentity(".text", 0x1000, 0x243ED2, 0x1000, 0x244000, 0x60000020),
    SectionIdentity(".rdata", 0x245000, 0x365D5, 0x245000, 0x37000, 0x40000040),
    SectionIdentity(".data", 0x27C000, 0x160A80, 0x27C000, 0x14000, 0xC0000040),
    SectionIdentity(".tls", 0x3DD000, 0x9, 0x290000, 0x1000, 0xC0000040),
    SectionIdentity(".rsrc", 0x3DE000, 0xDA6, 0x291000, 0x1000, 0x40000040),
)

THUG2_IDENTITY = RecoveryIdentity(
    disc_sha256="cc74ac7cfc458c342fdcccd6533c0a67e3102597bbe2f8bf97782ef02786ac5d",
    volume_id="THUG2_3",
    volume_sectors=340_964,
    embedded_path="CRACK/THUG2.EXE",
    embedded_lba=111,
    protected=PeIdentity(
        sha256="c34ea46e041d08d7d85565a262473c29b90ed8a4d5b740d6cc04d4fe48d52347",
        size=3_926_726,
        timestamp=0x41477593,
        image_base=0x400000,
        entry_rva=0x3E209E,
        image_size=0x3E6000,
        import_rva=0x3E5070,
        import_size=0x104,
        sections=CORE_SECTIONS + (
            SectionIdentity("stxt774", 0x3DF000, 0x2063, 0x293000, 0x3000,
                            0xE0000020),
            SectionIdentity("stxt371", 0x3E2000, 0x33D2, 0x296000, 0x4000,
                            0xE0000020),
        ),
    ),
    recovered=PeIdentity(
        sha256="52fc88849654b34839ec2f96bff3a8c0b7a855df9a207aab9f2fca2e6bd440f3",
        size=2_695_168,
        timestamp=0x41477593,
        image_base=0x400000,
        entry_rva=0x22583D,
        image_size=0x3DEDA6,
        import_rva=0x27A564,
        import_size=0xF0,
        sections=CORE_SECTIONS,
        imports=(
            ("binkw32.dll", 15),
            ("WS2_32.dll", 17),
            ("d3d9.dll", 1),
            ("WINMM.dll", 2),
            ("DINPUT8.dll", 1),
            ("DSOUND.dll", 2),
            ("KERNEL32.dll", 114),
            ("USER32.dll", 22),
            ("GDI32.dll", 1),
            ("ADVAPI32.dll", 7),
            ("WSOCK32.dll", 11),
        ),
    ),
)


@dataclass(frozen=True)
class RecoveryResult:
    output: Path
    size: int
    sha256: str
    import_descriptors: int
    import_thunks: int


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(HASH_CHUNK_SIZE):
            digest.update(chunk)
    return digest.hexdigest()


def checked_hash(actual: str, expected: str, label: str) -> None:
    if actual.lower() != expected.lower():
        raise RecoveryError(
            f"{label} SHA-256 mismatch: expected {expected.lower()}, got {actual.lower()}"
        )


def section_identity(section: pefile.SectionStructure) -> SectionIdentity:
    try:
        name = section.Name.rstrip(b"\0").decode("ascii")
    except UnicodeDecodeError as exc:
        raise RecoveryError("PE contains a non-ASCII section name") from exc
    return SectionIdentity(
        name,
        section.VirtualAddress,
        section.Misc_VirtualSize,
        section.PointerToRawData,
        section.SizeOfRawData,
        section.Characteristics,
    )


def validate_pe(data: bytes, expected: PeIdentity, label: str) -> tuple[pefile.PE, int, int]:
    if len(data) != expected.size:
        raise RecoveryError(
            f"{label} size mismatch: expected {expected.size:,}, got {len(data):,}"
        )
    checked_hash(hashlib.sha256(data).hexdigest(), expected.sha256, label)

    try:
        pe = pefile.PE(data=data, fast_load=False)
    except pefile.PEFormatError as exc:
        raise RecoveryError(f"{label} is not the expected PE: {exc}") from exc

    fields = (
        ("machine", pe.FILE_HEADER.Machine, 0x14C),
        ("PE magic", pe.OPTIONAL_HEADER.Magic, 0x10B),
        ("timestamp", pe.FILE_HEADER.TimeDateStamp, expected.timestamp),
        ("image base", pe.OPTIONAL_HEADER.ImageBase, expected.image_base),
        ("entry-point RVA", pe.OPTIONAL_HEADER.AddressOfEntryPoint,
         expected.entry_rva),
        ("image size", pe.OPTIONAL_HEADER.SizeOfImage, expected.image_size),
    )
    for field, actual, wanted in fields:
        if actual != wanted:
            raise RecoveryError(
                f"{label} {field} mismatch: expected 0x{wanted:X}, got 0x{actual:X}"
            )

    import_directory = pe.OPTIONAL_HEADER.DATA_DIRECTORY[IMAGE_DIRECTORY_ENTRY_IMPORT]
    if (import_directory.VirtualAddress, import_directory.Size) != (
            expected.import_rva, expected.import_size):
        raise RecoveryError(
            f"{label} import directory mismatch: expected RVA/size "
            f"0x{expected.import_rva:X}/0x{expected.import_size:X}, got "
            f"0x{import_directory.VirtualAddress:X}/0x{import_directory.Size:X}"
        )

    actual_sections = tuple(section_identity(section) for section in pe.sections)
    if actual_sections != expected.sections:
        raise RecoveryError(
            f"{label} section layout mismatch: expected {expected.sections!r}, "
            f"got {actual_sections!r}"
        )

    descriptor_count = 0
    thunk_count = 0
    if expected.imports is not None:
        pe.parse_data_directories(
            directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]]
        )
        descriptors = getattr(pe, "DIRECTORY_ENTRY_IMPORT", ())
        try:
            actual_imports = tuple(
                (descriptor.dll.decode("ascii"), len(descriptor.imports))
                for descriptor in descriptors
            )
        except UnicodeDecodeError as exc:
            raise RecoveryError(f"{label} contains a non-ASCII import DLL") from exc
        if actual_imports != expected.imports:
            raise RecoveryError(
                f"{label} import descriptors mismatch: expected {expected.imports!r}, "
                f"got {actual_imports!r}"
            )
        descriptor_count = len(actual_imports)
        thunk_count = sum(count for _dll, count in actual_imports)

    return pe, descriptor_count, thunk_count


def validate_output_path(disc_path: Path, protected_path: Path, output_path: Path) -> None:
    if output_path.exists():
        raise RecoveryError(f"refusing to overwrite existing output: {output_path}")
    output_resolved = output_path.resolve()
    for label, source in (("CD3 image", disc_path),
                          ("protected executable", protected_path)):
        if output_resolved == source.resolve():
            raise RecoveryError(f"refusing to overwrite the {label}: {source}")


def extract_embedded(disc_path: Path, expected: RecoveryIdentity) -> bytes:
    checked_hash(sha256_file(disc_path), expected.disc_sha256, "CD3 image")

    disc = DiscImage(disc_path)
    try:
        pvd = disc.pvd()
        if pvd["type"] != 1 or pvd["magic"] != "CD001" or pvd["block_size"] != 2048:
            raise RecoveryError("CD3 image does not contain the expected ISO9660 PVD")
        if pvd["volume_id"] != expected.volume_id:
            raise RecoveryError(
                f"CD3 volume mismatch: expected {expected.volume_id!r}, "
                f"got {pvd['volume_id']!r}"
            )
        if pvd["sectors"] != expected.volume_sectors or disc.sectors != expected.volume_sectors:
            raise RecoveryError(
                f"CD3 sector count mismatch: expected {expected.volume_sectors:,}, "
                f"PVD/file report {pvd['sectors']:,}/{disc.sectors:,}"
            )

        matches = [entry for entry in disc.walk(pvd["root_lba"], pvd["root_size"])
                   if entry[0].casefold() == expected.embedded_path.casefold()]
        if len(matches) != 1:
            raise RecoveryError(
                f"expected exactly one {expected.embedded_path!r} entry, found "
                f"{len(matches)}"
            )
        iso_path, lba, size = matches[0]
        if lba != expected.embedded_lba or size != expected.recovered.size:
            raise RecoveryError(
                f"{iso_path} extent mismatch: expected LBA/size "
                f"{expected.embedded_lba}/{expected.recovered.size}, got {lba}/{size}"
            )
        return disc.read_file(lba, size)
    finally:
        disc.handle.close()


def recover(disc_path: Path, protected_path: Path, output_path: Path,
            expected: RecoveryIdentity = THUG2_IDENTITY) -> RecoveryResult:
    for label, path in (("CD3 image", disc_path),
                        ("protected executable", protected_path)):
        if not path.is_file():
            raise RecoveryError(f"{label} does not exist: {path}")
    validate_output_path(disc_path, protected_path, output_path)

    protected_data = protected_path.read_bytes()
    protected_pe, _protected_descriptors, _protected_thunks = validate_pe(
        protected_data, expected.protected, "protected executable"
    )
    recovered_data = extract_embedded(disc_path, expected)
    recovered_pe, descriptor_count, thunk_count = validate_pe(
        recovered_data, expected.recovered, "bundled unprotected executable"
    )

    # Make the same-build relationship explicit in addition to the exact hashes.
    if (protected_pe.FILE_HEADER.TimeDateStamp != recovered_pe.FILE_HEADER.TimeDateStamp
            or protected_pe.OPTIONAL_HEADER.ImageBase
            != recovered_pe.OPTIONAL_HEADER.ImageBase):
        raise RecoveryError("protected and bundled executables are not the same build")
    protected_core = tuple(section_identity(section)
                           for section in protected_pe.sections[:len(recovered_pe.sections)])
    recovered_core = tuple(section_identity(section) for section in recovered_pe.sections)
    if protected_core != recovered_core:
        raise RecoveryError("protected and bundled executables have different core sections")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("xb") as handle:
        handle.write(recovered_data)

    return RecoveryResult(
        output=output_path,
        size=len(recovered_data),
        sha256=hashlib.sha256(recovered_data).hexdigest(),
        import_descriptors=descriptor_count,
        import_thunks=thunk_count,
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("disc", type=Path, help="supplied CD3 raw MODE1/2352 BIN")
    parser.add_argument("protected", type=Path, help="matching protected THUG2.exe")
    parser.add_argument("output", type=Path,
                        help="new path for the bundled unprotected executable")
    args = parser.parse_args()

    try:
        result = recover(args.disc, args.protected, args.output)
    except (OSError, RecoveryError) as exc:
        parser.error(str(exc))

    print(f"verified exact CD3 and protected THUG2 build 0x{THUG2_IDENTITY.protected.timestamp:08X}")
    print(f"recovered {THUG2_IDENTITY.embedded_path} to {result.output}")
    print(f"{result.size:,} bytes; SHA-256 {result.sha256}")
    print(f"entry RVA 0x{THUG2_IDENTITY.recovered.entry_rva:X}; "
          f"{result.import_descriptors} import descriptors, "
          f"{result.import_thunks} import thunks")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
