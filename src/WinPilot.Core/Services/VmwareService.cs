using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

public enum VmLaunchOutcome
{
    Started = 0,
    SkippedAlreadyRunning = 1,
    Failed = 2
}

public sealed record VmLaunchResult(VmwareVmEntry Vm, VmLaunchOutcome Outcome, string Detail);

public sealed record VmBatchSummary(
    int StartedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<VmLaunchResult> Results);

public sealed record DiscoveredVmInfo(string Name, string VmxPath, bool Exists);

/// <summary>
/// Discovers, queries, starts, and stops VMware Workstation/Player virtual machines via vmrun.exe.
/// </summary>
public sealed partial class VmwareService
{
    private readonly ActivityLogger _logger;
    private readonly Func<string, string, int, (int ExitCode, string StdOut, string StdErr)> _commandRunner;

    public VmwareService(
        ActivityLogger? logger = null,
        Func<string, string, int, (int ExitCode, string StdOut, string StdErr)>? commandRunner = null)
    {
        _logger = logger ?? new ActivityLogger();
        _commandRunner = commandRunner ?? RunProcess;
    }

    public static string? ResolveVmrunPath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string[] candidates =
        {
            @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe",
            @"C:\Program Files\VMware\VMware Workstation\vmrun.exe",
            @"C:\Program Files (x86)\VMware\VMware VIX\vmrun.exe",
            @"C:\Program Files\VMware\VMware VIX\vmrun.exe"
        };

        foreach (string path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        string[] regKeys =
        {
            @"SOFTWARE\WOW6432Node\VMware, Inc.\VMware Workstation",
            @"SOFTWARE\VMware, Inc.\VMware Workstation",
            @"SOFTWARE\WOW6432Node\VMware, Inc.\VMware Player",
            @"SOFTWARE\VMware, Inc.\VMware Player"
        };

        foreach (string keyPath in regKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key?.GetValue("InstallPath") is string installDir && !string.IsNullOrWhiteSpace(installDir))
                {
                    string candidate = Path.Combine(installDir, "vmrun.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // Ignore registry access issues.
            }
        }

        return null;
    }

    public static string DefaultInventoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VMware",
        "inventory.vmls");

    public static List<DiscoveredVmInfo> DiscoverInventoryVms(string? inventoryPath = null)
    {
        string path = inventoryPath ?? DefaultInventoryPath;
        if (!File.Exists(path)) return new List<DiscoveredVmInfo>();

        string content = File.ReadAllText(path);
        return ParseInventoryContent(content);
    }

    public static List<DiscoveredVmInfo> ParseInventoryContent(string content)
    {
        var entries = new Dictionary<string, (string? Config, string? DisplayName)>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            var match = InventoryLineRegex().Match(line);
            if (!match.Success) continue;

            string key = match.Groups["id"].Value;
            string field = match.Groups["field"].Value;
            string val = match.Groups["val"].Value;

            entries.TryGetValue(key, out var current);
            if (field.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                current.Config = val;
            }
            else if (field.Equals("DisplayName", StringComparison.OrdinalIgnoreCase))
            {
                current.DisplayName = val;
            }
            entries[key] = current;
        }

        var result = new List<DiscoveredVmInfo>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, (configPath, displayName)) in entries)
        {
            if (string.IsNullOrWhiteSpace(configPath)) continue;
            string trimmedPath = configPath.Trim();
            if (!seenPaths.Add(trimmedPath)) continue;

            bool exists = File.Exists(trimmedPath);
            string name = !string.IsNullOrWhiteSpace(displayName)
                ? displayName.Trim()
                : (exists ? ParseVmxDisplayName(trimmedPath) : null) ?? Path.GetFileNameWithoutExtension(trimmedPath);

            result.Add(new DiscoveredVmInfo(name, trimmedPath, exists));
        }

        return result;
    }

    public static List<DiscoveredVmInfo> ScanFolderForVmx(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return new List<DiscoveredVmInfo>();

        var result = new List<DiscoveredVmInfo>();
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true
            };

            foreach (string vmx in Directory.EnumerateFiles(folderPath, "*.vmx", opts))
            {
                string name = ParseVmxDisplayName(vmx) ?? Path.GetFileNameWithoutExtension(vmx);
                result.Add(new DiscoveredVmInfo(name, vmx, File.Exists(vmx)));
            }
        }
        catch
        {
            // Ignore unreadable directories.
        }

        return result;
    }

    public static string? ParseVmxDisplayName(string vmxPath)
    {
        try
        {
            if (!File.Exists(vmxPath)) return null;
            foreach (string line in File.ReadLines(vmxPath))
            {
                var match = VmxDisplayNameRegex().Match(line.Trim());
                if (match.Success)
                {
                    string val = match.Groups["val"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
        }
        catch
        {
            // Fall back to filename.
        }

        return null;
    }

    public static HashSet<string> ParseVmrunListOutput(string output)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output)) return running;

        foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Total running VMs", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase))
            {
                running.Add(line);
            }
        }

        return running;
    }

    public HashSet<string> GetRunningVmxPaths(string? configuredVmrunPath = null)
    {
        string? vmrun = ResolveVmrunPath(configuredVmrunPath);
        if (vmrun is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var (exitCode, stdOut, _) = _commandRunner(vmrun, "list", 8000);
            if (exitCode == 0)
            {
                return ParseVmrunListOutput(stdOut);
            }
        }
        catch
        {
            // Return empty set if vmrun list fails.
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public VmLaunchResult StartVm(
        VmwareVmEntry vm,
        string? configuredVmrunPath = null,
        HashSet<string>? runningVms = null)
    {
        string? vmrun = ResolveVmrunPath(configuredVmrunPath);
        if (vmrun is null)
        {
            const string msg = "Không tìm thấy vmrun.exe (hãy kiểm tra cài đặt VMware Workstation).";
            _logger.Error(LogCategory.Vmware, $"[{vm.Name}] {msg}");
            return new VmLaunchResult(vm, VmLaunchOutcome.Failed, msg);
        }

        if (string.IsNullOrWhiteSpace(vm.VmxPath) || !File.Exists(vm.VmxPath))
        {
            string msg = $"Không tìm thấy file .vmx: {vm.VmxPath}";
            _logger.Error(LogCategory.Vmware, $"[{vm.Name}] {msg}");
            return new VmLaunchResult(vm, VmLaunchOutcome.Failed, msg);
        }

        runningVms ??= GetRunningVmxPaths(vmrun);
        if (runningVms.Contains(vm.VmxPath))
        {
            const string msg = "Máy ảo đang chạy sẵn, bỏ qua.";
            _logger.Info(LogCategory.Vmware, $"[{vm.Name}] {msg}");
            return new VmLaunchResult(vm, VmLaunchOutcome.SkippedAlreadyRunning, msg);
        }

        string modeArg = vm.Headless ? "nogui" : "gui";
        string args = $"-T ws start \"{vm.VmxPath}\" {modeArg}";

        try
        {
            var (exitCode, stdOut, stdErr) = _commandRunner(vmrun, args, 45000);
            if (exitCode == 0)
            {
                runningVms.Add(vm.VmxPath);
                string modeLabel = vm.Headless ? "chạy ngầm nogui" : "mở cửa sổ GUI";
                string msg = $"Đã khởi động ({modeLabel})";
                _logger.Success(LogCategory.Vmware, $"[{vm.Name}] {msg}");
                return new VmLaunchResult(vm, VmLaunchOutcome.Started, msg);
            }

            string errDetail = FirstNonEmpty(stdErr, stdOut, $"Mã thoát {exitCode}");
            _logger.Error(LogCategory.Vmware, $"[{vm.Name}] Lỗi khởi động: {errDetail}");
            return new VmLaunchResult(vm, VmLaunchOutcome.Failed, errDetail);
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.Vmware, $"[{vm.Name}] Lỗi: {ex.Message}");
            return new VmLaunchResult(vm, VmLaunchOutcome.Failed, ex.Message);
        }
    }

    public (bool Success, string Detail) StopVm(
        VmwareVmEntry vm,
        string? configuredVmrunPath = null,
        bool hard = false)
    {
        string? vmrun = ResolveVmrunPath(configuredVmrunPath);
        if (vmrun is null)
        {
            return (false, "Không tìm thấy vmrun.exe.");
        }

        string mode = hard ? "hard" : "soft";
        string args = $"-T ws stop \"{vm.VmxPath}\" {mode}";

        try
        {
            var (exitCode, stdOut, stdErr) = _commandRunner(vmrun, args, 30000);
            if (exitCode == 0)
            {
                string msg = $"Đã tắt máy ảo ({mode})";
                _logger.Success(LogCategory.Vmware, $"[{vm.Name}] {msg}");
                return (true, msg);
            }

            string errDetail = FirstNonEmpty(stdErr, stdOut, $"Mã thoát {exitCode}");
            _logger.Warning(LogCategory.Vmware, $"[{vm.Name}] Tắt máy ảo thất bại: {errDetail}");
            return (false, errDetail);
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.Vmware, $"[{vm.Name}] Lỗi tắt máy ảo: {ex.Message}");
            return (false, ex.Message);
        }
    }

    public async Task<VmBatchSummary> StartEnabledVmsAsync(
        VmwareSettings settings,
        CancellationToken cancellationToken = default)
    {
        var enabled = settings.Vms
            .Where(v => v.Enabled)
            .OrderBy(v => v.Order)
            .ToList();

        if (!settings.AutoStartEnabled || enabled.Count == 0)
        {
            return new VmBatchSummary(0, 0, 0, Array.Empty<VmLaunchResult>());
        }

        var runningVms = await Task.Run(() => GetRunningVmxPaths(settings.VmrunPath), cancellationToken)
            .ConfigureAwait(false);

        var results = new List<VmLaunchResult>(enabled.Count);
        int started = 0, skipped = 0, failed = 0;

        foreach (var vm in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (vm.DelaySeconds > 0)
            {
                _logger.Info(LogCategory.Vmware, $"[{vm.Name}] Chờ {vm.DelaySeconds}s trước khi khởi động máy ảo...");
                await Task.Delay(TimeSpan.FromSeconds(vm.DelaySeconds), cancellationToken).ConfigureAwait(false);
            }

            var res = await Task.Run(() => StartVm(vm, settings.VmrunPath, runningVms), cancellationToken)
                .ConfigureAwait(false);

            results.Add(res);
            switch (res.Outcome)
            {
                case VmLaunchOutcome.Started: started++; break;
                case VmLaunchOutcome.SkippedAlreadyRunning: skipped++; break;
                case VmLaunchOutcome.Failed: failed++; break;
            }
        }

        return new VmBatchSummary(started, skipped, failed, results);
    }

    public void StopManagedVmsOnShutdown(VmwareSettings settings)
    {
        if (!settings.StopVmsOnShutdown || settings.Vms.Count == 0) return;

        string? vmrun = ResolveVmrunPath(settings.VmrunPath);
        if (vmrun is null) return;

        var running = GetRunningVmxPaths(vmrun);
        if (running.Count == 0) return;

        foreach (var vm in settings.Vms.Where(v => v.Enabled && running.Contains(v.VmxPath)))
        {
            try
            {
                _commandRunner(vmrun, $"-T ws stop \"{vm.VmxPath}\" soft", 4000);
                _logger.Info(LogCategory.Vmware, $"[{vm.Name}] Đã gửi lệnh soft-stop khi tắt Windows.");
            }
            catch
            {
                // Best-effort during shutdown.
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(string fileName, string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"vmrun quá thời gian chờ ({timeoutMs}ms).");
        }

        string stdOut = stdOutTask.GetAwaiter().GetResult();
        string stdErr = stdErrTask.GetAwaiter().GetResult();
        return (process.ExitCode, stdOut, stdErr);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    [GeneratedRegex(@"^(?<id>vmlist\d+)\.(?<field>config|DisplayName)\s*=\s*""(?<val>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex InventoryLineRegex();

    [GeneratedRegex(@"^displayName\s*=\s*""(?<val>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex VmxDisplayNameRegex();
}
