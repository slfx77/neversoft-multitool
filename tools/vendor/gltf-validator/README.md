# glTF Validator (local dependency)

This directory is the local home for the Khronos [glTF Validator](https://github.com/KhronosGroup/glTF-Validator) command-line distribution. The executable and its upstream `LICENSE`, `NOTICES`, and `docs/` payload remain local/ignored; only this acquisition record is tracked.

Acquire the Windows command-line archive from the upstream project's releases or build instructions and extract its contents directly into this directory.

Recorded local artifact:

- Reported version: `glTF 2.0 Validator, version 2.0.0-dev.3.10`
- File: `gltf_validator.exe`
- SHA-256: `4388A152FF90B68C6430AE03862E05E257A9D50A500ED7D0EB1CD420DC75FF96`

Verify a replacement artifact with:

```powershell
.\tools\vendor\gltf-validator\gltf_validator.exe --version
Get-FileHash -Algorithm SHA256 .\tools\vendor\gltf-validator\gltf_validator.exe
```
