# lib-ps3-kit

A dependency-free C# library for PlayStation 3 disc images. It reads a Redump ISO's region table and decrypts the image with its disc key (`.dkey`), either into a **decrypted ISO** (same size and layout, which RPCS3 loads) or into a **JB folder** (`PS3_DISC.SFB`, `PS3_GAME/…`). It can also make a JB folder back into a decrypted ISO. It checks each result, and can re-encrypt a decrypted ISO back to the Redump image byte for byte. No native code, no package dependencies.

| Package | What it is |
|---|---|
| **lib-ps3-kit** | The library (namespace `LibPs3Kit`) |
| **lib-ps3-kit.Cli** | The `ps3-kit` command-line tool |

It targets `net8.0` and `net10.0` and is marked trim- and AOT-compatible.

## What it does

| Capability | Notes |
|---|---|
| Open | `Ps3Disc.Open` reads the region table (sector 0), the product code (sector 1) and the ISO 9660 filesystem (Joliet names when present; multi-extent files over 4 GiB) |
| State | `Encrypted`, `Decrypted` (PS3Dec, 3k3y or this library), `NoEncryptedRegions`, or `Unknown`, told from files in encrypted regions with a known header (SELF, SFO, PNG, SDAT) |
| Key check | `KeyFits` decrypts those files' first sector and checks their header, so a wrong key fails before anything is written |
| Decrypted ISO | `Ps3Converter.ToDecryptedIso` |
| JB folder | `Ps3Converter.ToJbFolder`, optionally without `PS3_UPDATE` |
| Folder to ISO | `Ps3Converter.FolderToIso`: a JB folder back into a decrypted ISO (ISO 9660 + Joliet, files over 4 GiB in several extents), checked by reading every file back |
| Re-encrypt | `Ps3Converter.ToEncryptedIso` gives back the Redump image exactly |
| Verify | The output is read back and compared with the SHA-1 of what was written; a decrypted output must no longer look encrypted, and a JB folder's `EBOOT.BIN` must be a decrypted SELF |
| Safety | Output is written to `<output>.partial` and renamed only once checked; unsafe names on the disc (`..`, separators) are refused |
| PARAM.SFO | `ParamSfo` reads the title, title ID and version |

The disc key is the "Disc Key" Redump publishes for each PS3 disc: 32 hex digits, usually in a `.dkey` file named like the image. `DiscKey.FindFileFor` looks beside the image, then in any folders you give it.

## Quick start

```csharp
using LibPs3Kit;

var iso = "Demon's Souls (USA).iso";
var key = DiscKey.FindFileFor(iso) is { } file ? DiscKey.Load(file) : null;

var result = Ps3Converter.ToDecryptedIso(iso, "Demon's Souls (USA) (decrypted).iso", key);
// or: Ps3Converter.ToJbFolder(iso, "Demon's Souls (USA)", key);
Console.WriteLine($"{result.TitleId} {result.Title}: {result.Verification?.Success}");
```

## Command line

```
ps3-kit info    <image.iso> [--key HEX | --dkey FILE]
ps3-kit ls      <image.iso>
ps3-kit decrypt <image.iso> <out.iso>    [--key HEX | --dkey FILE] [--overwrite] [--no-verify]
ps3-kit jb      <image.iso> <out-folder> [--key HEX | --dkey FILE] [--no-update] [--parallel N] [--overwrite] [--no-verify]
ps3-kit encrypt <decrypted.iso> <out.iso> (--key HEX | --dkey FILE) [--overwrite] [--no-verify]
ps3-kit iso     <jb-folder> <out.iso>    [--no-update] [--overwrite] [--no-verify]
```

## How the image is laid out

Sector 0 holds a big-endian count N of plain regions, four bytes, then 2N big-endian sector numbers. Plain region k runs from v(2k) to v(2k+1) inclusive; the encrypted region between two plain ones runs from v(2k+1)+1 to v(2k+2)-1. Encrypted sectors are AES-128-CBC with the disc key and an IV of zeros whose last four bytes are the sector number, big-endian.

## License

MIT
