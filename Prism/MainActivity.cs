using System.Text.Json;
using Android.Content.PM;

namespace Prism;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    private const int PickPakRequest = 1001;
    private const int PickUsmapRequest = 1002;
    private const int PickRawExportTreeRequest = 1003;
    private const int PickPngExportTreeRequest = 1004;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object DiagnosticsLock = new();
    private static readonly List<string> Diagnostics = [];
    private static int _compressionWarmupStarted;

    private PakTool.Core.PakArchiveSession? _session;
    private string? _pakPath;
    private string? _pakDisplayName;
    private string? _usmapPath;
    private string? _usmapDisplayName;
    private string _currentFolder = string.Empty;
    private string _status = "Select a .pak file to begin.";
    private string? _selectedSummary;
    private string? _previewDataUrl;
    private string? _previewTitle;
    private string _oodleStatus = "Oodle native not checked.";
    private bool _busy;
    private bool _webReady;
    private int _openGeneration;
    private System.Threading.CancellationTokenSource? _indexCancellation;
    private global::Android.Net.Uri? _exportTreeUri;
    private PakTool.Core.ArchiveEntryDto? _selectedEntry;
    private IReadOnlyList<PakTool.Core.ArchiveEntryDto> _entries = [];
    private global::Android.Webkit.WebView? _webView;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        RequestWindowFeature(global::Android.Views.WindowFeatures.NoTitle);
        base.OnCreate(savedInstanceState);
        LogPerf("Prism OnCreate started.");
        ApplySystemBarsMode();
        CleanupImportCache();
        _oodleStatus = EnsureBundledOodleInitialized("startup");
        LogDecode("Oodle startup status: " + _oodleStatus);

        _webView = new global::Android.Webkit.WebView(this);
        ConfigureWebView(_webView);
        SetContentView(_webView);
        _webView.LoadDataWithBaseURL("https://paktool.local/", BuildHtml(), "text/html", "utf-8", null);
    }

    protected override void OnDestroy()
    {
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _session?.Dispose();
        DeleteCachedImport(_pakPath);
        DeleteCachedImport(_usmapPath);
        _webView?.Destroy();
        base.OnDestroy();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
            ApplySystemBarsMode();
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplySystemBarsMode();
        PushState();
    }

    public override void OnBackPressed()
    {
        if (!string.IsNullOrEmpty(_currentFolder))
        {
            _ = NavigateUpAsync();
            return;
        }

        base.OnBackPressed();
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, global::Android.Content.Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is null)
            return;

        try
        {
            if (requestCode is PickRawExportTreeRequest or PickPngExportTreeRequest)
            {
                _exportTreeUri = data.Data;
                TryPersistUriPermission(data);
                if (requestCode == PickPngExportTreeRequest)
                    await ExportSelectedPngAsync();
                else
                    await ExportSelectedRawAsync();
            }
            else if (requestCode == PickUsmapRequest)
            {
                var oldUsmapPath = _usmapPath;
                var newUsmapDisplayName = GetDisplayName(data.Data, "Mapping.usmap");
                var newUsmapPath = await CopyDocumentToCacheAsync(data.Data, ".usmap", "Importing usmap");
                _usmapDisplayName = newUsmapDisplayName;
                _usmapPath = newUsmapPath;
                if (_session?.IsOpen == true)
                {
                    await _session.LoadUsmapAsync(_usmapPath);
                    SetStatus("Usmap selected and loaded.");
                }
                else
                {
                    SetStatus("Usmap selected.");
                }

                DeleteCachedImport(oldUsmapPath);
            }
            else
            {
                var oldPakPath = _pakPath;
                var newPakDisplayName = GetDisplayName(data.Data, "Selected pak");
                var newPakPath = await CopyDocumentToCacheAsync(data.Data, ".pak", "Importing pak");
                CloseCurrentArchive();
                _pakDisplayName = newPakDisplayName;
                _pakPath = newPakPath;
                SetStatus("Pak selected.");
                DeleteCachedImport(oldPakPath);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void ConfigureWebView(global::Android.Webkit.WebView webView)
    {
        webView.SetBackgroundColor(global::Android.Graphics.Color.Rgb(247, 244, 235));
        webView.Settings.JavaScriptEnabled = true;
        webView.Settings.DomStorageEnabled = true;
        webView.Settings.UseWideViewPort = true;
        webView.Settings.LoadWithOverviewMode = true;
        webView.Settings.AllowFileAccess = false;
        webView.Settings.AllowContentAccess = false;
        webView.SetWebViewClient(new PakToolWebViewClient(this));
    }

    private void ApplySystemBarsMode()
    {
        var decorView = Window?.DecorView;
        if (decorView is null)
            return;

        var isLandscape = Resources?.Configuration?.Orientation == global::Android.Content.Res.Orientation.Landscape;
        decorView.SystemUiVisibility = isLandscape
            ? (global::Android.Views.StatusBarVisibility)(
                global::Android.Views.SystemUiFlags.ImmersiveSticky |
                global::Android.Views.SystemUiFlags.HideNavigation |
                global::Android.Views.SystemUiFlags.Fullscreen |
                global::Android.Views.SystemUiFlags.LayoutHideNavigation |
                global::Android.Views.SystemUiFlags.LayoutFullscreen |
                global::Android.Views.SystemUiFlags.LayoutStable)
            : global::Android.Views.StatusBarVisibility.Visible;
    }

    private bool HandleBridgeUri(global::Android.Net.Uri? uri)
    {
        if (uri?.Scheme is null || !uri.Scheme.Equals("paktool", StringComparison.OrdinalIgnoreCase))
            return false;

        var action = uri.Host ?? string.Empty;
        var payload = uri.GetQueryParameter("payload") ?? "{}";
        _ = HandleBridgeActionAsync(action, payload);
        return true;
    }

    private async Task HandleBridgeActionAsync(string action, string payloadJson)
    {
        try
        {
            switch (action)
            {
                case "pickPak":
                    PickFile(PickPakRequest);
                    break;
                case "pickUsmap":
                    PickFile(PickUsmapRequest);
                    break;
                case "openPak":
                    await OpenPakAsync(GetPayloadString(payloadJson, "aesKey"));
                    break;
                case "search":
                    await SearchAsync(GetPayloadString(payloadJson, "query"));
                    break;
                case "up":
                    await NavigateUpAsync();
                    break;
                case "entry":
                    var index = GetPayloadInt(payloadJson, "index");
                    if (index >= 0 && index < _entries.Count)
                        await OpenEntryAsync(_entries[index]);
                    break;
                case "exportRaw":
                    await ExportSelectedRawAsync();
                    break;
                case "exportPng":
                    await ExportSelectedPngAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void PickFile(int requestCode)
    {
        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionOpenDocument);
        intent.AddCategory(global::Android.Content.Intent.CategoryOpenable);
        intent.SetType("*/*");
        StartActivityForResult(intent, requestCode);
    }

    private void PickExportTree(int requestCode)
    {
        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionOpenDocumentTree);
        intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission);
        intent.AddFlags(global::Android.Content.ActivityFlags.GrantWriteUriPermission);
        intent.AddFlags(global::Android.Content.ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, requestCode);
    }

    private async Task OpenPakAsync(string? aesKey)
    {
        if (string.IsNullOrWhiteSpace(_pakPath))
        {
            SetStatus("Select a pak first.");
            return;
        }

        try
        {
            _indexCancellation?.Cancel();
            _indexCancellation?.Dispose();
            _indexCancellation = new System.Threading.CancellationTokenSource();
            var generation = ++_openGeneration;

            SetBusy(true, "Opening pak...");
            await LetWebViewRenderAsync();

            var session = Session;
            var options = new PakTool.Core.PakOpenOptions(
                [_pakPath],
                aesKey,
                _usmapPath,
                DecodeLogger: LogDecode);
            var openClock = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(async () => await session.OpenAsync(options));
            openClock.Stop();
            LogOpenTimings(result, openClock.Elapsed);

            SetStatus($"Mounted {result.MountedArchiveCount} archive(s), {result.FileCount} file(s) in {FormatDuration(openClock.Elapsed)}.");
            var listClock = System.Diagnostics.Stopwatch.StartNew();
            await NavigateToAsync(string.Empty, "Building file list...");
            listClock.Stop();
            LogPerf($"Root list: {FormatDuration(listClock.Elapsed)}, {_entries.Count} item(s)");
            StartDirectoryIndexBuild(session, generation, result.FileCount);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SearchAsync(string? query)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                await NavigateToAsync(_currentFolder);
                return;
            }

            SetBusy(true, "Searching...");
            await LetWebViewRenderAsync();

            var session = Session;
            var trimmedQuery = query.Trim();
            var results = await Task.Run(async () => await session.SearchAsync(trimmedQuery));
            _entries = results;
            ClearSelection(pushState: false);
            SetStatus($"{results.Count} result(s).");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task NavigateToAsync(string folder, string? busyStatus = null)
    {
        var ownsBusy = !_busy && busyStatus is not null;
        if (busyStatus is not null)
        {
            SetBusy(true, busyStatus);
            await LetWebViewRenderAsync();
        }

        try
        {
            _currentFolder = NormalizeFolder(folder);
            _entries = await Task.Run(async () => await Session.ListAsync(_currentFolder));
            ClearSelection(pushState: false);
            SetStatus($"{_entries.Count} item(s).");
        }
        finally
        {
            if (ownsBusy)
                SetBusy(false);
        }
    }

    private async Task NavigateUpAsync()
    {
        if (string.IsNullOrEmpty(_currentFolder))
            return;

        var trimmed = _currentFolder.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        await NavigateToAsync(slash < 0 ? string.Empty : trimmed[..(slash + 1)], "Loading folder...");
    }

    private async Task OpenEntryAsync(PakTool.Core.ArchiveEntryDto entry)
    {
        if (entry.IsDirectory)
        {
            await NavigateToAsync(entry.FullPath, "Loading folder...");
            return;
        }

        await SelectEntryAsync(entry);
    }

    private async Task SelectEntryAsync(PakTool.Core.ArchiveEntryDto entry)
    {
        _selectedEntry = entry;
        LogDecode($"Entry selected: path={entry.FullPath}, ext={entry.Extension}, asset={entry.IsAssetPackage}, related={entry.RelatedPaths?.Count ?? 1}");
        var relatedCount = entry.RelatedPaths?.Count ?? 1;
        var packageSuffix = entry.IsAssetPackage && relatedCount > 1 ? $" / {relatedCount} raw files" : string.Empty;
        _selectedSummary = $"{entry.FullPath} ({entry.Size:n0} bytes{packageSuffix})";
        _previewDataUrl = null;
        _previewTitle = null;
        SetStatus("Selected file.");

        if (!entry.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) &&
            !entry.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            SetBusy(true, "Loading texture preview...");
            await LetWebViewRenderAsync();

            var session = Session;
            LogDecode($"Preview start: {entry.FullPath}");
            _oodleStatus = EnsureBundledOodleInitialized("preview");
            LogDecode($"Preview Oodle status: {_oodleStatus}; initialized={CUE4Parse.Compression.OodleHelper.Instance is not null}");
            var preview = await Task.Run(async () => await session.TryReadTexturePreviewAsync(entry.FullPath));
            if (preview is null)
            {
                LogDecode($"Preview returned null: {entry.FullPath}");
                _previewTitle = "No previewable texture found.";
                SetStatus("No previewable texture found.");
                return;
            }

            LogDecode($"Preview success: {preview.TextureName}, {preview.Width}x{preview.Height}, png={preview.PngData.Length} bytes");
            _previewDataUrl = EncodePreviewDataUrl(preview);
            _previewTitle = $"{preview.TextureName} ({preview.Width}x{preview.Height})";
            SetStatus(_previewTitle);
        }
        catch (Exception ex)
        {
            LogDecode($"Preview failed: {entry.FullPath}, {ex.GetType().Name}: {ex.Message}");
            _previewTitle = "Preview failed.";
            SetStatus("Preview failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExportSelectedRawAsync()
    {
        if (_selectedEntry is not { IsDirectory: false } entry)
        {
            SetStatus("Select a file first.");
            return;
        }

        if (_exportTreeUri is null)
        {
            PickExportTree(PickRawExportTreeRequest);
            return;
        }

        try
        {
            SetBusy(true, "Exporting raw files...");
            await LetWebViewRenderAsync();

            var session = Session;
            var files = await Task.Run(async () => await session.ReadRelatedRawFilesAsync(entry.FullPath));

            foreach (var (path, data) in files)
            {
                var outputUri = CreateDocument(_exportTreeUri, System.IO.Path.GetFileName(path), "application/octet-stream");
                await using var output = ContentResolver!.OpenOutputStream(outputUri, "wt")
                    ?? throw new InvalidOperationException("Could not open output document.");
                await output.WriteAsync(data);
            }

            SetStatus($"Exported {files.Count} raw file(s).");
        }
        catch (Exception ex)
        {
            SetStatus("Export failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExportSelectedPngAsync()
    {
        if (_selectedEntry is not { IsDirectory: false } entry)
        {
            SetStatus("Select a texture asset first.");
            return;
        }

        if (_exportTreeUri is null)
        {
            PickExportTree(PickPngExportTreeRequest);
            return;
        }

        try
        {
            SetBusy(true, "Decoding PNG...");
            await LetWebViewRenderAsync();

            var session = Session;
            LogDecode($"PNG export decode start: {entry.FullPath}");
            _oodleStatus = EnsureBundledOodleInitialized("png export");
            LogDecode($"PNG export Oodle status: {_oodleStatus}; initialized={CUE4Parse.Compression.OodleHelper.Instance is not null}");
            var preview = await Task.Run(async () => await session.TryReadTexturePreviewAsync(entry.FullPath, int.MaxValue));
            if (preview is null)
            {
                LogDecode($"PNG export decode returned null: {entry.FullPath}");
                SetStatus("No previewable texture found.");
                return;
            }

            SetBusy(true, "Encoding PNG...");
            await LetWebViewRenderAsync();

            var png = preview.PngData;
            LogDecode($"PNG export writing: {preview.TextureName}, {preview.Width}x{preview.Height}, png={png.Length} bytes");
            var fileName = System.IO.Path.GetFileNameWithoutExtension(entry.Name) + ".png";
            var outputUri = CreateDocument(_exportTreeUri, fileName, "image/png");
            await using var output = ContentResolver!.OpenOutputStream(outputUri, "wt")
                ?? throw new InvalidOperationException("Could not open output document.");
            await output.WriteAsync(png);

            SetStatus($"Exported {fileName} ({preview.Width}x{preview.Height}).");
        }
        catch (Exception ex)
        {
            LogDecode($"PNG export failed: {entry.FullPath}, {ex.GetType().Name}: {ex.Message}");
            SetStatus("PNG export failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<string> CopyDocumentToCacheAsync(global::Android.Net.Uri uri, string extension, string statusPrefix)
    {
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var outputPath = System.IO.Path.Combine(GetImportCacheDirectory(), fileName);

        SetBusy(true, statusPrefix + "...");
        await LetWebViewRenderAsync();

        try
        {
            var totalBytes = GetDocumentSize(uri);
            var copiedBytes = 0L;
            var buffer = new byte[1024 * 1024];
            var progressClock = System.Diagnostics.Stopwatch.StartNew();

            await using var input = ContentResolver!.OpenInputStream(uri) ?? throw new InvalidOperationException("Could not open selected document.");
            await using var output = File.Create(outputPath);

            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (read <= 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read));
                copiedBytes += read;

                if (progressClock.ElapsedMilliseconds < 250)
                    continue;

                progressClock.Restart();
                SetBusy(true, totalBytes > 0
                    ? $"{statusPrefix} {Math.Min(99, copiedBytes * 100 / totalBytes)}%"
                    : $"{statusPrefix} {FormatBytes(copiedBytes)}");
            }

            return outputPath;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string GetImportCacheDirectory()
    {
        var directory = System.IO.Path.Combine(CacheDir!.AbsolutePath, "prism-imports");
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    private void CloseCurrentArchive()
    {
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _indexCancellation = null;
        _session?.Dispose();
        _session = null;
        _currentFolder = string.Empty;
        _entries = [];
        ClearSelection(pushState: false);
    }

    private void CleanupImportCache(params string?[] keepPaths)
    {
        try
        {
            var keep = keepPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => System.IO.Path.GetFullPath(path!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            DeleteCachedImportsInDirectory(CacheDir!.AbsolutePath, keep, includeLegacyRootFiles: true);

            var importDirectory = GetImportCacheDirectory();
            DeleteCachedImportsInDirectory(importDirectory, keep, includeLegacyRootFiles: false);
        }
        catch
        {
            // Cache cleanup is best-effort; failing to delete should not block the app.
        }
    }

    private static void DeleteCachedImportsInDirectory(string directory, ISet<string> keep, bool includeLegacyRootFiles)
    {
        if (!System.IO.Directory.Exists(directory))
            return;

        var files = includeLegacyRootFiles
            ? System.IO.Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase))
            : System.IO.Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            var fullPath = System.IO.Path.GetFullPath(file);
            if (keep.Contains(fullPath))
                continue;

            TryDeleteFile(fullPath);
        }
    }

    private static void DeleteCachedImport(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The provider may still be releasing a handle; the next startup cleanup will try again.
        }
    }

    private long GetDocumentSize(global::Android.Net.Uri uri)
    {
        try
        {
            using var cursor = ContentResolver!.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
                return -1;

            var index = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.Size);
            return index >= 0 && !cursor.IsNull(index) ? cursor.GetLong(index) : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double) bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private void ClearSelection(bool pushState = true)
    {
        _selectedEntry = null;
        _selectedSummary = null;
        _previewDataUrl = null;
        _previewTitle = null;
        if (pushState)
            PushState();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (status is not null)
            _status = status;
        PushState();
    }

    private void SetStatus(string text)
    {
        _status = text;
        PushState();
    }

    private static async Task LetWebViewRenderAsync()
    {
        await Task.Yield();
        await Task.Delay(120);
    }

    private static void StartCompressionWarmup()
    {
        if (System.Threading.Interlocked.Exchange(ref _compressionWarmupStarted, 1) != 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var source = new byte[] { 80, 114, 105, 115, 109, 32, 90, 108, 105, 98, 32, 87, 97, 114, 109, 117, 112 };
                using var compressedStream = new MemoryStream();
                using (var zlib = new System.IO.Compression.ZLibStream(
                           compressedStream,
                           System.IO.Compression.CompressionLevel.Fastest,
                           leaveOpen: true))
                {
                    zlib.Write(source, 0, source.Length);
                }

                var compressed = compressedStream.ToArray();
                var output = new byte[source.Length];
                CUE4Parse.Compression.Compression.Decompress(
                    compressed,
                    0,
                    compressed.Length,
                    output,
                    0,
                    output.Length,
                    CUE4Parse.Compression.CompressionMethod.Zlib);

                LogPerf("Compression warmup completed.");
            }
            catch (Exception ex)
            {
                LogPerf("Compression warmup failed: " + ex.Message, global::Android.Util.LogPriority.Warn);
            }
        });
    }

    private string EnsureBundledOodleInitialized(string reason)
    {
        if (CUE4Parse.Compression.OodleHelper.Instance is not null)
        {
            var already = $"Oodle native already initialized ({reason}).";
            LogPerf(already);
            return already;
        }

        try
        {
            var nativeDir = ApplicationInfo?.NativeLibraryDir;
            LogPerf($"Oodle initialize check ({reason}). nativeDir={nativeDir ?? "<null>"}");
            if (!string.IsNullOrWhiteSpace(nativeDir))
            {
                LogOodleFiles(nativeDir);
                var oodlePath = Path.Combine(nativeDir, "liboodle-data-shared.so");
                if (File.Exists(oodlePath))
                {
                    if (TryInitializeOodleFromPath(oodlePath))
                    {
                        var loaded = "Oodle native initialized from bundled native library.";
                        LogDecode(loaded);
                        return loaded;
                    }

                    LogPerf("Bundled Oodle native file was found but could not be loaded: " + oodlePath, global::Android.Util.LogPriority.Warn);
                }
                else
                {
                    LogPerf("Bundled Oodle native file was not found at " + oodlePath, global::Android.Util.LogPriority.Warn);
                }
            }

            if (TryInitializeOodleByName("oodle-data-shared") ||
                TryInitializeOodleByName("liboodle-data-shared.so"))
            {
                var loaded = "Oodle native initialized by library name.";
                LogDecode(loaded);
                return loaded;
            }

            var unavailable = "Bundled Oodle native library is not available; Oodle-compressed assets cannot be decoded.";
            LogPerf(unavailable, global::Android.Util.LogPriority.Warn);
            return unavailable;
        }
        catch (Exception ex)
        {
            var failed = "Oodle native initialization failed: " + ex.Message;
            LogPerf(failed, global::Android.Util.LogPriority.Warn);
            LogDecode(failed + Environment.NewLine + ex);
            return failed;
        }
    }

    private static void LogOodleFiles(string nativeDir)
    {
        try
        {
            if (!Directory.Exists(nativeDir))
            {
                LogPerf("Native library directory does not exist: " + nativeDir, global::Android.Util.LogPriority.Warn);
                return;
            }

            var files = Directory.EnumerateFiles(nativeDir)
                .Select(Path.GetFileName)
                .Where(name => name?.Contains("oodle", StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            LogPerf(files.Length == 0
                ? "Native library directory has no Oodle files."
                : "Native Oodle files: " + string.Join(", ", files));
        }
        catch (Exception ex)
        {
            LogPerf("Native library directory scan failed: " + ex.Message, global::Android.Util.LogPriority.Warn);
        }
    }

    private static bool TryInitializeOodleFromPath(string oodlePath)
    {
        try
        {
            CUE4Parse.Compression.OodleHelper.Initialize(new OodleDotNet.Oodle(oodlePath));
            LogPerf("Oodle native initialized from " + oodlePath);
            return true;
        }
        catch (Exception ex)
        {
            LogPerf("Oodle native path load failed: " + ex.Message, global::Android.Util.LogPriority.Warn);
            LogDecode("Oodle native path load failed: " + ex);
            return false;
        }
    }

    private static bool TryInitializeOodleByName(string libraryName)
    {
        nint handle = 0;
        try
        {
            if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(
                    libraryName,
                    typeof(MainActivity).Assembly,
                    System.Runtime.InteropServices.DllImportSearchPath.AssemblyDirectory,
                    out handle) &&
                !System.Runtime.InteropServices.NativeLibrary.TryLoad(libraryName, out handle))
            {
                return false;
            }

            CUE4Parse.Compression.OodleHelper.Initialize(new OodleDotNet.Oodle(handle));
            LogPerf("Oodle native initialized by library name " + libraryName);
            return true;
        }
        catch (Exception ex)
        {
            if (handle != 0)
                System.Runtime.InteropServices.NativeLibrary.Free(handle);

            LogPerf($"Oodle native name load failed for {libraryName}: {ex.Message}", global::Android.Util.LogPriority.Warn);
            LogDecode($"Oodle native name load failed for {libraryName}: {ex}");
            return false;
        }
    }

    private void StartDirectoryIndexBuild(PakTool.Core.PakArchiveSession session, int generation, int fileCount)
    {
        var token = _indexCancellation?.Token ?? System.Threading.CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                SetStatusFromAnyThread($"Indexing {fileCount} file(s) in background...");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var result = await session.BuildDirectoryIndexAsync(token);
                clock.Stop();

                if (token.IsCancellationRequested || generation != _openGeneration)
                    return;

                LogPerf($"Directory index: {FormatDuration(clock.Elapsed)}, {result.FolderCount} folder(s), {result.EntryCount} visible item(s)");
                SetStatusFromAnyThread($"Indexed {result.FolderCount} folder(s) in {FormatDuration(clock.Elapsed)}.");
            }
            catch (OperationCanceledException)
            {
                // A newer pak was opened before this background index finished.
            }
            catch (Exception ex)
            {
                LogPerf("Directory index failed: " + ex.Message, global::Android.Util.LogPriority.Warn);
                if (generation == _openGeneration)
                    SetStatusFromAnyThread("Background index failed: " + ex.Message);
            }
        }, token);
    }

    private void SetStatusFromAnyThread(string text)
    {
        RunOnUiThread(() => SetStatus(text));
    }

    private static void LogOpenTimings(PakTool.Core.PakOpenResult result, TimeSpan wallClock)
    {
        var timings = result.Timings.Count == 0
            ? string.Empty
            : " [" + string.Join(", ", result.Timings.Select(timing => $"{timing.Name}={timing.Milliseconds}ms")) + "]";
        LogPerf($"Open pak wall={FormatDuration(wallClock)}, mounted={result.MountedArchiveCount}, files={result.FileCount}, requiredKeys={result.RequiredKeyCount}{timings}");
    }

    private static void LogPerf(string message, global::Android.Util.LogPriority priority = global::Android.Util.LogPriority.Info)
    {
        const string tag = "PrismPerf";
        AddDiagnostic("PERF", message);
        Console.WriteLine($"{tag}: {message}");
        Java.Lang.JavaSystem.Err.Println($"{tag}: {message}");
        switch (priority)
        {
            case global::Android.Util.LogPriority.Warn:
                global::Android.Util.Log.Warn(tag, message);
                break;
            case global::Android.Util.LogPriority.Error:
                global::Android.Util.Log.Error(tag, message);
                break;
            default:
                global::Android.Util.Log.Info(tag, message);
                break;
        }
    }

    private static void LogDecode(string message)
    {
        AddDiagnostic("DECODE", message);
        Console.WriteLine("PrismDecode: " + message);
        Java.Lang.JavaSystem.Err.Println("PrismDecode: " + message);
        global::Android.Util.Log.Info("PrismDecode", message);
    }

    private static void AddDiagnostic(string channel, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {channel}: {message}";
        lock (DiagnosticsLock)
        {
            Diagnostics.Add(line);
            if (Diagnostics.Count > 120)
                Diagnostics.RemoveRange(0, Diagnostics.Count - 120);
        }
    }

    private static string[] SnapshotDiagnostics()
    {
        lock (DiagnosticsLock)
        {
            return Diagnostics.ToArray();
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds:0.0}s"
            : $"{duration.TotalMilliseconds:0}ms";
    }

    private PakTool.Core.PakArchiveSession Session => _session ??= new PakTool.Core.PakArchiveSession();

    private void TryPersistUriPermission(global::Android.Content.Intent data)
    {
        try
        {
            var flags = data.Flags & (global::Android.Content.ActivityFlags.GrantReadUriPermission |
                                      global::Android.Content.ActivityFlags.GrantWriteUriPermission);
            ContentResolver!.TakePersistableUriPermission(data.Data!, flags);
        }
        catch
        {
            // Some file providers do not grant persistable permissions; exporting can still work for this session.
        }
    }

    private global::Android.Net.Uri CreateDocument(global::Android.Net.Uri treeUri, string fileName, string mimeType)
    {
        var treeDocumentId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
        var parentUri = global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeDocumentId)
            ?? throw new InvalidOperationException("Could not resolve the export directory.");
        var documentUri = global::Android.Provider.DocumentsContract.CreateDocument(
            ContentResolver!,
            parentUri,
            mimeType,
            fileName);

        return documentUri ?? throw new InvalidOperationException("Could not create output document.");
    }

    private string GetDisplayName(global::Android.Net.Uri uri, string fallback)
    {
        try
        {
            using var cursor = ContentResolver!.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
                return fallback;

            var index = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.DisplayName);
            return index >= 0 ? cursor.GetString(index) ?? fallback : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void PushState()
    {
        if (!_webReady || _webView is null)
            return;

        var stateJson = JsonSerializer.Serialize(CreateState(), JsonOptions);
        RunOnUiThread(() => _webView?.EvaluateJavascript($"window.PakToolUI.applyState({stateJson});", null));
    }

    private object CreateState()
    {
        return new
        {
            status = _status,
            busy = _busy,
            currentPath = string.IsNullOrEmpty(_currentFolder) ? "/" : "/" + _currentFolder,
            pakName = _pakDisplayName ?? "No pak selected",
            usmapName = _usmapDisplayName ?? "No usmap",
            selectedSummary = _selectedSummary,
            canExportRaw = _selectedEntry is { IsDirectory: false },
            canExportPng = _selectedEntry is { IsDirectory: false, IsAssetPackage: true },
            previewDataUrl = _previewDataUrl,
            previewTitle = _previewTitle,
            oodleStatus = _oodleStatus,
            diagnostics = SnapshotDiagnostics(),
            entries = _entries.Select((entry, index) => new
            {
                index,
                name = entry.Name,
                path = entry.FullPath,
                size = FormatSize(entry.Size),
                extension = entry.Extension,
                isDirectory = entry.IsDirectory,
                isAssetPackage = entry.IsAssetPackage,
                relatedCount = entry.RelatedPaths?.Count ?? 1
            }).ToArray()
        };
    }

    private static byte[] EncodePreviewPng(PakTool.Core.TexturePreviewDto preview)
    {
        return preview.PngData;
    }

    private static string EncodePreviewDataUrl(PakTool.Core.TexturePreviewDto preview)
    {
        return "data:image/png;base64," + Convert.ToBase64String(preview.PngData);
    }

    private static string NormalizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder == "/")
            return string.Empty;

        var normalized = folder.Replace('\\', '/').TrimStart('/');
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)size;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private static string? GetPayloadString(string payloadJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(name, out var property) ? property.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static int GetPayloadInt(string payloadJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string BuildHtml()
    {
        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0, user-scalable=no, viewport-fit=cover" />
  <title>Prism</title>
  <style>
    :root {
      --paper: #fff6e8;
      --panel: #fffaf1;
      --ink: #2c2117;
      --muted: #7d684f;
      --line: #efd7b2;
      --accent: #ff9f45;
      --accent-2: #ffd586;
      --accent-3: #ffe0a3;
      --accent-gradient: linear-gradient(112deg, #FFB772, #FFE0A3, #FFD586);
      --danger: #a43d22;
      --shadow: 0 18px 48px rgba(111, 74, 28, .16);
      --body-bg:
        radial-gradient(circle at top left, rgba(255, 183, 114, .52), transparent 30rem),
        radial-gradient(circle at bottom right, rgba(255, 213, 134, .42), transparent 28rem),
        linear-gradient(145deg, #fff6e8 0%, #fff1d8 100%);
      --panel-glass: rgba(255, 250, 241, .88);
      --panel-solid: rgba(255, 250, 241, .92);
      --soft-panel: rgba(255, 255, 255, .56);
      --hero-border: rgba(151, 98, 35, .12);
      --status-color: #4f2a05;
      --status-bg: linear-gradient(112deg, rgba(255, 183, 114, .92), rgba(255, 224, 163, .96), rgba(255, 213, 134, .92));
      --progress-bg: rgba(255, 159, 69, .16);
      --button-bg: #2c2117;
      --button-fg: #fffaf1;
      --button-secondary-bg: #f4dfbd;
      --button-accent-fg: #4a2705;
      --button-disabled-fg: #a8967c;
      --button-disabled-bg: #ead8bd;
      --input-bg: rgba(255, 255, 255, .72);
      --focus-border: rgba(255, 159, 69, .7);
      --focus-shadow: rgba(255, 183, 114, .22);
      --browser-shadow: 0 12px 34px rgba(111, 74, 28, .1);
      --row-selected: #fff0d1;
      --kind-color: #5a3007;
      --kind-bg: #ffe9bf;
      --kind-file-color: #7a6245;
      --kind-file-bg: #f4dfbd;
      --badge-bg: #f8e7c8;
      --details-bg: linear-gradient(180deg, rgba(255, 247, 232, .64), rgba(255, 250, 241, .96));
      --preview-border: #e2bd83;
      --preview-tile: rgba(111, 74, 28, .045);
      --preview-bg: #fff7e8;
      --spinner-track: rgba(255, 159, 69, .18);
      --toggle-bg: rgba(255, 255, 255, .48);
      font-family: "Noto Sans SC", "HarmonyOS Sans", "MiSans", "Avenir Next", sans-serif;
    }

    body.theme-mint {
      --paper: #f7f4eb;
      --panel: #fffdf7;
      --ink: #20231f;
      --muted: #6f7469;
      --line: #e3dece;
      --accent: #0f766e;
      --accent-2: #d8f36a;
      --accent-3: #9bd8c6;
      --accent-gradient: linear-gradient(112deg, #0f766e, #9bd8c6, #d8f36a);
      --danger: #8b2f1d;
      --shadow: 0 18px 48px rgba(39, 61, 44, .14);
      --body-bg:
        radial-gradient(circle at top left, rgba(216, 243, 106, .55), transparent 30rem),
        radial-gradient(circle at bottom right, rgba(15, 118, 110, .16), transparent 28rem),
        linear-gradient(145deg, #f7f4eb 0%, #edf4e8 100%);
      --panel-glass: rgba(255, 253, 247, .88);
      --panel-solid: rgba(255, 253, 247, .92);
      --soft-panel: rgba(255, 255, 255, .58);
      --hero-border: rgba(31, 63, 52, .11);
      --status-color: #073f39;
      --status-bg: linear-gradient(112deg, rgba(216, 243, 106, .92), rgba(155, 216, 198, .9), rgba(15, 118, 110, .18));
      --progress-bg: rgba(15, 118, 110, .13);
      --button-bg: #20231f;
      --button-fg: #fffdf7;
      --button-secondary-bg: #ebe5d4;
      --button-accent-fg: #17332e;
      --button-disabled-fg: #989b8d;
      --button-disabled-bg: #e5dfcd;
      --input-bg: rgba(255, 255, 255, .72);
      --focus-border: rgba(15, 118, 110, .62);
      --focus-shadow: rgba(216, 243, 106, .28);
      --browser-shadow: 0 12px 34px rgba(39, 61, 44, .09);
      --row-selected: #eef4df;
      --kind-color: #173f39;
      --kind-bg: #e7f2df;
      --kind-file-color: #73776d;
      --kind-file-bg: #ebe5d4;
      --badge-bg: #eef0de;
      --details-bg: linear-gradient(180deg, rgba(247, 244, 235, .72), rgba(255, 253, 247, .96));
      --preview-border: #cfc8b5;
      --preview-tile: rgba(39, 61, 44, .045);
      --preview-bg: #fbf8ed;
      --spinner-track: rgba(15, 118, 110, .16);
      --toggle-bg: rgba(255, 255, 255, .5);
    }

    * { box-sizing: border-box; min-width: 0; }

    html {
      width: 100%;
      height: 100%;
      overflow: hidden;
      font-size: clamp(13px, 2.8vw, 16px);
    }

    body {
      margin: 0;
      width: 100%;
      height: 100%;
      overflow: hidden;
      color: var(--ink);
      background: var(--body-bg);
    }

    button, input {
      max-width: 100%;
      font: inherit;
    }

    .shell {
      height: 100vh;
      height: 100dvh;
      padding: calc(clamp(10px, 3.6vw, 16px) + env(safe-area-inset-top)) clamp(10px, 3.6vw, 16px) calc(clamp(10px, 3.6vw, 18px) + env(safe-area-inset-bottom));
      display: flex;
      flex-direction: column;
      gap: clamp(8px, 2.4vw, 14px);
      overflow: hidden;
    }

    .hero {
      display: grid;
      flex: 0 0 auto;
      gap: clamp(8px, 2.4vw, 12px);
      padding: clamp(12px, 3.6vw, 18px);
      border: 1px solid var(--hero-border);
      border-radius: clamp(20px, 6vw, 28px);
      background: var(--panel-glass);
      box-shadow: var(--shadow);
      backdrop-filter: blur(18px);
      overflow: hidden;
    }

    .topline {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
    }

    .brand {
      display: grid;
      gap: 2px;
    }

    .brand h1 {
      margin: 0;
      font-size: clamp(21px, 5.6vw, 25px);
      letter-spacing: -.04em;
      line-height: 1;
    }

    .brand p, .file-meta, .muted {
      margin: 0;
      color: var(--muted);
      font-size: 12px;
    }

    .top-actions {
      min-width: 0;
      max-width: min(58%, 330px);
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: 8px;
    }

    .theme-toggle {
      flex: 0 0 auto;
      min-height: 34px;
      padding: 0 11px;
      border: 1px solid var(--line);
      border-radius: 999px;
      color: var(--ink);
      background: var(--toggle-bg);
      font-size: 12px;
      font-weight: 850;
      white-space: nowrap;
    }

    .status {
      min-width: 0;
      max-width: 100%;
      padding: 8px 10px;
      border-radius: 999px;
      color: var(--status-color);
      background: var(--status-bg);
      font-size: 12px;
      font-weight: 750;
      text-align: right;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    body.busy .status::before {
      content: "";
      display: inline-block;
      width: 7px;
      height: 7px;
      margin-right: 7px;
      border-radius: 999px;
      background: var(--accent);
      animation: pulse .9s infinite alternate;
    }

    @keyframes pulse { from { opacity: .35; transform: scale(.75); } to { opacity: 1; transform: scale(1); } }

    .progress {
      position: relative;
      height: 0;
      overflow: hidden;
      border-radius: 999px;
      background: var(--progress-bg);
      opacity: 0;
      transition: height .18s ease, opacity .18s ease;
    }

    body.busy .progress {
      height: 7px;
      opacity: 1;
    }

    .progress::after {
      content: "";
      position: absolute;
      inset: 0 auto 0 0;
      width: 42%;
      border-radius: inherit;
      background: var(--accent-gradient);
      animation: progress-slide 1.05s ease-in-out infinite;
    }

    @keyframes progress-slide {
      0% { transform: translateX(-110%); }
      55% { transform: translateX(70%); }
      100% { transform: translateX(245%); }
    }

    .chosen {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
      gap: 8px;
    }

    .chip {
      min-width: 0;
      padding: 10px 12px;
      border: 1px solid var(--line);
      border-radius: 18px;
      background: var(--soft-panel);
    }

    .chip strong {
      display: block;
      margin-bottom: 2px;
      font-size: 11px;
      color: var(--muted);
      text-transform: uppercase;
      letter-spacing: .09em;
    }

    .chip span {
      display: block;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 13px;
      font-weight: 750;
    }

    .grid {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: 8px;
    }

    .button {
      min-height: clamp(39px, 8vw, 43px);
      padding: 0 10px;
      border: 0;
      border-radius: 16px;
      background: var(--button-bg);
      color: var(--button-fg);
      font-weight: 800;
      letter-spacing: -.01em;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .button.secondary {
      color: var(--ink);
      background: var(--button-secondary-bg);
    }

    .button.accent {
      color: var(--button-accent-fg);
      background: var(--accent-gradient);
    }

    .button:disabled {
      color: var(--button-disabled-fg);
      background: var(--button-disabled-bg);
    }

    .searchbar {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(72px, 86px);
      gap: 8px;
    }

    input {
      min-width: 0;
      width: 100%;
      height: clamp(40px, 8vw, 44px);
      padding: 0 13px;
      border: 1px solid var(--line);
      border-radius: 16px;
      outline: none;
      color: var(--ink);
      background: var(--input-bg);
    }

    input:focus { border-color: var(--focus-border); box-shadow: 0 0 0 4px var(--focus-shadow); }

    .browser {
      flex: 1;
      min-height: 0;
      display: grid;
      grid-template-rows: auto minmax(0, 1fr) auto;
      overflow: hidden;
      border: 1px solid var(--hero-border);
      border-radius: 28px;
      background: var(--panel-solid);
      box-shadow: var(--browser-shadow);
    }

    .pathbar {
      display: grid;
      grid-template-columns: minmax(54px, 62px) minmax(0, 1fr);
      align-items: center;
      gap: 10px;
      padding: 12px;
      border-bottom: 1px solid var(--line);
    }

    .path {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 13px;
      font-weight: 800;
    }

    .list {
      overflow: auto;
      padding: 8px;
    }

    .row {
      width: 100%;
      display: grid;
      grid-template-columns: 44px minmax(0, 1fr) auto;
      gap: 10px;
      align-items: center;
      padding: 12px 10px;
      border: 0;
      border-radius: 18px;
      color: inherit;
      background: transparent;
      text-align: left;
    }

    .row:active, .row.selected { background: var(--row-selected); }

    .kind {
      width: 40px;
      height: 40px;
      display: grid;
      place-items: center;
      border-radius: 15px;
      color: var(--kind-color);
      background: var(--kind-bg);
      font-size: 10px;
      font-weight: 900;
      letter-spacing: .04em;
    }

    .kind.asset { background: var(--accent-gradient); }
    .kind.file { background: var(--kind-file-bg); color: var(--kind-file-color); }

    .file-title {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 14px;
      font-weight: 850;
      letter-spacing: -.015em;
    }

    .badge {
      padding: 6px 8px;
      border-radius: 999px;
      color: var(--muted);
      background: var(--badge-bg);
      font-size: 11px;
      font-weight: 800;
      white-space: nowrap;
    }

    .details {
      display: grid;
      gap: 12px;
      padding: 12px;
      border-top: 1px solid var(--line);
      background: var(--details-bg);
      overflow: hidden;
    }

    .selected {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 12px;
      color: var(--muted);
    }

    .preview {
      position: relative;
      min-height: clamp(112px, 26vh, 148px);
      max-height: 32vh;
      display: grid;
      place-items: center;
      overflow: hidden;
      border: 1px dashed var(--preview-border);
      border-radius: 20px;
      background:
        linear-gradient(45deg, var(--preview-tile) 25%, transparent 25%),
        linear-gradient(-45deg, var(--preview-tile) 25%, transparent 25%),
        var(--preview-bg);
      background-size: 18px 18px;
    }

    .preview-stage {
      position: absolute;
      inset: 0;
      display: grid;
      place-items: center;
      overflow: hidden;
      touch-action: none;
    }

    .preview-stage.dragging {
      cursor: grabbing;
    }

    .preview-image {
      max-width: 100%;
      max-height: min(230px, 30vh);
      object-fit: contain;
      image-rendering: auto;
      pointer-events: none;
      user-select: none;
      transform-origin: center;
      will-change: transform;
    }

    .preview-tools {
      position: absolute;
      top: 8px;
      right: 8px;
      z-index: 2;
      display: flex;
      gap: 6px;
      padding: 5px;
      border: 1px solid var(--line);
      border-radius: 999px;
      background: var(--panel-glass);
      box-shadow: 0 10px 24px rgba(80, 55, 24, .12);
      backdrop-filter: blur(12px);
    }

    .preview-tool {
      width: 31px;
      height: 31px;
      border: 0;
      border-radius: 999px;
      color: var(--ink);
      background: var(--button-secondary-bg);
      font-weight: 900;
      line-height: 1;
    }

    .preview-empty {
      padding: 18px;
      text-align: center;
      color: var(--muted);
      font-size: 13px;
    }

    .preview-loading {
      display: grid;
      place-items: center;
      gap: 10px;
      color: var(--muted);
      font-size: 13px;
      text-align: center;
    }

    .spinner {
      width: 34px;
      height: 34px;
      border: 4px solid var(--spinner-track);
      border-top-color: var(--accent);
      border-radius: 999px;
      animation: spin .78s linear infinite;
    }

    @keyframes spin { to { transform: rotate(360deg); } }

    .actions {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
      gap: 8px;
    }

    .diagnostics {
      overflow: hidden;
      border: 1px solid var(--line);
      border-radius: 18px;
      background: rgba(255, 255, 255, .42);
    }

    .diagnostics summary {
      padding: 10px 12px;
      color: var(--muted);
      font-size: 12px;
      font-weight: 900;
      letter-spacing: .02em;
      text-transform: uppercase;
    }

    .diagnostics-log {
      max-height: 160px;
      overflow: auto;
      padding: 0 12px 12px;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      color: var(--muted);
      font-family: "JetBrains Mono", "Cascadia Mono", monospace;
      font-size: 11px;
      line-height: 1.45;
    }

    .empty {
      padding: 42px 18px;
      color: var(--muted);
      text-align: center;
      font-size: 13px;
    }

    @media (max-width: 380px) {
      .brand p { display: none; }
      .top-actions { max-width: 64%; gap: 6px; }
      .theme-toggle { padding: 0 9px; }
      .chip { padding: 9px 10px; }
      .button { padding: 0 8px; }
      .row { grid-template-columns: 38px minmax(0, 1fr); }
      .kind { width: 36px; height: 36px; border-radius: 13px; }
      .badge { display: none; }
    }

    @media (orientation: landscape) and (min-width: 640px) {
      html { font-size: clamp(12px, 1.6vw, 15px); }

      .shell {
        display: grid;
        grid-template-rows: auto minmax(0, 1fr);
        gap: 10px;
        padding: calc(8px + env(safe-area-inset-top)) calc(12px + env(safe-area-inset-right)) calc(8px + env(safe-area-inset-bottom)) calc(12px + env(safe-area-inset-left));
      }

      .hero {
        grid-template-columns: minmax(150px, .78fr) minmax(230px, 1.12fr) minmax(260px, 1.28fr);
        align-items: center;
        gap: 8px 10px;
        padding: 10px;
        border-radius: 22px;
      }

      .topline {
        grid-column: 1;
        grid-row: 1;
      }

      .brand h1 { font-size: clamp(18px, 2.4vw, 22px); }
      .brand p { display: none; }

      .top-actions { max-width: 60%; }
      .status { padding: 7px 9px; }
      .theme-toggle { min-height: 32px; padding: 0 10px; }

      .progress {
        grid-column: 1 / -1;
        grid-row: 2;
      }

      .chosen {
        grid-column: 2;
        grid-row: 1;
      }

      #aesKey {
        grid-column: 3;
        grid-row: 1;
      }

      .grid {
        grid-column: 1;
        grid-row: 3;
      }

      .searchbar {
        grid-column: 2 / 4;
        grid-row: 3;
      }

      .button, input {
        min-height: 36px;
        height: 36px;
        border-radius: 14px;
      }

      .chip {
        padding: 7px 9px;
        border-radius: 15px;
      }

      .chip strong { font-size: 10px; }
      .chip span { font-size: 12px; }

      .browser {
        grid-template-columns: minmax(270px, .95fr) minmax(320px, 1.05fr);
        grid-template-rows: auto minmax(0, 1fr);
        border-radius: 22px;
      }

      .pathbar {
        grid-column: 1;
        grid-row: 1;
        padding: 10px;
      }

      .list {
        grid-column: 1;
        grid-row: 2;
      }

      .details {
        grid-column: 2;
        grid-row: 1 / 3;
        grid-template-rows: auto minmax(0, 1fr) auto;
        border-top: 0;
        border-left: 1px solid var(--line);
        padding: 10px;
      }

      .preview {
        min-height: 0;
        max-height: none;
        height: 100%;
      }

      .preview-image {
        max-height: 100%;
      }

      .row {
        grid-template-columns: 42px minmax(0, 1fr) auto;
        padding: 10px;
      }

      .kind {
        width: 38px;
        height: 38px;
      }
    }
  </style>
</head>
<body>
  <main class="shell">
    <section class="hero">
      <div class="topline">
        <div class="brand">
          <h1>Prism</h1>
          <p>UE pak browser for Android</p>
        </div>
        <div class="top-actions">
          <button id="themeToggle" class="theme-toggle" type="button" onclick="toggleTheme()">Warm</button>
          <div id="status" class="status">Ready</div>
        </div>
      </div>
      <div class="progress" aria-hidden="true"></div>

      <div class="chosen">
        <div class="chip"><strong>Pak</strong><span id="pakName">No pak selected</span></div>
        <div class="chip"><strong>Usmap</strong><span id="usmapName">No usmap</span></div>
      </div>

      <input id="aesKey" autocomplete="off" spellcheck="false" placeholder="AES key, optional" />

      <div class="grid">
        <button class="button secondary" onclick="native('pickPak')">Pak</button>
        <button class="button secondary" onclick="native('pickUsmap')">Usmap</button>
        <button class="button accent" onclick="openPak()">Open</button>
      </div>

      <div class="searchbar">
        <input id="search" autocomplete="off" spellcheck="false" placeholder="Search assets or files" />
        <button class="button secondary" onclick="search()">Search</button>
      </div>
    </section>

    <section class="browser">
      <div class="pathbar">
        <button class="button secondary" onclick="native('up')">Up</button>
        <div id="path" class="path">/</div>
      </div>

      <div id="list" class="list"></div>

      <div class="details">
        <div id="selected" class="selected">No file selected</div>
        <div id="preview" class="preview"><div class="preview-empty">Select a texture asset to preview it.</div></div>
        <div class="actions">
          <button id="exportRaw" class="button secondary" onclick="native('exportRaw')" disabled>Export Raw</button>
          <button id="exportPng" class="button" onclick="native('exportPng')" disabled>Export PNG</button>
        </div>
        <details class="diagnostics">
          <summary>Diagnostics</summary>
          <div id="diagnostics" class="diagnostics-log">No diagnostics yet.</div>
        </details>
      </div>
    </section>
  </main>

  <script>
    let state = {
      status: "Ready",
      busy: false,
      currentPath: "/",
      pakName: "No pak selected",
      usmapName: "No usmap",
      selectedSummary: null,
      canExportRaw: false,
      canExportPng: false,
      previewDataUrl: null,
      previewTitle: null,
      oodleStatus: "Oodle native not checked.",
      diagnostics: [],
      entries: []
    };

    const $ = id => document.getElementById(id);
    const themeLabels = { warm: "Warm", mint: "Mint" };
    let theme = localStorage.getItem("prism.theme") || "warm";
    let previewScale = 1;
    let previewPanX = 0;
    let previewPanY = 0;
    let previewPointerId = null;
    let previewLastX = 0;
    let previewLastY = 0;
    let lastPreviewDataUrl = null;

    function applyTheme() {
      if (!themeLabels[theme]) theme = "warm";
      document.body.classList.toggle("theme-warm", theme === "warm");
      document.body.classList.toggle("theme-mint", theme === "mint");
      const toggle = $("themeToggle");
      if (toggle) toggle.textContent = themeLabels[theme];
    }

    function toggleTheme() {
      theme = theme === "warm" ? "mint" : "warm";
      localStorage.setItem("prism.theme", theme);
      applyTheme();
    }

    function native(action, payload = {}) {
      const encoded = encodeURIComponent(JSON.stringify(payload));
      location.href = `paktool://${action}?payload=${encoded}&t=${Date.now()}`;
    }

    function openPak() {
      native("openPak", { aesKey: $("aesKey").value });
    }

    function search() {
      native("search", { query: $("search").value });
    }

    $("search").addEventListener("keydown", event => {
      if (event.key === "Enter") search();
    });

    window.PakToolUI = {
      applyState(next) {
        state = next;
        render();
      }
    };

    function render() {
      document.body.classList.toggle("busy", !!state.busy);
      $("status").textContent = state.status || "Ready";
      $("path").textContent = state.currentPath || "/";
      $("pakName").textContent = state.pakName || "No pak selected";
      $("usmapName").textContent = state.usmapName || "No usmap";
      $("selected").textContent = state.selectedSummary || "No file selected";
      $("exportRaw").disabled = !state.canExportRaw;
      $("exportPng").disabled = !state.canExportPng;
      renderList();
      renderPreview();
      renderDiagnostics();
    }

    function renderDiagnostics() {
      const diagnostics = $("diagnostics");
      if (!diagnostics) return;
      const lines = state.diagnostics || [];
      diagnostics.textContent = lines.length
        ? [`Oodle: ${state.oodleStatus || "unknown"}`, ...lines].join("\n")
        : `Oodle: ${state.oodleStatus || "unknown"}`;
      diagnostics.scrollTop = diagnostics.scrollHeight;
    }

    function renderList() {
      const list = $("list");
      list.replaceChildren();

      if (!state.entries || state.entries.length === 0) {
        const empty = document.createElement("div");
        empty.className = "empty";
        empty.textContent = "No items here yet.";
        list.appendChild(empty);
        return;
      }

      for (const entry of state.entries) {
        const row = document.createElement("button");
        row.className = "row";
        row.onclick = () => native("entry", { index: entry.index });

        const kind = document.createElement("div");
        kind.className = "kind";
        kind.textContent = entry.isDirectory ? "DIR" : entry.isAssetPackage ? "UE" : "FILE";
        if (entry.isAssetPackage) kind.classList.add("asset");
        if (!entry.isDirectory && !entry.isAssetPackage) kind.classList.add("file");

        const middle = document.createElement("div");
        const title = document.createElement("div");
        title.className = "file-title";
        title.textContent = entry.name;
        const meta = document.createElement("div");
        meta.className = "file-meta";
        meta.textContent = entry.isDirectory
          ? entry.path
          : `${entry.size}${entry.relatedCount > 1 ? " / " + entry.relatedCount + " parts" : ""}`;
        middle.append(title, meta);

        const badge = document.createElement("div");
        badge.className = "badge";
        badge.textContent = entry.isDirectory ? "Folder" : entry.extension || "raw";

        row.append(kind, middle, badge);
        list.appendChild(row);
      }
    }

    function renderPreview() {
      const preview = $("preview");
      preview.replaceChildren();

      if (state.previewDataUrl !== lastPreviewDataUrl) {
        lastPreviewDataUrl = state.previewDataUrl;
        resetPreviewTransform(false);
      }

      if (state.busy && /preview|Decoding|Encoding/i.test(state.status || "")) {
        const loading = document.createElement("div");
        loading.className = "preview-loading";
        const spinner = document.createElement("div");
        spinner.className = "spinner";
        const label = document.createElement("div");
        label.textContent = state.status || "Loading...";
        loading.append(spinner, label);
        preview.appendChild(loading);
        return;
      }

      if (state.previewDataUrl) {
        const stage = document.createElement("div");
        stage.className = "preview-stage";

        const img = document.createElement("img");
        img.className = "preview-image";
        img.id = "previewImage";
        img.src = state.previewDataUrl;
        img.alt = state.previewTitle || "Texture preview";

        const tools = document.createElement("div");
        tools.className = "preview-tools";
        tools.innerHTML = `
          <button class="preview-tool" type="button" aria-label="Zoom out" onclick="zoomPreview(-0.2)">-</button>
          <button class="preview-tool" type="button" aria-label="Reset preview" onclick="resetPreviewTransform()">1:1</button>
          <button class="preview-tool" type="button" aria-label="Zoom in" onclick="zoomPreview(0.2)">+</button>
        `;

        stage.appendChild(img);
        preview.append(stage, tools);
        wirePreviewStage(stage);
        applyPreviewTransform();
        return;
      }

      const empty = document.createElement("div");
      empty.className = "preview-empty";
      empty.textContent = state.previewTitle || "Select a texture asset to preview it.";
      preview.appendChild(empty);
    }

    function clamp(value, min, max) {
      return Math.max(min, Math.min(max, value));
    }

    function applyPreviewTransform() {
      const img = $("previewImage");
      if (!img) return;
      img.style.transform = `translate(${previewPanX}px, ${previewPanY}px) scale(${previewScale})`;
    }

    function resetPreviewTransform(renderNow = true) {
      previewScale = 1;
      previewPanX = 0;
      previewPanY = 0;
      if (renderNow) applyPreviewTransform();
    }

    function zoomPreview(delta) {
      previewScale = clamp(previewScale + delta, 0.25, 8);
      if (previewScale <= 1) {
        previewPanX = 0;
        previewPanY = 0;
      }
      applyPreviewTransform();
    }

    function wirePreviewStage(stage) {
      stage.onpointerdown = event => {
        previewPointerId = event.pointerId;
        previewLastX = event.clientX;
        previewLastY = event.clientY;
        stage.setPointerCapture(event.pointerId);
        stage.classList.add("dragging");
      };

      stage.onpointermove = event => {
        if (previewPointerId !== event.pointerId) return;
        previewPanX += event.clientX - previewLastX;
        previewPanY += event.clientY - previewLastY;
        previewLastX = event.clientX;
        previewLastY = event.clientY;
        applyPreviewTransform();
      };

      const stopDrag = event => {
        if (previewPointerId !== event.pointerId) return;
        previewPointerId = null;
        stage.classList.remove("dragging");
      };

      stage.onpointerup = stopDrag;
      stage.onpointercancel = stopDrag;
      stage.ondblclick = () => resetPreviewTransform();
      stage.onwheel = event => {
        event.preventDefault();
        zoomPreview(event.deltaY < 0 ? 0.2 : -0.2);
      };
    }

    applyTheme();
    render();
  </script>
</body>
</html>
""";
    }

    private sealed class PakToolWebViewClient(MainActivity activity) : global::Android.Webkit.WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(global::Android.Webkit.WebView? view, global::Android.Webkit.IWebResourceRequest? request)
        {
            return activity.HandleBridgeUri(request?.Url);
        }

        public override bool ShouldOverrideUrlLoading(global::Android.Webkit.WebView? view, string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            try
            {
                return activity.HandleBridgeUri(global::Android.Net.Uri.Parse(url));
            }
            catch
            {
                return false;
            }
        }

        public override void OnPageFinished(global::Android.Webkit.WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            activity._webReady = true;
            StartCompressionWarmup();
            activity.PushState();
        }
    }
}
