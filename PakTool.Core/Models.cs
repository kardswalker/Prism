namespace PakTool.Core;

public sealed record PakOpenOptions(
    IReadOnlyList<string> PakPaths,
    string? AesKeyHex = null,
    string? UsmapPath = null,
    string Game = "GAME_UE5_6",
    bool CaseInsensitivePaths = true,
    Action<string>? DecodeLogger = null);

public sealed record PakOpenResult(
    int MountedArchiveCount,
    int FileCount,
    int RequiredKeyCount,
    IReadOnlyList<string> MountedArchives,
    IReadOnlyList<OperationTimingDto> Timings);

public sealed record OperationTimingDto(
    string Name,
    long Milliseconds);

public sealed record DirectoryIndexResult(
    int FolderCount,
    int EntryCount);

public sealed record ArchiveEntryDto(
    string FullPath,
    string Name,
    bool IsDirectory,
    long Size,
    string Extension,
    bool IsEncrypted,
    string CompressionMethod,
    bool IsAssetPackage = false,
    IReadOnlyList<string>? RelatedPaths = null);

public sealed record AssetPropertyDto(
    string Name,
    string Type,
    string? ValuePreview);

public sealed record AssetExportDto(
    string Name,
    string Type,
    int PropertyCount,
    IReadOnlyList<AssetPropertyDto> Properties);

public sealed record AssetInfoDto(
    string Path,
    int NameCount,
    int ExportCount,
    IReadOnlyList<AssetExportDto> Exports);

public sealed record TexturePreviewDto(
    string SourcePath,
    string TextureName,
    int Width,
    int Height,
    byte[] PngData);

public sealed record ExportRequest(
    IReadOnlyList<string> EntryPaths,
    string OutputDirectory,
    bool IncludePackagePayloads = true);

public sealed record ExportProgress(
    int Completed,
    int Total,
    string CurrentPath);

public sealed record ExportResult(
    int Succeeded,
    int Failed,
    IReadOnlyList<string> Errors);
