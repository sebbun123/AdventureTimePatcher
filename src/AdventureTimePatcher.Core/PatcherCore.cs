using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AdventureTimePatcher.Core;

public sealed record RepoRef(string Owner, string Name, string Branch)
{
    public string CommitApiUrl => $"https://api.github.com/repos/{Owner}/{Name}/commits/{Uri.EscapeDataString(Branch)}";
    public string ZipUrl => $"https://github.com/{Owner}/{Name}/archive/refs/heads/{Uri.EscapeDataString(Branch)}.zip";
}

// WHAT THE PATCHER CAN INSTALL (1.1.0). Each product is a GitHub repo with a manifest.txt and a folder under lua/.
public sealed record Product(
    string Key, string Name, string Owner, string Repo, string Folder,
    string MarkerFile, string StatusFile, string BackupRoot,
    string[] ProtectedPrefixes, string[] ProtectedFiles,
    bool HasTargetsIni, bool CompareBuilds, bool WriteInstalledTxt, string RunHint)
{
    public RepoRef RepoRef => new(Owner, Repo, "main");

    public static readonly Product AdventureTime = new(
        "adventuretime", "AdventureTime", "sebbun123", "Adventuretime", "adventuretime",
        "adventuretime_install.json", "adventuretime_update_status.json", "AdventureTimePatcher_backup",
        new[] { "settings/", "logs/", "backup-" }, new[] { "AdventureTime_targets.ini" },
        HasTargetsIni: true, CompareBuilds: true, WriteInstalledTxt: false,
        RunHint: "/lua run adventuretime");

    // LazCraft keeps per-character Settings/ and logs inside its own folder; its INIs are published files
    // (update.bat brings them current too). installed.txt is what its Settings tab reads for the versions.
    public static readonly Product LazCraft = new(
        "lazcraft", "LazCraft", "sebbun123", "LazCraft", "Lazcraft",
        "lazcraft_install.json", "lazcraft_update_status.json", "LazCraftPatcher_backup",
        new[] { "settings/", "logs/", "backup-" }, new[] { "installed.txt" },
        HasTargetsIni: false, CompareBuilds: false, WriteInstalledTxt: true,
        RunHint: "/lua run Lazcraft");

    public static readonly Product[] All = { AdventureTime, LazCraft };

    public static Product Find(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
        ?? AdventureTime;
}

public sealed record InstallMarker(string Owner, string Repo, string Branch, string Sha, DateTimeOffset InstalledAtUtc);

public sealed record UpdateStatus(string Owner, string Repo, string Branch, string LatestSha, string InstalledSha, bool UpdateAvailable, string Status, DateTimeOffset CheckedAtUtc, string Error);

public sealed record CheckResult(RepoRef Repo, string MqRoot, string LatestSha, string InstalledSha, bool UpdateAvailable);

public sealed record UpdateResult(RepoRef Repo, string MqRoot, string LatestSha, bool AlreadyCurrent, IReadOnlyList<string> CopiedFiles, string? BackupDir);

public sealed record SelfUpdateResult(bool UpdateAvailable, string LocalVersion, string LatestVersion, string DownloadUrl);

public sealed record AdventureTimeBuild(string Version, string BuildTag)
{
    public string Display => string.IsNullOrWhiteSpace(BuildTag) ? Version : $"{Version} build {BuildTag}";
}

public sealed class LocalBuildNewerException : Exception
{
    public AdventureTimeBuild LocalBuild { get; }
    public AdventureTimeBuild RemoteBuild { get; }

    public LocalBuildNewerException(AdventureTimeBuild localBuild, AdventureTimeBuild remoteBuild)
        : base($"The installed copy appears newer than GitHub. Installed: {localBuild.Display}; GitHub: {remoteBuild.Display}.")
    {
        LocalBuild = localBuild;
        RemoteBuild = remoteBuild;
    }
}

public sealed class PatcherService
{
    public Product Product { get; }
    public PatcherService(Product? product = null) { Product = product ?? Product.AdventureTime; }
    private string MarkerFileName => Product.MarkerFile;
    private string StatusFileName => Product.StatusFile;
    private string LuaPrefix => "lua/" + Product.Folder.ToLowerInvariant() + "/";
    private const string PatcherOwner = "sebbun123";
    private const string PatcherRepo = "AdventureTimePatcher";
    private const string PatcherLatestApi = $"https://api.github.com/repos/{PatcherOwner}/{PatcherRepo}/releases/latest";
    private const string PatcherDownloadUrlWindows = $"https://github.com/{PatcherOwner}/{PatcherRepo}/releases/latest/download/AdventureTimePatcher-win-x64.exe";
    private const string PatcherDownloadUrlLinux = $"https://github.com/{PatcherOwner}/{PatcherRepo}/releases/latest/download/AdventureTimePatcher-linux-x64";

    public static string PatcherDownloadUrl => OperatingSystem.IsWindows() ? PatcherDownloadUrlWindows : PatcherDownloadUrlLinux;
    private static string PatcherAssetName => OperatingSystem.IsWindows() ? "AdventureTimePatcher-win-x64.exe" : "AdventureTimePatcher-linux-x64";

    // The asset's download URL when the release lists it as fully uploaded; null while it is missing or still uploading.
    internal static string? UploadedAssetUrl(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var n) || !string.Equals(n.GetString(), assetName, StringComparison.OrdinalIgnoreCase)) continue;
            if (a.TryGetProperty("state", out var st) && !string.Equals(st.GetString(), "uploaded", StringComparison.OrdinalIgnoreCase)) return null;
            if (a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.GetInt64() <= 0) return null;
            return a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
        }
        return null;
    }

    // AdventureTime is more than init.lua since 1.22 (modules/*.lua). What gets installed is whatever the repo's
    // manifest.txt lists, so a new module never needs a patcher change. User data is never written.

    public sealed record ManifestEntry(string Path, string Sha, bool Delete);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string ShortSha(string sha) => string.IsNullOrEmpty(sha) ? "" : (sha.Length <= 12 ? sha : sha[..12]);

    public static string LocalPatcherVersionString()
    {
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
                  ?? typeof(PatcherService).Assembly.GetName().Version;
        if (ver is null) return "";
        return $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    public SelfUpdateResult CheckSelfUpdate()
    {
        var localStr = LocalPatcherVersionString();
        var local = ParseVersion(localStr);
        try
        {
            using var http = NewHttpClient();
            var json = http.GetStringAsync(PatcherLatestApi).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = ParseVersion(tag);
            var latestStr = latest is null ? tag.TrimStart('v', 'V') : $"{latest.Major}.{latest.Minor}.{Math.Max(latest.Build, 0)}";
            if (local is null || latest is null)
                return new SelfUpdateResult(false, localStr, latestStr, PatcherDownloadUrl);
            var localCmp = new Version(local.Major, local.Minor, Math.Max(local.Build, 0));
            var latestCmp = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latestCmp <= localCmp)
                return new SelfUpdateResult(false, localStr, latestStr, PatcherDownloadUrl);
            // Only offer the update once the release actually carries this platform's file. A release published
            // before its assets finish uploading has the new tag but nothing to download yet (404).
            var assetUrl = UploadedAssetUrl(doc.RootElement, PatcherAssetName);
            if (assetUrl is null)
                return new SelfUpdateResult(false, localStr, latestStr, PatcherDownloadUrl);
            return new SelfUpdateResult(true, localStr, latestStr, assetUrl);
        }
        catch
        {
            return new SelfUpdateResult(false, localStr, string.Empty, PatcherDownloadUrl);
        }
    }

    public async Task<CheckResult> CheckAsync(string mqRoot, RepoRef repo, CancellationToken ct = default)
    {
        mqRoot = ValidateMqRoot(mqRoot);
        using var http = NewHttpClient();
        var latestSha = await GetLatestCommitShaAsync(http, repo, ct).ConfigureAwait(false);
        var marker = ReadMarker(mqRoot);
        var installedSha = marker?.Sha ?? string.Empty;
        var available = installedSha != latestSha
            || (await LocalFilesDifferAsync(http, repo, latestSha, mqRoot, ct).ConfigureAwait(false)).Count > 0;
        WriteUpdateStatus(mqRoot, repo, latestSha, installedSha, available, available ? "update_available" : "up_to_date");
        return new CheckResult(repo, mqRoot, latestSha, installedSha, available);
    }

    public async Task<UpdateResult> UpdateAsync(string mqRoot, RepoRef repo, IProgress<string>? progress = null, CancellationToken ct = default, bool allowLocalNewer = false)
    {
        mqRoot = ValidateMqRoot(mqRoot);
        using var http = NewHttpClient();
        progress?.Report("Checking GitHub...");
        var latestSha = await GetLatestCommitShaAsync(http, repo, ct).ConfigureAwait(false);
        var marker = ReadMarker(mqRoot);

        // SAME COMMIT IS NOT ENOUGH: an install from patcher 1.0.4 or earlier has init.lua but no modules.
        var differ = marker?.Sha == latestSha
            ? await LocalFilesDifferAsync(http, repo, latestSha, mqRoot, ct).ConfigureAwait(false)
            : new List<string>();
        if (differ.Count > 0)
            progress?.Report("Installed files are missing or different: " + string.Join(", ", differ));
        if (marker?.Sha == latestSha && differ.Count == 0)
        {
            WriteUpdateStatus(mqRoot, repo, latestSha, marker?.Sha, false, "up_to_date");
            return new UpdateResult(repo, mqRoot, latestSha, true, Array.Empty<string>(), null);
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"AdventureTimePatcher_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var zipPath = Path.Combine(tempRoot, "source.zip");
            var extractPath = Path.Combine(tempRoot, "source");
            progress?.Report($"Downloading {Product.Name}...");
            await DownloadFileAsync(http, repo.ZipUrl, zipPath, ct).ConfigureAwait(false);
            progress?.Report("Extracting package...");
            ZipFile.ExtractToDirectory(zipPath, extractPath);

            var sourceRoot = FindSourceRoot(extractPath);
            var luaDir = ResolveLuaDir(mqRoot);
            var localInit = Path.Combine(luaDir, "init.lua");
            var remoteInit = Path.Combine(sourceRoot, "init.lua");
            var localBuild = ReadAdventureTimeBuild(localInit);
            var remoteBuild = ReadAdventureTimeBuild(remoteInit);
            if (!allowLocalNewer && Product.CompareBuilds && IsLocalBuildNewer(localBuild, remoteBuild))
            {
                WriteUpdateStatus(mqRoot, repo, latestSha, marker?.Sha, true, "local_newer");
                throw new LocalBuildNewerException(localBuild, remoteBuild);
            }
            var configDir = Path.Combine(mqRoot, "config");
            Directory.CreateDirectory(luaDir);
            Directory.CreateDirectory(configDir);

            var backupDir = Path.Combine(configDir, Product.BackupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var copied = new List<string>();

            var manifest = ReadManifestFile(Path.Combine(sourceRoot, "manifest.txt"));
            if (manifest.Count == 0)
            {
                // No manifest in the repo: init.lua plus every modules/*.lua beside it.
                manifest.Add(new ManifestEntry("init.lua", string.Empty, false));
                var modDir = Path.Combine(sourceRoot, "modules");
                if (Directory.Exists(modDir))
                    foreach (var f in Directory.GetFiles(modDir, "*.lua").OrderBy(f => f, StringComparer.Ordinal))
                        manifest.Add(new ManifestEntry("modules/" + Path.GetFileName(f), string.Empty, false));
            }
            // ALL OR NOTHING: every file is checked against its hash before anything in the install is touched.
            foreach (var e in manifest.Where(e => !e.Delete))
            {
                EnsureManagedLuaFile(LuaPrefix + e.Path);
                var src = Path.Combine(sourceRoot, e.Path);
                if (!File.Exists(src)) throw new FileNotFoundException($"The download is missing {e.Path} (listed in manifest.txt).");
                if (e.Sha.Length > 0 && !string.Equals(Sha256(src), e.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Checksum mismatch on {e.Path} - nothing was changed.");
            }
            foreach (var e in manifest)
            {
                var dst = Path.Combine(luaDir, e.Path);
                if (e.Delete)
                {
                    EnsureManagedLuaFile(LuaPrefix + e.Path);
                    if (!File.Exists(dst)) continue;
                    var bk = Path.Combine(backupDir, e.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(bk)!);
                    File.Move(dst, bk, overwrite: true);
                    copied.Add("removed " + LuaPrefix + e.Path);
                    continue;
                }
                CopyOverwriteWithBackup(Path.Combine(sourceRoot, e.Path), dst, backupDir, LuaPrefix + e.Path, copied, e.Path);
            }
            var readme = Path.Combine(sourceRoot, "README.md");
            if (File.Exists(readme) && !manifest.Any(e => string.Equals(e.Path, "README.md", StringComparison.OrdinalIgnoreCase)))
                CopyOverwriteWithBackup(readme, Path.Combine(luaDir, "README.md"), backupDir, LuaPrefix + "README.md", copied, "README.md");

            var targetsSrc = Path.Combine(sourceRoot, "AdventureTime_targets.ini");
            if (Product.HasTargetsIni && File.Exists(targetsSrc))
            {
                var targetsDst = Path.Combine(luaDir, "AdventureTime_targets.ini");
                if (File.Exists(targetsDst))
                {
                    File.Copy(targetsSrc, Path.Combine(luaDir, "AdventureTime_targets.ini.example"), overwrite: true);
                    copied.Add("lua/adventuretime/AdventureTime_targets.ini.example");
                }
                else
                {
                    File.Copy(targetsSrc, targetsDst);
                    copied.Add("lua/adventuretime/AdventureTime_targets.ini");
                }
            }

            if (Product.WriteInstalledTxt)
            {
                var (build, config) = ReadManifestStamps(Path.Combine(sourceRoot, "manifest.txt"));
                File.WriteAllText(Path.Combine(luaDir, "installed.txt"),
                    $"# {Product.Name} - written by AdventureTimePatcher. Do not edit.\r\n# build {build}\r\n# config {config}\r\n");
            }
            WriteMarker(mqRoot, new InstallMarker(repo.Owner, repo.Name, repo.Branch, latestSha, DateTimeOffset.UtcNow));
            WriteUpdateStatus(mqRoot, repo, latestSha, latestSha, false, "up_to_date");
            PruneBackups(Path.Combine(configDir, Product.BackupRoot), keep: 5);
            progress?.Report("Update complete.");
            return new UpdateResult(repo, mqRoot, latestSha, false, copied, Directory.Exists(backupDir) ? backupDir : null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    public InstallMarker? ReadMarker(string mqRoot)
    {
        var path = Path.Combine(mqRoot, "config", MarkerFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }


    public static AdventureTimeBuild ReadAdventureTimeBuild(string path)
    {
        if (!File.Exists(path)) return new AdventureTimeBuild(string.Empty, string.Empty);
        var text = File.ReadAllText(path);
        var version = MatchLuaString(text, "VERSION");
        var build = MatchLuaString(text, "BUILD_TAG");
        return new AdventureTimeBuild(version, build);
    }

    public static bool IsLocalBuildNewer(AdventureTimeBuild localBuild, AdventureTimeBuild remoteBuild)
    {
        var l = ParseBuildVersion(localBuild.BuildTag.Length > 0 ? localBuild.BuildTag : localBuild.Version);
        var r = ParseBuildVersion(remoteBuild.BuildTag.Length > 0 ? remoteBuild.BuildTag : remoteBuild.Version);
        if (l is null || r is null) return false;
        return l.CompareTo(r) > 0;
    }

    public async Task<string> DownloadLatestPatcherAsync(IProgress<string>? log, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AdventureTimePatcher_update");
        Directory.CreateDirectory(dir);
        var fileName = OperatingSystem.IsWindows() ? "AdventureTimePatcher.exe" : "AdventureTimePatcher-linux-x64";
        var dest = Path.Combine(dir, fileName);
        log?.Report($"Downloading {PatcherDownloadUrl} ...");
        using var http = NewHttpClient();
        using var res = await http.GetAsync(PatcherDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using (var output = File.Create(dest))
            await res.Content.CopyToAsync(output, ct).ConfigureAwait(false);
        log?.Report($"Downloaded to {dest}");
        return dest;
    }

    public static string WriteWindowsSelfUpdateHelper(int pid, string newExe, string targetExe, string? workingDir, string relaunchArgs = "")
    {
        var dir = Path.GetDirectoryName(newExe) ?? Path.GetTempPath();
        var helper = Path.Combine(dir, "adventuretime_patcher_self_update.cmd");
        workingDir ??= Path.GetDirectoryName(targetExe) ?? dir;
        var args = string.IsNullOrWhiteSpace(relaunchArgs) ? "" : " " + relaunchArgs.Trim();
        var failLog = Path.Combine(dir, "self_update_fail.txt");
        var content = $"""
            @echo off
            setlocal
            :wait
            tasklist /FI "PID eq {pid}" 2>NUL | find "{pid}" >NUL
            if not errorlevel 1 (
              ping -n 2 127.0.0.1 >NUL
              goto wait
            )
            ping -n 2 127.0.0.1 >NUL
            copy /Y "{newExe}" "{targetExe}" >NUL
            if errorlevel 1 (
              echo AdventureTimePatcher self-update failed to replace the exe.> "{failLog}"
              exit /b 1
            )
            start "" /D "{workingDir}" "{targetExe}"{args}
            del "%~f0" >NUL 2>&1
            endlocal
            """;
        File.WriteAllText(helper, content);
        return helper;
    }

    public async Task ApplyLinuxSelfUpdateAsync(IProgress<string>? log, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("Linux self-update only.");
        var current = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve current executable path.");
        var downloaded = await DownloadLatestPatcherAsync(log, ct).ConfigureAwait(false);
        var dir = Path.GetDirectoryName(current) ?? ".";
        var backup = current + ".old";
        var staged = Path.Combine(dir, Path.GetFileName(current) + ".new");
        File.Copy(downloaded, staged, overwrite: true);
        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
        File.Move(current, backup);
        File.Move(staged, current);
        try { File.SetUnixFileMode(current, (UnixFileMode)Convert.ToInt32("755", 8)); } catch { }
        log?.Report($"Replaced {current}. Re-run AdventureTimePatcher to use the new version.");
        try { File.Delete(backup); } catch { }
    }

    private static string MatchLuaString(string text, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, $@"(?:local\s+)?{name}\s*=\s*['\""]([^'\""\r\n]+)['\""]");
        return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
    }

    private static Version? ParseBuildVersion(string text)
    {
        text = (text ?? string.Empty).Trim().TrimStart('v', 'V');
        if (text.Length == 0) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:\.\d+){0,3}");
        if (!m.Success) return null;
        var parts = m.Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
        while (parts.Count < 2) parts.Add(0);
        while (parts.Count < 4) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    private static Version? ParseVersion(string? text)
    {
        text = (text ?? string.Empty).Trim().TrimStart('v', 'V');
        if (text.Length == 0) return null;
        var core = text.Split('-', '+')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }

    private static string ValidateMqRoot(string mqRoot)
    {
        var full = Path.GetFullPath(mqRoot);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"MQ root not found: {full}");
        return full;
    }

    private static HttpClient NewHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AdventureTimePatcher", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private static async Task<string> GetLatestCommitShaAsync(HttpClient http, RepoRef repo, CancellationToken ct)
    {
        using var res = await http.GetAsync(repo.CommitApiUrl, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var sha = doc.RootElement.GetProperty("sha").GetString();
        if (string.IsNullOrWhiteSpace(sha)) throw new InvalidOperationException("GitHub response did not contain a commit sha.");
        return sha;
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string destination, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    private static string FindSourceRoot(string extractPath)
    {
        var candidates = Directory.GetDirectories(extractPath, "*", SearchOption.TopDirectoryOnly).Concat(new[] { extractPath });
        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "init.lua"))) return dir;
        }
        throw new FileNotFoundException("Downloaded source did not contain init.lua at the repository root.");
    }

    private void CopyOverwriteWithBackup(string source, string destination, string backupDir, string displayPath, List<string> copied, string? relPath = null)
    {
        EnsureManagedLuaFile(displayPath);
        if (!File.Exists(source)) throw new FileNotFoundException($"Required source file missing: {source}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (File.Exists(destination))
        {
            Directory.CreateDirectory(backupDir);
            var bk = Path.Combine(backupDir, relPath ?? Path.GetFileName(destination));
            Directory.CreateDirectory(Path.GetDirectoryName(bk)!);
            File.Copy(destination, bk, overwrite: true);
        }

        File.Copy(source, destination, overwrite: true);
        copied.Add(displayPath);
    }

    private void EnsureManagedLuaFile(string displayPath)
    {
        var normalized = displayPath.Replace('\\', '/');
        var ok = normalized.StartsWith(LuaPrefix, StringComparison.OrdinalIgnoreCase);
        var rel = ok ? normalized[LuaPrefix.Length..] : string.Empty;
        if (ok && (rel.Length == 0 || rel.StartsWith('/') || rel.Contains(':') || rel.Split('/').Any(p => p is "" or "." or "..")))
            ok = false;
        if (ok && Product.ProtectedFiles.Contains(rel, StringComparer.OrdinalIgnoreCase)) ok = false;
        if (ok && Product.ProtectedPrefixes.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase))) ok = false;
        if (!ok)
            throw new InvalidOperationException($"Refusing to overwrite unmanaged {Product.Name} file: {displayPath}");
    }

    public static List<ManifestEntry> ParseManifest(string text)
    {
        var list = new List<ManifestEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var p = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p[0] == "file" && p.Length >= 3) list.Add(new ManifestEntry(p[1].Replace('\\', '/'), p[2].ToLowerInvariant(), false));
            else if (p[0] == "delete" && p.Length >= 2) list.Add(new ManifestEntry(p[1].Replace('\\', '/'), string.Empty, true));
        }
        return list;
    }

    // build and config stamps from a manifest ("(unversioned)" / "" when absent), as LazCraft's update.bat records them.
    private static (string Build, string Config) ReadManifestStamps(string path)
    {
        string build = "(unversioned)", config = "";
        if (!File.Exists(path)) return (build, config);
        foreach (var raw in File.ReadAllLines(path))
        {
            var p = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[0] == "build") build = p[1];
            else if (p.Length >= 2 && p[0] == "config") config = p[1];
        }
        return (build, config);
    }

    // The product's folder under lua/, matched without regard to case (Lazcraft vs lazcraft) so an existing
    // install is updated in place - Linux/Wine paths are case-sensitive. A new install uses Product.Folder.
    public string ResolveLuaDir(string mqRoot)
    {
        var lua = Path.Combine(mqRoot, "lua");
        if (Directory.Exists(lua))
        {
            var hit = Directory.GetDirectories(lua)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), Product.Folder, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return Path.Combine(lua, Product.Folder);
    }

    private static List<ManifestEntry> ReadManifestFile(string path) =>
        File.Exists(path) ? ParseManifest(File.ReadAllText(path)) : new List<ManifestEntry>();

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }

    // The manifest.txt of that commit, compared with what is installed. The files that are missing or
    // different; empty when they all match, or when the manifest cannot be read (nothing to go on).
    public List<string> CompareInstall(string mqRoot, IEnumerable<ManifestEntry> manifest)
    {
        var luaDir = ResolveLuaDir(mqRoot);
        var bad = new List<string>();
        foreach (var e in manifest)
        {
            var p = Path.Combine(luaDir, e.Path);
            if (e.Delete) { if (File.Exists(p)) bad.Add(e.Path); continue; }
            if (!File.Exists(p)) { bad.Add(e.Path); continue; }
            if (e.Sha.Length > 0 && !string.Equals(Sha256(p), e.Sha, StringComparison.OrdinalIgnoreCase)) bad.Add(e.Path);
        }
        return bad;
    }

    private async Task<List<string>> LocalFilesDifferAsync(HttpClient http, RepoRef repo, string sha, string mqRoot, CancellationToken ct)
    {
        try
        {
            var url = $"https://raw.githubusercontent.com/{repo.Owner}/{repo.Name}/{sha}/manifest.txt";
            var text = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            return CompareInstall(mqRoot, ParseManifest(text));
        }
        catch
        {
            return new List<string>();
        }
    }

    private void WriteMarker(string mqRoot, InstallMarker marker)
    {
        var configDir = Path.Combine(mqRoot, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, MarkerFileName), JsonSerializer.Serialize(marker, JsonOptions));
    }

    private void WriteUpdateStatus(string mqRoot, RepoRef repo, string latestSha, string? installedSha, bool updateAvailable, string status)
    {
        var configDir = Path.Combine(mqRoot, "config");
        Directory.CreateDirectory(configDir);
        var payload = new UpdateStatus(repo.Owner, repo.Name, repo.Branch, latestSha, installedSha ?? string.Empty, updateAvailable, status, DateTimeOffset.UtcNow, string.Empty);
        File.WriteAllText(Path.Combine(configDir, StatusFileName), JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static void PruneBackups(string backupRoot, int keep)
    {
        if (!Directory.Exists(backupRoot)) return;
        var dirs = Directory.GetDirectories(backupRoot)
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.CreationTimeUtc)
            .Skip(keep);
        foreach (var dir in dirs)
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }
}
