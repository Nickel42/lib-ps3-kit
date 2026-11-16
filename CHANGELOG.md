# Changelog

## 0.1.0-preview.2

- Faster still: encrypted sectors are decrypted many per call into AES (the same bytes); the read-back checks read
  the next 4 MB while the last is hashed; a JB folder's small files are read back several at once when
  `Ps3Options.MaxParallelFiles` allows. `FolderToIso` hashes each file while the image is written instead of while
  it's read, and with `MaxParallelFiles` above 1 reads the small files ahead several at once (about 3x on a folder
  of many small files on an SSD); the image is the same byte for byte. Small extracted files are no longer sized
  before their one write.

- `Ps3Converter.FolderToIso` (CLI `ps3-kit iso`): a JB folder made into a decrypted ISO (one plain region, the
  title ID from PARAM.SFO in the disc header, ISO 9660 with a Joliet tree, files over 4 GiB in several extents),
  checked by reading both trees and every file back.

- Faster conversions: encrypted sectors are read and decrypted many at a time instead of one by one; the whole-image
  pass reads on a handle of its own with the OS's read-ahead on; reading, decrypting, writing and hashing overlap.
- `Ps3Options.SourceBytes`: an ISO conversion hands over the source image's bytes as it reads them, so its hashes
  can be worked out without reading it again.
- JB folders: files are extracted in disc order; `Ps3Options.MaxParallelFiles` (CLI `--parallel N`) extracts small
  files several at once, much faster on an SSD.

## 0.1.0-preview.1

- First version: open PS3 disc images (region table, ISO 9660 with Joliet and multi-extent files, PARAM.SFO),
  tell encrypted from decrypted, check a disc key, decrypt to an ISO, extract a JB folder, re-encrypt, and the
  `ps3-kit` CLI.
