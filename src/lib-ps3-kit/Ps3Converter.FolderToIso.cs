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
        var layout = Iso9660Writer.Build(root, titleId, options.MaxExtentSize, options.MadeAt ?? DateTime.UtcNow);
        PrepareOutput(final, options.Overwrite, isFolder: false);
        var partial = final + ".partial";
        var total = layout.TotalSectors * RegionTable.SectorSize;
        try
        {
            // Empty files have no data to read; their hash is the empty one.
            var hashes = layout.Files.Where(f => f.Size == 0).ToDictionary(f => f, _ => SHA1.HashData(ReadOnlySpan<byte>.Empty));
            if (File.Exists(partial)) File.Delete(partial);
            using (var image = new FolderImageStream(layout, Math.Max(1, options.MaxParallelFiles), options.ChunkSize))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            using (var outFile = CreateOutput(partial, total))
            {
                long done = 0, offset = 0;
                // Each chunk's file segments, taken on the reading thread once the chunk before is done with, so the
                // consumer (started after) sees them. The files are hashed there, while the chunk is written.
                List<FileSegment> segments = [];
                Pump(image, new byte[options.ChunkSize], new byte[options.ChunkSize], (_, _) => segments = image.TakeSegments(),
                    (chunk, n) =>
                    {
                        var (these, at) = (segments, offset);
                        offset += n;
                        var write = Task.Run(() => outFile.Write(chunk, 0, n));
                        try
                        {
                            foreach (var s in these)
                            {
                                sha.AppendData(chunk, (int)(s.At - at), s.Length);
                                if (s.Last) hashes[layout.Files[s.File]] = sha.GetHashAndReset();
                            }
                        }
                        finally { write.GetAwaiter().GetResult(); }
                    },
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
}
