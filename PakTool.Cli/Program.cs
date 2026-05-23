using PakTool.Core;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();
var parsed = ParseArgs(args.Skip(1).ToArray());

try
{
    if (command == "loose-texture")
    {
        var uasset = Require(parsed, "uasset");
        var game = parsed.GetValueOrDefault("game", "GAME_UE5_6");
        var usmap = parsed.GetValueOrDefault("usmap");
        await InspectLooseTextureAsync(uasset, game, usmap);
        return 0;
    }

    using var session = new PakArchiveSession();
    var pak = Require(parsed, "pak");
    var open = await session.OpenAsync(new PakOpenOptions(
        [pak],
        parsed.GetValueOrDefault("aes"),
        parsed.GetValueOrDefault("usmap"),
        parsed.GetValueOrDefault("game", "GAME_UE5_6")));

    Console.WriteLine($"Mounted archives: {open.MountedArchiveCount}");
    Console.WriteLine($"Files: {open.FileCount}");
    Console.WriteLine($"Required keys: {open.RequiredKeyCount}");

    switch (command)
    {
        case "list":
        {
            var folder = parsed.GetValueOrDefault("folder");
            var entries = await session.ListAsync(folder, parsed.ContainsKey("recursive"));
            foreach (var entry in entries)
                Console.WriteLine($"{(entry.IsDirectory ? "DIR " : "FILE")} {entry.FullPath} {entry.Size}");
            break;
        }
        case "search":
        {
            var query = Require(parsed, "query");
            var entries = await session.SearchAsync(query);
            foreach (var entry in entries)
                Console.WriteLine($"{entry.FullPath} {entry.Size}");
            break;
        }
        case "export":
        {
            var entry = Require(parsed, "entry");
            var outDir = Require(parsed, "out");
            var result = await session.ExportAsync(
                new ExportRequest([entry], outDir),
                new Progress<ExportProgress>(p =>
                {
                    if (!string.IsNullOrEmpty(p.CurrentPath))
                        Console.WriteLine($"[{p.Completed}/{p.Total}] {p.CurrentPath}");
                }));
            Console.WriteLine($"Exported: {result.Succeeded}, failed: {result.Failed}");
            foreach (var error in result.Errors)
                Console.WriteLine(error);
            break;
        }
        case "asset":
        {
            var entry = Require(parsed, "entry");
            var info = await session.ReadAssetInfoAsync(entry);
            Console.WriteLine($"{info.Path}: {info.ExportCount} exports, {info.NameCount} names");
            foreach (var export in info.Exports)
                Console.WriteLine($"{export.Type} {export.Name} ({export.PropertyCount} properties)");
            break;
        }
        default:
            PrintUsage();
            return 1;
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
            continue;

        var key = arg[2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            result[key] = args[++i];
        else
            result[key] = "true";
    }
    return result;
}

static string Require(Dictionary<string, string> args, string key)
{
    if (args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        return value;

    throw new ArgumentException($"Missing --{key}.");
}

static void PrintUsage()
{
    Console.WriteLine("PakTool.Cli list --pak <file.pak> [--aes <hex>] [--usmap <file.usmap>] [--game GAME_UE5_6] [--folder Game/Content] [--recursive]");
    Console.WriteLine("PakTool.Cli search --pak <file.pak> --query <text> [--aes <hex>] [--usmap <file.usmap>]");
    Console.WriteLine("PakTool.Cli export --pak <file.pak> --entry <path-or-folder/> --out <directory> [--aes <hex>] [--usmap <file.usmap>]");
    Console.WriteLine("PakTool.Cli asset --pak <file.pak> --entry <package.uasset> [--aes <hex>] [--usmap <file.usmap>]");
    Console.WriteLine("PakTool.Cli loose-texture --uasset <file.uasset> [--game GAME_UE5_6] [--usmap <file.usmap>]");
}

static async Task InspectLooseTextureAsync(string uassetPath, string game, string? usmapPath)
{
    var fileInfo = new FileInfo(uassetPath);
    if (fileInfo.Directory?.Parent is null)
        throw new DirectoryNotFoundException("Could not resolve loose package directory.");

    var versions = new VersionContainer(Enum.TryParse<EGame>(game, true, out var parsed) ? parsed : EGame.GAME_UE5_6);
    using var provider = new DefaultFileProvider(fileInfo.Directory, SearchOption.TopDirectoryOnly, versions, StringComparer.OrdinalIgnoreCase)
    {
        SkipReferencedTextures = false,
        ReadShaderMaps = false,
        ReadNaniteData = false
    };

    if (!string.IsNullOrWhiteSpace(usmapPath))
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);

    provider.Initialize();

    var packagePath = fileInfo.Name;
    if (!provider.TryGetGameFile(packagePath, out var gameFile))
    {
        var matchName = fileInfo.Name;
        gameFile = provider.Files.Values.FirstOrDefault(file => file.Name.Equals(matchName, StringComparison.OrdinalIgnoreCase));
    }

    if (gameFile is null)
        throw new InvalidOperationException($"Could not find loose package: {packagePath}. Registered: {string.Join(", ", provider.Files.Keys.Take(8))}");

    Console.WriteLine("file=" + gameFile.Path);
    var package = provider.LoadPackage(gameFile);

    foreach (var export in package.ExportsLazy)
    {
        if (export.Value is not UTexture texture)
            continue;

        Console.WriteLine($"{texture.ExportType} {texture.Name} format={texture.Format} platform={provider.Versions.Platform}");
        try
        {
            var bitmap = texture.Decode(provider.Versions.Platform);
            if (bitmap is null)
            {
                Console.WriteLine("decode=null");
                continue;
            }

            var png = bitmap.Encode(ETextureFormat.Png, false, out var ext);
            Console.WriteLine($"decoded {bitmap.Width}x{bitmap.Height} {bitmap.PixelFormat}, encoded={png.Length} .{ext}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
        }
    }

    await Task.CompletedTask;
}
