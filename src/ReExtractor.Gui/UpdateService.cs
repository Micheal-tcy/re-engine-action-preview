using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReExtractor.Gui;

public sealed record UpdateRelease(Version Version, string Tag, string Name,
    string Notes, string DownloadUrl, long DownloadSize, string WebUrl);

public sealed record PreparedUpdate(string NewExecutable, string PackageDirectory, string StagingDirectory);

public sealed class UpdateService
{
    internal const string RepositoryUrl = "https://github.com/Micheal-tcy/re-engine-action-preview";
    internal const string ExecutableName = "ReExtractor-ActionPreview.exe";
    private const string LatestReleaseUrl = RepositoryUrl + "/releases/latest";
    private const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    private const long MaximumExecutableBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly HttpClient _http;
    private readonly Version? _currentVersion;

    public UpdateService() : this(Http) { }

    internal UpdateService(HttpClient http, Version? currentVersion = null)
    {
        _http = http;
        _currentVersion = currentVersion;
    }

    public Version CurrentVersion => _currentVersion ??
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    internal static string ArchiveName(Version version) =>
        $"ReExtractor-ActionPreview-v{version.ToString(3)}-win-x64.zip";

    private static string AssetUrl(string tag, string name) =>
        $"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{name}";

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        // The public latest-release redirect avoids the unauthenticated GitHub API quota.
        using var response = await _http.GetAsync(LatestReleaseUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var releasePage = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("GitHub 未返回版本地址");
        if (!IsRepositoryUri(releasePage, "/releases/tag/", prefix: true))
            throw new InvalidDataException("最新版本地址不属于本工具的 GitHub 仓库");
        var tag = Uri.UnescapeDataString(releasePage.AbsolutePath[(new Uri(RepositoryUrl).AbsolutePath.Length + "/releases/tag/".Length)..]);
        if (!TryParseVersion(tag, out var version))
            throw new InvalidDataException("最新发布未使用 v主版本.次版本.修订版本 格式");
        if (version <= CurrentVersion) return null;
        return new UpdateRelease(version, tag, $"RE Engine 动作预览 {tag}",
            $"发现新版本 {tag}。下载并校验后将更新当前程序，保留资源目录、配置及启动参数。\n\n完整更新说明：{releasePage}",
            AssetUrl(tag, ArchiveName(version)), 0, releasePage.AbsoluteUri);
    }

    public async Task<PreparedUpdate> DownloadAsync(UpdateRelease release,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!TryParseVersion(release.Tag, out var parsedVersion) || parsedVersion != release.Version ||
            release.Version <= CurrentVersion)
            throw new InvalidDataException("更新版本信息无效或并非新版本");
        var archiveName = ArchiveName(release.Version);
        var expectedUrl = AssetUrl(release.Tag, archiveName);
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri) ||
            !IsRepositoryUri(uri, $"/releases/download/{Uri.EscapeDataString(release.Tag)}/{archiveName}") ||
            uri.AbsoluteUri != expectedUrl)
            throw new InvalidDataException("更新包地址不属于本工具的 GitHub 发布");

        var staging = Path.Combine(Path.GetTempPath(), $"ReExtractor-action-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var checksum = await ReadChecksumAsync(release.Tag, archiveName, cancellationToken);
            var archivePath = Path.Combine(staging, "update.zip");
            using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                ValidateAssetResponse(response, expectedUrl);
                var total = response.Content.Headers.ContentLength ?? release.DownloadSize;
                if (total > MaximumArchiveBytes) throw new InvalidDataException("更新包超过允许的大小");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > MaximumArchiveBytes) throw new InvalidDataException("更新包超过允许的大小");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (total > 0) progress?.Report(Math.Clamp(received / (double)total, 0, 0.95));
                }
            }
            await using (var input = File.OpenRead(archivePath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包 SHA-256 校验失败，请重试下载");
            }

            var extracted = Path.Combine(staging, "extracted");
            Directory.CreateDirectory(extracted);
            var newExe = Path.Combine(extracted, ExecutableName);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                // Never extract paths supplied by the archive. Only the one root executable
                // is copied to a path chosen here; catalog, data and sidecars stay untouched.
                var candidates = archive.Entries.Where(e => e.FullName.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length != 1 || candidates[0].Length <= 0 || candidates[0].Length > MaximumExecutableBytes ||
                    ((candidates[0].ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("更新包必须包含唯一的根目录 ReExtractor-ActionPreview.exe");
                await using var source = candidates[0].Open();
                await using var destination = new FileStream(newExe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long extractedBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    extractedBytes += read;
                    if (extractedBytes > MaximumExecutableBytes || extractedBytes > candidates[0].Length)
                        throw new InvalidDataException("更新程序解压大小与包内记录不符");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (extractedBytes != candidates[0].Length)
                    throw new InvalidDataException("更新程序解压不完整");
            }
            var fileVersionText = FileVersionInfo.GetVersionInfo(newExe).FileVersion;
            if (!Version.TryParse(fileVersionText, out var fileVersion) ||
                fileVersion.Major != release.Version.Major || fileVersion.Minor != release.Version.Minor ||
                fileVersion.Build != release.Version.Build || fileVersion.Revision > 0)
                throw new InvalidDataException($"更新包版本校验失败：{fileVersionText ?? "未知"}");
            progress?.Report(1);
            return new PreparedUpdate(newExe, extracted, staging);
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    private async Task<string> ReadChecksumAsync(string tag, string archiveName, CancellationToken cancellationToken)
    {
        var checksumUrl = AssetUrl(tag, "SHA256SUMS.txt");
        using var response = await _http.GetAsync(checksumUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        ValidateAssetResponse(response, checksumUrl);
        if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("校验文件过大");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var data = new byte[65537];
        var length = 0;
        while (length < data.Length)
        {
            var read = await input.ReadAsync(data.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > 65536) throw new InvalidDataException("校验文件过大");
        var matches = Encoding.UTF8.GetString(data, 0, length).TrimStart('\uFEFF').Split('\n')
            .Select(line => line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*').Equals(archiveName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0][0].Length != 64 || !matches[0][0].All(Uri.IsHexDigit))
            throw new InvalidDataException("SHA256SUMS.txt 中缺少更新包的唯一 SHA-256 校验值");
        return matches[0][0];
    }

    private static bool IsRepositoryUri(Uri uri, string suffix, bool prefix = false)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var expected = new Uri(RepositoryUrl).AbsolutePath + suffix;
        return prefix ? uri.AbsolutePath.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
            : uri.AbsolutePath.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateAssetResponse(HttpResponseMessage response, string expectedUrl)
    {
        var actual = response.RequestMessage?.RequestUri;
        if (actual == null || actual.Scheme != Uri.UriSchemeHttps || !actual.IsDefaultPort || actual.UserInfo.Length != 0)
            throw new InvalidDataException("更新下载重定向地址无效");
        if (actual.AbsoluteUri == expectedUrl) return;
        // GitHub release assets use signed CDN URLs after a repository-owned redirect.
        if (actual.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            actual.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidDataException("更新下载重定向到了非 GitHub 发布服务器");
    }

    public void LaunchInstaller(PreparedUpdate update)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("当前自动更新仅支持 Windows");
        var currentExe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径");
        var script = BuildInstallerScript(update, currentExe, Environment.CurrentDirectory,
            Environment.GetCommandLineArgs().Skip(1).ToArray(), Environment.ProcessId, AppPaths.LogsDirectory);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ReExtractor-updater-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    internal static string BuildInstallerScript(PreparedUpdate update, string currentExe, string workingDirectory,
        string[] arguments, int oldPid, string logsDirectory)
    {
        currentExe = Path.GetFullPath(currentExe);
        var staging = Path.GetFullPath(update.StagingDirectory);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var stagingName = Path.GetFileName(staging);
        const string prefix = "ReExtractor-action-update-";
        if (!Path.GetFileName(currentExe).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase) ||
            !staging.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !stagingName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(stagingName[prefix.Length..], "N", out _) ||
            !Path.GetFullPath(update.PackageDirectory).Equals(Path.Combine(staging, "extracted"), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(update.NewExecutable).Equals(Path.Combine(staging, "extracted", ExecutableName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("自动更新仅能替换本工具固定名称的程序文件");
        if (!File.Exists(currentExe) || !File.Exists(update.NewExecutable)) throw new FileNotFoundException("更新程序文件不存在");
        if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(update.PackageDirectory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(update.NewExecutable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新临时目录不能是链接");
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        var token = Guid.NewGuid().ToString("N");
        var pending = currentExe + ".update-" + token;
        var backup = currentExe + ".previous-" + token;
        var commandLine = string.Join(" ", arguments.Select(QuoteWindowsArgument));
        var log = Path.Combine(Path.GetFullPath(logsDirectory), "update.log");
        return string.Join(Environment.NewLine,
        [
            "$ErrorActionPreference = 'Stop'",
            "$installed = $false",
            $"$target = {Q(currentExe)}",
            $"$pending = {Q(pending)}",
            $"$backup = {Q(backup)}",
            $"$source = {Q(update.NewExecutable)}",
            $"$updateLog = {Q(log)}",
            "New-Item -ItemType Directory -Path (Split-Path -Parent $updateLog) -Force | Out-Null",
            "try {",
            $"  $oldPid = {oldPid}",
            "  $deadline = (Get-Date).AddMinutes(2)",
            "  while (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) {",
            "    if ((Get-Date) -gt $deadline) { throw '原程序未退出，已取消更新' }",
            "    Start-Sleep -Milliseconds 250",
            "  }",
            "  Copy-Item -LiteralPath $source -Destination $pending",
            "  [System.IO.File]::Replace($pending, $target, $backup)",
            "  $installed = $true",
            "  $start = New-Object System.Diagnostics.ProcessStartInfo",
            "  $start.FileName = $target",
            $"  $start.WorkingDirectory = {Q(Path.GetFullPath(workingDirectory))}",
            $"  $start.Arguments = {Q(commandLine)}",
            "  $start.UseShellExecute = $false",
            "  [System.Diagnostics.Process]::Start($start) | Out-Null",
            "  Add-Content -LiteralPath $updateLog -Value ('{0:o} 更新完成' -f (Get-Date))",
            "  Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue",
            $"  Remove-Item -LiteralPath {Q(staging)} -Recurse -Force -ErrorAction SilentlyContinue",
            "  Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
            "} catch {",
            "  $failure = $_.Exception.Message",
            "  if ($installed -and (Test-Path -LiteralPath $backup)) {",
            "    try { [System.IO.File]::Replace($backup, $target, $pending) } catch { $failure += '; 回滚失败: ' + $_.Exception.Message }",
            "  }",
            "  Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue",
            "  Add-Content -LiteralPath $updateLog -Value ('{0:o} 更新失败: {1}' -f (Get-Date), $failure)",
            "  exit 1",
            "}",
        ]);
    }

    internal static string QuoteWindowsArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var value = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(value, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0 ||
            !(tag.Equals("v" + parsed.ToString(3), StringComparison.OrdinalIgnoreCase) || tag == parsed.ToString(3))) return false;
        version = parsed;
        return true;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RE-Engine-Action-Preview", "2.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }
}
