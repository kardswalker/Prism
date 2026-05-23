using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

namespace PakTool.Core;

public sealed class PakArchiveSession : IDisposable
{
    private DefaultFileProvider? _provider;
    private Action<string>? _decodeLogger;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, IReadOnlyList<ArchiveEntryDto>> _listCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArchiveEntryDto> _entryCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<ArchiveEntryDto>>? _directoryIndex;

    public bool IsOpen => _provider is not null;

    public async Task<PakOpenResult> OpenAsync(PakOpenOptions options, CancellationToken cancellationToken = default)
    {
        if (options.PakPaths.Count == 0)
            throw new ArgumentException("At least one .pak path is required.", nameof(options));

        var timings = new List<OperationTimingDto>();
        var totalClock = System.Diagnostics.Stopwatch.StartNew();
        var stepClock = System.Diagnostics.Stopwatch.StartNew();

        DisposeProvider();
        ClearCaches();
        _decodeLogger = options.DecodeLogger;
        AddTiming(timings, "ResetSession", stepClock);

        stepClock.Restart();
        var firstPak = new FileInfo(options.PakPaths[0]);
        if (firstPak.Directory is null)
            throw new DirectoryNotFoundException("Could not resolve the pak directory.");

        var version = new VersionContainer(ParseGame(options.Game));
        var comparer = options.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var provider = new DefaultFileProvider(firstPak.Directory, SearchOption.TopDirectoryOnly, version, comparer)
        {
            SkipReferencedTextures = false,
            ReadShaderMaps = false,
            ReadNaniteData = false
        };
        AddTiming(timings, "CreateProvider", stepClock);

        stepClock.Restart();
        foreach (var pakPath in options.PakPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            provider.RegisterVfs(pakPath);
        }
        AddTiming(timings, "RegisterVfs", stepClock);

        stepClock.Restart();
        if (!string.IsNullOrWhiteSpace(options.UsmapPath))
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(options.UsmapPath);
        AddTiming(timings, "LoadUsmap", stepClock);

        stepClock.Restart();
        if (!string.IsNullOrWhiteSpace(options.AesKeyHex))
            await provider.SubmitKeyAsync(new FGuid(), new FAesKey(NormalizeAesKey(options.AesKeyHex))).ConfigureAwait(false);
        AddTiming(timings, "SubmitAes", stepClock);

        stepClock.Restart();
        var mountedByScan = await provider.MountAsync().ConfigureAwait(false);
        AddTiming(timings, "Mount", stepClock);

        stepClock.Restart();
        provider.PostMount();
        AddTiming(timings, "PostMount", stepClock);

        _provider = provider;
        stepClock.Restart();
        var mountedArchives = provider.MountedVfs.Select(vfs => vfs.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        AddTiming(timings, "MountedArchives", stepClock);
        AddTiming(timings, "OpenTotal", totalClock);

        return new PakOpenResult(
            mountedArchives.Length == 0 ? mountedByScan : mountedArchives.Length,
            provider.Files.Count,
            provider.RequiredKeys.Count,
            mountedArchives,
            timings);
    }

    public Task LoadUsmapAsync(string usmapPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ArchiveEntryDto>> ListAsync(string? folder = null, bool recursive = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedFolder = NormalizeFolder(folder);
        if (!recursive)
        {
            lock (_cacheLock)
            {
                if (_directoryIndex?.TryGetValue(normalizedFolder, out var indexedEntries) == true)
                    return Task.FromResult(indexedEntries);

                if (_listCache.TryGetValue(normalizedFolder, out var cachedEntries))
                    return Task.FromResult(cachedEntries);
            }
        }

        var entries = recursive
            ? ListRecursive(normalizedFolder)
            : ListImmediate(normalizedFolder);

        var sortedEntries = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!recursive)
        {
            lock (_cacheLock)
            {
                _listCache[normalizedFolder] = sortedEntries;
            }
        }

        return Task.FromResult<IReadOnlyList<ArchiveEntryDto>>(sortedEntries);
    }

    public Task<DirectoryIndexResult> BuildDirectoryIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var folderMap = new Dictionary<string, Dictionary<string, ArchiveEntryDto>>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new(StringComparer.OrdinalIgnoreCase)
        };
        var entryCount = 0;

        foreach (var file in Provider.Files.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddDirectoryChain(folderMap, file.Path);

            if (ShouldHidePackagePayload(file))
                continue;

            var folder = GetParentFolder(file.Path);
            if (!folderMap.TryGetValue(folder, out var entries))
                folderMap[folder] = entries = new Dictionary<string, ArchiveEntryDto>(StringComparer.OrdinalIgnoreCase);

            var entry = ToAssetAwareDto(file);
            if (entries.TryAdd(entry.FullPath, entry))
                entryCount++;
        }

        var finalized = folderMap.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ArchiveEntryDto>) pair.Value.Values
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

        lock (_cacheLock)
        {
            _directoryIndex = finalized;
            _listCache.Clear();
        }

        return Task.FromResult(new DirectoryIndexResult(finalized.Count, entryCount));
    }

    public Task<IReadOnlyList<ArchiveEntryDto>> SearchAsync(string query, int limit = 250, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<ArchiveEntryDto>>([]);

        var results = Provider.Files.Values
            .Where(file => file.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, limit))
            .Select(ToAssetAwareDto)
            .DistinctBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ArchiveEntryDto>>(results);
    }

    public Task<AssetInfoDto> ReadAssetInfoAsync(string assetPath, int maxExports = 12, int maxPropertiesPerExport = 24, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = Provider;
        var fixedPath = provider.FixPath(assetPath);

        if (!provider.TryLoadPackage(fixedPath, out var package))
            throw new InvalidOperationException($"Could not load package: {assetPath}");

        var exports = package.GetExports()
            .Take(Math.Max(1, maxExports))
            .Select(export => new AssetExportDto(
                export.Name,
                export.ExportType,
                export.Properties.Count,
                export.Properties
                    .Take(Math.Max(1, maxPropertiesPerExport))
                    .Select(prop => new AssetPropertyDto(
                        prop.Name.Text,
                        prop.PropertyType.Text,
                        PreviewValue(prop.Tag?.GenericValue)))
                    .ToArray()))
            .ToArray();

        return Task.FromResult(new AssetInfoDto(fixedPath, package.NameMap.Length, package.ExportMapLength, exports));
    }

    public async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await ReadGameFileAsync(path).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> ReadRelatedRawFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = Provider;
        var fixedPath = provider.FixPath(path);

        if (!provider.TryGetGameFile(fixedPath, out var file))
            throw new FileNotFoundException("The archive entry was not found.", fixedPath);

        var output = new Dictionary<string, byte[]>(provider.PathComparer);
        foreach (var related in GetRelatedFiles(provider, file))
        {
            cancellationToken.ThrowIfCancellationRequested();
            output[related.Path] = await related.ReadAsync().ConfigureAwait(false);
        }

        return output;
    }

    public Task<TexturePreviewDto?> TryReadTexturePreviewAsync(string assetPath, int maxMipSize = 1024, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = Provider;
            var fixedPath = provider.FixPath(assetPath);
            LogDecode($"Preview requested: asset={assetPath}, fixed={fixedPath}, maxMipSize={maxMipSize}, platform={provider.Versions.Platform}");

            try
            {
                if (!TryResolveGameFile(provider, fixedPath, out var gameFile))
                {
                    LogDecode($"GameFile not found for preview path: {fixedPath}");
                    LogNearbyFiles(provider, fixedPath);
                    return null;
                }

                LogDecode($"GameFile resolved: path={gameFile.Path}, name={gameFile.Name}, size={gameFile.Size}, type={gameFile.GetType().Name}");

                IPackage package;
                try
                {
                    package = provider.LoadPackage(gameFile);
                }
                catch (Exception ex)
                {
                    LogDecode($"Package load failed: file={gameFile.Path}, error={ex.GetType().Name}: {ex.Message}");
                    throw;
                }

                LogDecode($"Package loaded: {fixedPath}, exports={package.ExportMapLength}, names={package.NameMap.Length}");

                var sawTexture = false;
                string? failedTexture = null;
                Exception? decodeError = null;

                var exportIndex = 0;
                foreach (var export in package.ExportsLazy)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    exportIndex++;

                    CUE4Parse.UE4.Assets.Exports.UObject? value;
                    UTexture? texture;
                    try
                    {
                        value = export.Value;
                        texture = value as UTexture;
                    }
                    catch (Exception ex) when (IsMissingMappingsError(ex))
                    {
                        LogDecode($"Export #{exportIndex} failed due to missing mappings: {ex.Message}");
                        throw new InvalidOperationException("This asset uses unversioned properties. Import the matching .usmap mapping file, then open or preview it again.", ex);
                    }
                    catch (Exception ex)
                    {
                        LogDecode($"Export #{exportIndex} skipped: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    if (texture is null)
                    {
                        LogDecode($"Export #{exportIndex} is not texture: type={value.ExportType}, name={value.Name}");
                        continue;
                    }

                    sawTexture = true;
                    LogDecode($"Texture export found: type={texture.ExportType}, name={texture.Name}, format={texture.Format}");
                    if (!TryEncodeTexturePreview(texture, maxMipSize, provider.Versions.Platform, out var preview, out var error))
                    {
                        failedTexture = $"{texture.ExportType} {texture.Name} ({texture.Format})";
                        decodeError = error;
                        LogDecode($"Texture decode failed: {failedTexture}, error={error?.GetType().Name}: {error?.Message ?? "decoder returned no bitmap"}");
                        continue;
                    }

                    LogDecode($"Texture decode succeeded: {preview.Name}, {preview.Width}x{preview.Height}, png={preview.PngData.Length} bytes");
                    return new TexturePreviewDto(
                        fixedPath,
                        preview.Name,
                        preview.Width,
                        preview.Height,
                        preview.PngData);
                }

                if (sawTexture)
                {
                    var reason = decodeError is null ? "The decoder returned no bitmap." : decodeError.Message;
                    LogDecode($"Preview failed after seeing texture: texture={failedTexture}, reason={reason}");
                    throw new InvalidOperationException($"Texture found but could not be decoded{(failedTexture is null ? string.Empty : $" for {failedTexture}")}: {reason}", decodeError);
                }

                LogDecode($"No UTexture export found: {fixedPath}");
                return null;
            }
            catch (Exception ex) when (IsMissingMappingsError(ex))
            {
                LogDecode($"Preview failed due to missing mappings: {ex.Message}");
                throw new InvalidOperationException("This asset uses unversioned properties. Import the matching .usmap mapping file, then open or preview it again.", ex);
            }
        }, cancellationToken);
    }

    public async Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(request.OutputDirectory);

        var files = ResolveExportFiles(request.EntryPaths, request.IncludePackagePayloads);
        var errors = new List<string>();
        var completed = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress(completed, files.Count, file.Path));

            try
            {
                var outputPath = BuildOutputPath(request.OutputDirectory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var data = await file.ReadAsync().ConfigureAwait(false);
                await File.WriteAllBytesAsync(outputPath, data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"{file.Path}: {ex.Message}");
            }

            completed++;
        }

        progress?.Report(new ExportProgress(completed, files.Count, string.Empty));
        return new ExportResult(files.Count - errors.Count, errors.Count, errors);
    }

    public void Dispose()
    {
        DisposeProvider();
    }

    private DefaultFileProvider Provider => _provider ?? throw new InvalidOperationException("No pak session is open.");

    private IReadOnlyList<GameFile> ResolveExportFiles(IReadOnlyList<string> entryPaths, bool includePackagePayloads)
    {
        var provider = Provider;
        var results = new Dictionary<string, GameFile>(provider.PathComparer);

        foreach (var path in entryPaths)
        {
            var normalized = provider.FixPath(path).TrimStart('/');
            var isFolder = normalized.EndsWith('/');

            foreach (var file in provider.Files.Values)
            {
                if (isFolder)
                {
                    if (!file.Path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (!file.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results[file.Path] = file;

                if (includePackagePayloads && file.IsUePackage)
                    AddPackagePayloads(provider, file, results);
            }
        }

        return results.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddPackagePayloads(DefaultFileProvider provider, GameFile file, IDictionary<string, GameFile> results)
    {
        foreach (var extension in GameFile.UePackagePayloadExtensions)
        {
            var payloadPath = $"{file.PathWithoutExtension}.{extension}";
            if (provider.TryGetGameFile(payloadPath, out var payload))
                results[payload.Path] = payload;
        }
    }

    private IReadOnlyList<ArchiveEntryDto> ListImmediate(string folder)
    {
        var directories = new Dictionary<string, ArchiveEntryDto>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ArchiveEntryDto>();

        foreach (var file in Provider.Files.Values)
        {
            if (!file.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = file.Path[folder.Length..];
            if (rest.Length == 0)
                continue;

            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                var dirName = rest[..slash];
                var fullPath = folder + dirName + "/";
                directories.TryAdd(fullPath, new ArchiveEntryDto(fullPath, dirName, true, 0, string.Empty, false, string.Empty));
            }
            else
            {
                if (!ShouldHidePackagePayload(file))
                    files.Add(ToAssetAwareDto(file));
            }
        }

        return directories.Values.Concat(files).ToArray();
    }

    private IReadOnlyList<ArchiveEntryDto> ListRecursive(string folder)
    {
        return Provider.Files.Values
            .Where(file => file.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Where(file => !ShouldHidePackagePayload(file))
            .Select(ToAssetAwareDto)
            .ToArray();
    }

    private static ArchiveEntryDto ToDto(GameFile file)
    {
        return new ArchiveEntryDto(
            file.Path,
            file.Name,
            false,
            file.Size,
            file.Extension,
            file.IsEncrypted,
            file.CompressionMethod.ToString(),
            file.IsUePackage,
            [file.Path]);
    }

    private ArchiveEntryDto ToAssetAwareDto(GameFile file)
    {
        if (file.IsUePackagePayload && TryGetOwningPackage(Provider, file, out var owner))
            return ToAssetAwareDto(owner);

        lock (_cacheLock)
        {
            if (_entryCache.TryGetValue(file.Path, out var cachedEntry))
                return cachedEntry;
        }

        ArchiveEntryDto entry;
        if (!file.IsUePackage)
        {
            entry = ToDto(file);
        }
        else
        {
            var related = GetRelatedFiles(Provider, file).ToArray();
            entry = new ArchiveEntryDto(
                file.Path,
                file.Name,
                false,
                related.Sum(relatedFile => relatedFile.Size),
                file.Extension,
                related.Any(relatedFile => relatedFile.IsEncrypted),
                string.Join(", ", related.Select(relatedFile => relatedFile.CompressionMethod.ToString()).Distinct(StringComparer.OrdinalIgnoreCase)),
                true,
                related.Select(relatedFile => relatedFile.Path).ToArray());
        }

        lock (_cacheLock)
        {
            _entryCache[file.Path] = entry;
        }

        return entry;
    }

    private bool ShouldHidePackagePayload(GameFile file)
    {
        if (!file.IsUePackagePayload)
            return false;

        return TryGetOwningPackage(Provider, file, out _);
    }

    private static bool TryGetOwningPackage(DefaultFileProvider provider, GameFile file, out GameFile owner)
    {
        if (provider.TryGetGameFile($"{file.PathWithoutExtension}.uasset", out owner!) ||
            provider.TryGetGameFile($"{file.PathWithoutExtension}.umap", out owner!))
        {
            return true;
        }

        owner = null!;
        return false;
    }

    private static IReadOnlyList<GameFile> GetRelatedFiles(DefaultFileProvider provider, GameFile file)
    {
        if (!file.IsUePackage)
            return [file];

        var results = new Dictionary<string, GameFile>(provider.PathComparer)
        {
            [file.Path] = file
        };
        AddPackagePayloads(provider, file, results);

        return results.Values.OrderBy(related => PackagePartOrder(related.Extension)).ToArray();
    }

    private static int PackagePartOrder(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            "uasset" or "umap" => 0,
            "uexp" => 1,
            "ubulk" => 2,
            "uptnl" => 3,
            _ => 4
        };
    }

    private static void AddDirectoryChain(
        IDictionary<string, Dictionary<string, ArchiveEntryDto>> folderMap,
        string path)
    {
        var folder = string.Empty;
        var start = 0;

        while (true)
        {
            var slash = path.IndexOf('/', start);
            if (slash < 0)
                return;

            var dirName = path[start..slash];
            var fullPath = path[..(slash + 1)];

            if (!folderMap.TryGetValue(folder, out var entries))
                folderMap[folder] = entries = new Dictionary<string, ArchiveEntryDto>(StringComparer.OrdinalIgnoreCase);

            entries.TryAdd(fullPath, new ArchiveEntryDto(fullPath, dirName, true, 0, string.Empty, false, string.Empty));

            if (!folderMap.ContainsKey(fullPath))
                folderMap[fullPath] = new Dictionary<string, ArchiveEntryDto>(StringComparer.OrdinalIgnoreCase);

            folder = fullPath;
            start = slash + 1;
        }
    }

    private static string GetParentFolder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..(slash + 1)];
    }

    private static void AddTiming(ICollection<OperationTimingDto> timings, string name, System.Diagnostics.Stopwatch stopwatch)
    {
        stopwatch.Stop();
        timings.Add(new OperationTimingDto(name, stopwatch.ElapsedMilliseconds));
    }

    private void ClearCaches()
    {
        lock (_cacheLock)
        {
            _listCache.Clear();
            _entryCache.Clear();
            _directoryIndex = null;
        }
    }

    private async Task<byte[]> ReadGameFileAsync(string path)
    {
        var provider = Provider;
        var fixedPath = provider.FixPath(path);

        if (!provider.TryGetGameFile(fixedPath, out var file))
            throw new FileNotFoundException("The archive entry was not found.", fixedPath);

        return await file.ReadAsync().ConfigureAwait(false);
    }

    private static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder is "/")
            return string.Empty;

        var normalized = folder.Replace('\\', '/').TrimStart('/');
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static string NormalizeAesKey(string key)
    {
        var trimmed = key.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed : "0x" + trimmed;
    }

    private static EGame ParseGame(string game)
    {
        return Enum.TryParse<EGame>(game, true, out var parsed) ? parsed : EGame.GAME_UE5_6;
    }

    private static string BuildOutputPath(string outputDirectory, string archivePath)
    {
        var parts = archivePath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizePathPart);

        return Path.Combine([outputDirectory, .. parts]);
    }

    private static string SanitizePathPart(string part)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            part = part.Replace(invalid, '_');
        return part;
    }

    private static string? PreviewValue(object? value)
    {
        if (value is null)
            return null;

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return text.Length <= 160 ? text : text[..160] + "...";
    }

    private static bool TryEncodeTexturePreview(
        UTexture texture,
        int maxMipSize,
        ETexturePlatform platform,
        out EncodedTexturePreview preview,
        out Exception? error)
    {
        preview = default;
        error = null;

        try
        {
            CTexture? bitmap;
            var textureName = texture.Name;

            if (texture is UTexture2DArray textureArray)
            {
                bitmap = textureArray.DecodeTextureArray(platform)?.FirstOrDefault();
                textureName += "_0";
            }
            else
            {
                bitmap = texture.Decode(maxMipSize, platform);
                if (bitmap is not null && texture is UTextureCube)
                    bitmap = bitmap.ToPanorama();
            }

            if (bitmap is null)
                return false;

            var pngData = bitmap.Encode(ETextureFormat.Png, false, out _);
            preview = new EncodedTexturePreview(textureName, bitmap.Width, bitmap.Height, pngData);
            return pngData.Length > 0;
        }
        catch (Exception ex) when (IsMissingMappingsError(ex))
        {
            throw new InvalidOperationException("This texture requires the matching .usmap mapping file.", ex);
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private readonly record struct EncodedTexturePreview(string Name, int Width, int Height, byte[] PngData);

    private static bool IsMissingMappingsError(Exception ex)
    {
        return ex.Message.Contains("mapping file is missing", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("unversioned properties", StringComparison.OrdinalIgnoreCase) ||
               (ex.InnerException is not null && IsMissingMappingsError(ex.InnerException));
    }

    private static bool TryResolveGameFile(DefaultFileProvider provider, string path, out GameFile file)
    {
        if (provider.TryGetGameFile(path, out file!))
            return true;

        var dot = path.LastIndexOf('.');
        var noExtension = dot < 0 ? path : path[..dot];
        if (!string.Equals(noExtension, path, StringComparison.Ordinal) &&
            provider.TryGetGameFile(noExtension, out file!))
        {
            return true;
        }

        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (!string.Equals(normalized, path, StringComparison.Ordinal) &&
            provider.TryGetGameFile(normalized, out file!))
        {
            return true;
        }

        file = null!;
        return false;
    }

    private void LogNearbyFiles(DefaultFileProvider provider, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var nearby = provider.Files.Values
            .Where(file => file.Path.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .Select(file => file.Path)
            .ToArray();

        LogDecode(nearby.Length == 0
            ? $"No nearby files found for name '{name}'."
            : $"Nearby files for '{name}': {string.Join(" | ", nearby)}");
    }

    private static byte[] ConvertToRgba8888(CTexture texture)
    {
        var pixelCount = checked(texture.Width * texture.Height);
        var output = new byte[checked(pixelCount * 4)];
        var input = texture.Data;

        switch (texture.PixelFormat)
        {
            case EPixelFormat.PF_R8G8B8A8:
                Buffer.BlockCopy(input, 0, output, 0, Math.Min(input.Length, output.Length));
                return output;

            case EPixelFormat.PF_B8G8R8A8:
            {
                for (var i = 0; i < pixelCount; i++)
                {
                    var src = i * 4;
                    output[src] = input[src + 2];
                    output[src + 1] = input[src + 1];
                    output[src + 2] = input[src];
                    output[src + 3] = input[src + 3];
                }
                return output;
            }

            case EPixelFormat.PF_A8R8G8B8:
            {
                for (var i = 0; i < pixelCount; i++)
                {
                    var src = i * 4;
                    output[src] = input[src + 1];
                    output[src + 1] = input[src + 2];
                    output[src + 2] = input[src + 3];
                    output[src + 3] = input[src];
                }
                return output;
            }

            case EPixelFormat.PF_G8:
            case EPixelFormat.PF_R8:
            {
                for (var i = 0; i < pixelCount; i++)
                {
                    var gray = input[i];
                    var dst = i * 4;
                    output[dst] = gray;
                    output[dst + 1] = gray;
                    output[dst + 2] = gray;
                    output[dst + 3] = byte.MaxValue;
                }
                return output;
            }

            default:
                throw new NotSupportedException($"Texture preview does not support decoded pixel format {texture.PixelFormat} yet.");
        }
    }

    private void DisposeProvider()
    {
        _provider?.Dispose();
        _provider = null;
        ClearCaches();
    }

    private void LogDecode(string message)
    {
        _decodeLogger?.Invoke(message);
    }
}
