using System.Security.Cryptography;

namespace LibPs3Kit;

public static partial class Ps3Converter
{
    /// <summary>
    /// A JB folder (PS3_DISC.SFB, PS3_GAME/…) made into a decrypted PS3 disc image: the region table lists one plain
    /// region (nothing to decrypt), sector 1 holds the title ID from PS3_GAME/PARAM.SFO, and the files are in an ISO
    /// 9660 filesystem with a Joliet tree. RPCS3 loads it as it loads a decrypted Redump image. The check opens the
    /// new image, compares both of its trees with the folder, and reads every file back.
    /// </summary>
    public static Ps3Result FolderToIso(string folder, string output, Ps3Options? options = null,
        IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        options ??= new();
        var report = new ThrottledProgress(progress);
        var source = Path.GetFullPath(folder);
        report.Report(new(OperationStage.Opening, 0, 0, source));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"{source} doesn't exist.");
        var sfoPath = Path.Combine(source, "PS3_GAME", "PARAM.SFO");
        if (!File.Exists(sfoPath))
            throw new Ps3Exception(Ps3Error.NotPs3Disc, $"{Path.GetFileName(source)} isn't a PS3 game folder (it has no PS3_GAME/PARAM.SFO).");
        ParamSfo? sfo;
        using (var s = File.OpenRead(sfoPath)) sfo = ParamSfo.TryRead(s);
        var titleId = DiscTitleId(sfo?.TitleId);

        var final = Path.GetFullPath(output);
        var inside = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
        if (final.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The ISO can't go inside the folder it's made from.", nameof(output));
        var root = Scan(source, options.IncludeUpdate, token);
        var layout = Iso9660Writer.Build(root, titleId, options.MaxExtentSize, DateTime.UtcNow);
        PrepareOutput(final, options.Overwrite, isFolder: false);
        var partial = final + ".partial";
        var total = layout.TotalSectors * RegionTable.SectorSize;
        try
        {
            // Empty files have no data to read; their hash is the empty one.
            var hashes = layout.Files.Where(f => f.Size == 0).ToDictionary(f => f, _ => SHA1.HashData(ReadOnlySpan<byte>.Empty));
            if (File.Exists(partial)) File.Delete(partial);
            using (var image = new FolderImageStream(layout, hashes))
            using (var outFile = CreateOutput(partial, total))
            {
                long done = 0;
                Pump(image, new byte[options.ChunkSize], new byte[options.ChunkSize], null,
                    (chunk, n) => outFile.Write(chunk, 0, n),
                    n =>
                    {
                        done += n;
                        report.Report(new(OperationStage.Writing, done, total, image.Current));
                    }, token);
                if (done != total) throw new IOException($"The image came out the wrong size ({done} of {total} bytes).");
            }

            Ps3Verification? verification = null;
            if (options.Verify)
            {
                verification = VerifyBuilt(partial, layout, hashes, titleId, options, report, token);
                if (!verification.Success) throw new Ps3Exception(Ps3Error.VerifyFailed, "The new image failed its check: " + string.Join("; ", verification.Problems));
            }
            if (File.Exists(final)) File.Delete(final);
            File.Move(partial, final);
            report.Report(new(OperationStage.Finished, total, total));
            return new Ps3Result(final, total, layout.Files.Count, verification, sfo?.TitleId ?? titleId, sfo?.Title);
        }
        catch
        {
            TryDelete(partial, isFolder: false);
            throw;
        }
    }

    /// <summary>The disc header's form of a title ID: "BLUS30443" becomes "BLUS-30443".</summary>
    internal static string? DiscTitleId(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId)) return null;
        var id = titleId.Trim();
        return id.Length == 9 && !id.Contains('-') ? id[..4] + "-" + id[4..] : id;
    }

    /// <summary>The folder's tree. Links (junctions, symlinks) are left out: a disc has none.</summary>
    private static IsoNode Scan(string folder, bool includeUpdate, CancellationToken token)
    {
        var info = new DirectoryInfo(folder);
        var root = new IsoNode { Name = "", Source = folder, DiscPath = "", IsDirectory = true, Time = info.LastWriteTimeUtc };
        var depth = 0;
        void Walk(IsoNode node, DirectoryInfo dir)
        {
            if (++depth > 64) throw new Ps3Exception(Ps3Error.BadFilesystem, "The folder's subfolders nest too deeply.");
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var path = node.DiscPath.Length == 0 ? item.Name : node.DiscPath + "/" + item.Name;
                if (!includeUpdate && node.DiscPath.Length == 0 && InUpdate(path)) continue;
                var isDir = item is DirectoryInfo;
                var child = new IsoNode
                {
                    Name = item.Name, Source = item.FullName, DiscPath = path, IsDirectory = isDir,
                    Size = item is FileInfo f ? f.Length : 0, Time = item.LastWriteTimeUtc,
                };
                node.Children.Add(child);
                if (isDir) Walk(child, (DirectoryInfo)item);
            }
            depth--;
        }
        Walk(root, info);
        return root;
    }

    /// <summary>Reads the new image back: both trees list the folder's files at their sizes, and every file hashes as written.</summary>
    private static Ps3Verification VerifyBuilt(string path, IsoLayout layout, Dictionary<IsoNode, byte[]> hashes, string? titleId,
        Ps3Options options, IProgress<OperationProgress> report, CancellationToken token)
    {
        var problems = new List<string>();
        using var disc = Ps3Disc.Open(path);
        if (disc.State != EncryptionState.NoEncryptedRegions) problems.Add("its region table doesn't read as a decrypted disc");
        if (titleId is not null && disc.DiscTitleId != titleId) problems.Add($"its disc header reads {disc.DiscTitleId ?? "no title ID"}");
        using var view = disc.OpenDecryptedSequential(null);

        var joliet = Iso9660Volume.TryOpen(view, preferJoliet: true);
        var primary = Iso9660Volume.TryOpen(view, preferJoliet: false);
        if (joliet is null || !joliet.IsJoliet || primary is null) return new(false, ["its filesystem can't be read"]);
        var expected = layout.Files.ToDictionary(f => f.DiscPath, f => f.Size, StringComparer.Ordinal);
        Compare("Joliet", joliet.Entries(), expected, layout.Folders.Skip(1).Select(d => d.DiscPath), problems);
        Compare("primary", primary.Entries(), layout.Files.ToDictionary(f => f.PrimaryPath, f => f.Size, StringComparer.Ordinal),
            layout.Folders.Skip(1).Select(d => d.PrimaryPath), problems);
        if (problems.Count > 0) return new(false, problems.Take(20).ToList());

        var entries = joliet.Entries().Where(e => !e.IsDirectory).ToDictionary(e => e.Path, StringComparer.Ordinal);
        byte[] a = new byte[options.ChunkSize], b = new byte[options.ChunkSize];
        long total = layout.Files.Sum(f => f.Size), done = 0;
        foreach (var file in layout.Files)
        {
            token.ThrowIfCancellationRequested();
            using var s = Iso9660Volume.OpenFile(view, entries[file.DiscPath]);
            var hash = HashOf(s, file.Size, a, b, n => report.Report(new(OperationStage.Verifying, done += n, total)), token);
            if (!hash.AsSpan().SequenceEqual(hashes[file])) problems.Add($"{file.DiscPath} doesn't read back as written");
        }
        return new Ps3Verification(problems.Count == 0, problems.Take(20).ToList());
    }

    private static void Compare(string tree, IReadOnlyList<IsoEntry> found, Dictionary<string, long> files, IEnumerable<string> folders,
        List<string> problems)
    {
        var seen = found.ToDictionary(e => e.Path, StringComparer.Ordinal);
        foreach (var (p, size) in files)
            if (!seen.TryGetValue(p, out var e) || e.IsDirectory || e.Size != size) problems.Add($"{p} is missing or the wrong size in the {tree} tree");
        foreach (var p in folders)
            if (!seen.TryGetValue(p, out var e) || !e.IsDirectory) problems.Add($"the folder {p} is missing from the {tree} tree");
        var extra = found.Count - files.Count - folders.Count();
        if (extra != 0) problems.Add($"the {tree} tree lists {found.Count} entries, not {files.Count + folders.Count()}");
    }

    /// <summary>
    /// The new image's bytes front to back: the metadata, then each file at its sector with zeros up to the next.
    /// Each file is hashed as it's read, and must be the size it was when the folder was scanned.
    /// </summary>
    private sealed class FolderImageStream(IsoLayout layout, Dictionary<IsoNode, byte[]> hashes) : Stream
    {
        private readonly long _length = layout.TotalSectors * RegionTable.SectorSize;
        private long _position;
        private int _file = -1; // the file being read, by index in layout.Files
        private FileStream? _input;
        private IncrementalHash? _sha;

        /// <summary>The file being read, for progress.</summary>
        public string? Current => _file >= 0 && _file < layout.Files.Count ? layout.Files[_file].DiscPath : null;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            while (buffer.Length > 0 && _position < _length)
            {
                int n;
                if (_position < layout.Metadata.Length)
                {
                    n = (int)Math.Min(buffer.Length, layout.Metadata.Length - _position);
                    layout.Metadata.AsSpan((int)_position, n).CopyTo(buffer);
                }
                else
                {
                    var next = NextData();
                    if (next is null || _position < next.Value.Start)
                    {
                        // Zeros: the end of a file's last sector, or up to the image's end.
                        var upTo = next?.Start ?? _length;
                        n = (int)Math.Min(buffer.Length, upTo - _position);
                        buffer[..n].Clear();
                    }
                    else
                    {
                        var (start, file) = next.Value;
                        n = (int)Math.Min(buffer.Length, start + file.Size - _position);
                        _input ??= new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
                        _sha ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                        if (_input.ReadAtLeast(buffer[..n], n, throwOnEndOfStream: false) < n)
                            throw new IOException($"{file.DiscPath} got shorter while the image was being made.");
                        _sha.AppendData(buffer[..n]);
                        if (_position + n == start + file.Size) FinishFile();
                    }
                }
                _position += n;
                total += n;
                buffer = buffer[n..];
            }
            return total;
        }

        /// <summary>The file whose data comes next (its first byte's offset), skipping empty ones.</summary>
        private (long Start, IsoNode File)? NextData()
        {
            if (_input is not null) return (layout.Files[_file].Sector * RegionTable.SectorSize, layout.Files[_file]);
            while (_file + 1 < layout.Files.Count)
            {
                var f = layout.Files[_file + 1];
                var start = f.Sector * RegionTable.SectorSize;
                if (f.Size == 0) { _file++; continue; }
                if (_position < start) return (start, f);
                _file++;
                return (start, f);
            }
            return null;
        }

        private void FinishFile()
        {
            hashes[layout.Files[_file]] = _sha!.GetHashAndReset();
            _input!.Dispose();
            _input = null;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input?.Dispose();
                _sha?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
