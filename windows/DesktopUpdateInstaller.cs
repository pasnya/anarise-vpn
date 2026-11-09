using System.Diagnostics;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Anarise;

public sealed class DesktopUpdateInstaller
{
    private const long MaxUpdateBytes = 500L * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private const string WindowsUpdaterScript = """
        param(
            [Parameter(Mandatory=$true)][string]$ArchivePath,
            [Parameter(Mandatory=$true)][string]$InstallDirectory,
            [Parameter(Mandatory=$true)][int]$ProcessId
        )
        $ErrorActionPreference = 'Stop'
        $stage = "$InstallDirectory.update-stage-$PID"
        $backup = "$InstallDirectory.update-backup-$PID"
        try {
            while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
            if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
            New-Item -ItemType Directory -Path $stage | Out-Null
            Expand-Archive -LiteralPath $ArchivePath -DestinationPath $stage -Force
            $newExe = Join-Path $stage 'Anarise.exe'
            if (-not (Test-Path -LiteralPath $newExe -PathType Leaf)) { throw 'В архиве обновления нет Anarise.exe.' }
            Move-Item -LiteralPath $InstallDirectory -Destination $backup
            try {
                Move-Item -LiteralPath $stage -Destination $InstallDirectory
            } catch {
                Move-Item -LiteralPath $backup -Destination $InstallDirectory
                throw
            }
            Remove-Item -LiteralPath $backup -Recurse -Force
            Remove-Item -LiteralPath $ArchivePath -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath (Join-Path $InstallDirectory 'Anarise.exe')
        } catch {
            if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $InstallDirectory)) {
                Move-Item -LiteralPath $backup -Destination $InstallDirectory -ErrorAction SilentlyContinue
            }
            Add-Type -AssemblyName PresentationFramework -ErrorAction SilentlyContinue
            [System.Windows.MessageBox]::Show('Не удалось установить обновление ANARISE. Старую версию оставили без изменений. ' + $_.Exception.Message, 'ANARISE VPN') | Out-Null
        } finally {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
        """;

    private const string MacUpdaterScript = """
        #!/bin/bash
        set -euo pipefail
        ARCHIVE="$1"
        APP_PATH="$2"
        PROCESS_ID="$3"
        BASE="$HOME/.anarise/updates"
        MOUNT="$BASE/mount-$$"
        STAGE="$BASE/stage-$$"
        STAGED_APP="$STAGE/Anarise.app"
        PARENT="$(dirname "$APP_PATH")"
        BACKUP="$APP_PATH.update-backup-$$"
        cleanup() {
          /usr/bin/hdiutil detach "$MOUNT" -quiet >/dev/null 2>&1 || true
          /bin/rm -rf "$MOUNT" "$STAGE"
        }
        show_error() {
          /usr/bin/osascript -e 'display dialog "Не удалось установить обновление ANARISE. Старая версия оставлена без изменений. Повторите попытку позже." with title "ANARISE VPN" buttons {"OK"} default button "OK"' >/dev/null 2>&1 || true
        }
        trap 'show_error' ERR
        trap cleanup EXIT
        while /bin/kill -0 "$PROCESS_ID" 2>/dev/null; do /bin/sleep 0.5; done
        /bin/mkdir -p "$MOUNT" "$STAGE"
        /usr/bin/hdiutil attach -nobrowse -readonly -mountpoint "$MOUNT" "$ARCHIVE" >/dev/null
        SOURCE_APP="$(/usr/bin/find "$MOUNT" -type d -name Anarise.app -print -quit)"
        if [ -z "$SOURCE_APP" ] || [ ! -x "$SOURCE_APP/Contents/MacOS/Anarise" ]; then
          show_error
          exit 1
        fi
        /usr/bin/ditto "$SOURCE_APP" "$STAGED_APP"
        /usr/bin/hdiutil detach "$MOUNT" -quiet >/dev/null
        if [ -w "$PARENT" ]; then
          /bin/mv "$APP_PATH" "$BACKUP"
          if ! /bin/mv "$STAGED_APP" "$APP_PATH"; then
            /bin/mv "$BACKUP" "$APP_PATH"
            show_error
            exit 1
          fi
        else
          if ! /usr/bin/osascript - "$STAGED_APP" "$APP_PATH" "$BACKUP" <<'APPLESCRIPT'
        on run argv
          set sourcePath to item 1 of argv
          set appPath to item 2 of argv
          set backupPath to item 3 of argv
          set stagePath to appPath & ".update-stage-" & (do shell script "/usr/bin/uuidgen")
          set command to "/usr/bin/ditto " & quoted form of sourcePath & " " & quoted form of stagePath & " && /bin/mv " & quoted form of appPath & " " & quoted form of backupPath & " && (/bin/mv " & quoted form of stagePath & " " & quoted form of appPath & " || { /bin/mv " & quoted form of backupPath & " " & quoted form of appPath & "; exit 1; }) && /usr/bin/xattr -cr " & quoted form of appPath & " && /bin/rm -rf " & quoted form of backupPath
          do shell script command with administrator privileges
        end run
        APPLESCRIPT
          then
            show_error
            exit 1
          fi
        fi
        /usr/bin/xattr -cr "$APP_PATH" >/dev/null 2>&1 || true
        /bin/rm -f "$ARCHIVE"
        /usr/bin/open -a "$APP_PATH"
        """;

    public async Task InstallAsync(
        string downloadUrl,
        string? expectedSha256,
        Action<int, string> reportProgress,
        CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        cancellationToken = linkedCancellation.Token;
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ссылка на обновление не прошла проверку безопасности.");
        }

        string updateDirectory = GetUpdateDirectory();
        Directory.CreateDirectory(updateDirectory);
        string extension = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dmg" : ".zip";
        string archivePath = Path.Combine(updateDirectory, "anarise-update" + extension);
        string partialPath = archivePath + ".part";
        try
        {
            await DownloadAsync(uri, partialPath, expectedSha256, reportProgress, cancellationToken);
            File.Move(partialPath, archivePath, true);
            reportProgress(100, "Загрузка завершена. Подготовка установки…");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                StartWindowsUpdater(archivePath, updateDirectory);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                StartMacUpdater(archivePath, updateDirectory);
            else
                throw new PlatformNotSupportedException("Автоматическое обновление доступно только для Windows и macOS.");
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private static async Task DownloadAsync(
        Uri uri,
        string destinationPath,
        string? expectedSha256,
        Action<int, string> reportProgress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("ANARISE-VPN-Updater");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri == null || finalUri.Scheme != Uri.UriSchemeHttps ||
            !(finalUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
              finalUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Сервер загрузки обновления не прошёл проверку.");
        }

        long? length = response.Content.Headers.ContentLength;
        if (length > MaxUpdateBytes)
            throw new InvalidDataException("Размер файла обновления превышает допустимый предел.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            byte[] buffer = new byte[128 * 1024];
            long downloaded = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                downloaded += read;
                if (downloaded > MaxUpdateBytes)
                    throw new InvalidDataException("Размер файла обновления превышает допустимый предел.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                int percent = length is > 0 ? (int)Math.Clamp(downloaded * 100 / length.Value, 0, 99) : 0;
                reportProgress(percent, length is > 0
                    ? $"Загружено {downloaded / 1024 / 1024} из {length.Value / 1024 / 1024} МБ…"
                    : $"Загружено {downloaded / 1024 / 1024} МБ…");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            await using var file = File.OpenRead(destinationPath);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (!actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма файла обновления не совпала.");
        }
    }

    private static string GetUpdateDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "AnariseVPN", "updates");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".anarise", "updates");
    }

    private static void StartWindowsUpdater(string archivePath, string updateDirectory)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Не удалось определить путь установленного приложения.");
        string installDirectory = Path.GetDirectoryName(executable)!;
        string updaterPath = Path.Combine(updateDirectory, "Apply-WindowsUpdate.ps1");
        File.WriteAllText(updaterPath, WindowsUpdaterScript);

        bool needsElevation = !CanWriteTo(installDirectory);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true,
            Verb = needsElevation ? "runas" : string.Empty,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(updaterPath);
        start.ArgumentList.Add("-ArchivePath");
        start.ArgumentList.Add(archivePath);
        start.ArgumentList.Add("-InstallDirectory");
        start.ArgumentList.Add(installDirectory);
        start.ArgumentList.Add("-ProcessId");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        if (Process.Start(start) == null)
            throw new InvalidOperationException("Не удалось запустить установщик обновления.");
    }

    private static void StartMacUpdater(string archivePath, string updateDirectory)
    {
        string? appBundle = FindMacAppBundle(AppDomain.CurrentDomain.BaseDirectory);
        if (string.IsNullOrWhiteSpace(appBundle) || appBundle.StartsWith("/Volumes/", StringComparison.Ordinal))
            throw new InvalidOperationException("Сначала переместите ANARISE VPN в папку «Программы», затем повторите обновление.");

        string updaterPath = Path.Combine(updateDirectory, "Apply-MacUpdate.sh");
        File.WriteAllText(updaterPath, MacUpdaterScript);
        var start = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(updaterPath);
        start.ArgumentList.Add(archivePath);
        start.ArgumentList.Add(appBundle);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        if (Process.Start(start) == null)
            throw new InvalidOperationException("Не удалось запустить установщик обновления.");
    }

    private static string? FindMacAppBundle(string startPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startPath));
        while (current != null)
        {
            if (current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static bool CanWriteTo(string directory)
    {
        string probe = Path.Combine(directory, ".anarise-update-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
