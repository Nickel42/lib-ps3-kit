# Changelog

## Unreleased

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
