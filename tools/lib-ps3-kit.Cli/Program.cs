using LibPs3Kit;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        ps3-kit: PlayStation 3 disc images

          ps3-kit info    <image.iso> [--key HEX | --dkey FILE]
          ps3-kit ls      <image.iso>
          ps3-kit decrypt <image.iso> <out.iso>   [--key HEX | --dkey FILE] [--overwrite] [--no-verify]
          ps3-kit jb      <image.iso> <out-folder> [--key HEX | --dkey FILE] [--no-update] [--parallel N] [--overwrite] [--no-verify]
          ps3-kit encrypt <decrypted.iso> <out.iso> (--key HEX | --dkey FILE) [--overwrite] [--no-verify]
          ps3-kit iso     <jb-folder> <out.iso>   [--no-update] [--overwrite] [--no-verify]

        Without --key or --dkey, <image>.dkey (or .key) beside the image is used when there is one.
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        try
        {
            var positional = args.Skip(1).Where((a, i) => !a.StartsWith("--") && !IsOptionValue(args, i + 1)).ToList();
            string Arg(int i) => i < positional.Count ? positional[i] : throw new ArgumentException("Missing argument.\n\n" + Usage);
            var options = new Ps3Options
            {
                Overwrite = args.Contains("--overwrite"),
                Verify = !args.Contains("--no-verify"),
                IncludeUpdate = !args.Contains("--no-update"),
                MaxParallelFiles = Value(args, "--parallel") is { } p ? int.Parse(p) : 1,
            };
            var progress = new Progress<OperationProgress>(p =>
            {
                if (p.Fraction is { } f) Console.Error.Write($"\r{p.Stage,-12} {f,7:P1}");
            });
            switch (args[0])
            {
                case "info": return Info(Arg(0), KeyFor(args, Arg(0)));
                case "ls": return List(Arg(0));
                case "decrypt": return Done(Ps3Converter.ToDecryptedIso(Arg(0), Arg(1), KeyFor(args, Arg(0)), options, progress));
                case "iso": return Done(Ps3Converter.FolderToIso(Arg(0), Arg(1), options, progress));
                case "jb": return Done(Ps3Converter.ToJbFolder(Arg(0), Arg(1), KeyFor(args, Arg(0)), options, progress));
                case "encrypt":
                    var key = KeyFor(args, Arg(0)) ?? throw new ArgumentException("encrypt needs --key or --dkey.");
                    return Done(Ps3Converter.ToEncryptedIso(Arg(0), Arg(1), key, options, progress));
                default:
                    Console.Error.WriteLine($"Unknown command {args[0]}.\n\n{Usage}");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is Ps3Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static bool IsOptionValue(string[] args, int i) => i > 0 && args[i - 1] is "--key" or "--dkey" or "--parallel";

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static DiscKey? KeyFor(string[] args, string image)
    {
        if (Value(args, "--key") is { } hex) return DiscKey.Parse(hex);
        if (Value(args, "--dkey") is { } file) return DiscKey.Load(file);
        return DiscKey.FindFileFor(image) is { } found ? DiscKey.Load(found) : null;
    }

    private static int Info(string path, DiscKey? key)
    {
        using var disc = Ps3Disc.Open(path);
        var sfo = disc.ReadParamSfo(key);
        Console.WriteLine($"Image:       {path}");
        Console.WriteLine($"Size:        {disc.Length:N0} bytes");
        Console.WriteLine($"Disc ID:     {disc.DiscTitleId ?? "-"}");
        Console.WriteLine($"Title ID:    {sfo?.TitleId ?? "-"}");
        Console.WriteLine($"Title:       {sfo?.Title ?? "-"}");
        Console.WriteLine($"Version:     {sfo?.AppVersion ?? sfo?.Version ?? "-"}");
        Console.WriteLine($"State:       {disc.State}");
        Console.WriteLine($"Filesystem:  {(disc.FileSystem.IsJoliet ? "ISO 9660 + Joliet" : "ISO 9660")} ({disc.FileSystem.VolumeId})");
        foreach (var r in disc.Regions.Regions)
            Console.WriteLine($"  {(r.Encrypted ? "encrypted" : "plain"),-9} sectors {r.FirstSector,9}-{r.LastSector,-9} ({r.SectorCount * 2048L:N0} bytes)");
        if (key is not null)
            Console.WriteLine($"Key:         {disc.KeyFits(key) switch { true => "fits", false => "DOESN'T FIT", null => "can't be checked" }}");
        return 0;
    }

    private static int List(string path)
    {
        using var disc = Ps3Disc.Open(path);
        foreach (var e in disc.FileSystem.Entries())
            Console.WriteLine(e.IsDirectory ? $"{"",14}  {e.Path}/" : $"{e.Size,14:N0}  {e.Path}");
        return 0;
    }

    private static int Done(Ps3Result result)
    {
        Console.Error.WriteLine();
        Console.WriteLine($"{result.Output}: {result.BytesWritten:N0} bytes, {result.FilesWritten} file(s){(result.Verification is { Success: true } ? ", verified" : "")}");
        if (result.TitleId is not null) Console.WriteLine($"{result.TitleId} {result.Title}");
        return 0;
    }
}
