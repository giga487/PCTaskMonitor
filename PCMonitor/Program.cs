using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace PCMonitor;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        builder.WebHost.ConfigureKestrel((context, options) =>
        {
            options.Configure(context.Configuration.GetSection("Kestrel"));
        });

        builder.Services.Configure<MonitoringSettings>(builder.Configuration.GetSection("Monitoring"));
        builder.Services.Configure<LogStaticFilesSettings>(builder.Configuration.GetSection("LogStaticFiles"));
        builder.Services.AddDirectoryBrowser();
        builder.Services.AddHostedService<TaskMonitor>();

        ConfigureLogging(ReadLoggingSettings(builder.Configuration.GetSection("AppLogging")));

        try
        {
            await using WebApplication app = builder.Build();
            MapLogStaticFiles(
                app,
                ReadLoggingSettings(builder.Configuration.GetSection("AppLogging")),
                ReadLogStaticFilesSettings(builder.Configuration.GetSection("LogStaticFiles")));

            await app.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Information("PCMonitor stopped by user.");
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "PCMonitor stopped because of an unexpected error.");
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static LoggingSettings ReadLoggingSettings(IConfiguration configuration)
    {
        return configuration.Get<LoggingSettings>() ?? new LoggingSettings();
    }

    private static LogStaticFilesSettings ReadLogStaticFilesSettings(IConfiguration configuration)
    {
        return configuration.Get<LogStaticFilesSettings>() ?? new LogStaticFilesSettings();
    }

    private static void ConfigureLogging(LoggingSettings settings)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(settings.GetMinimumLevel())
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                settings.FilePath,
                rollingInterval: settings.GetRollingInterval(),
                fileSizeLimitBytes: settings.GetFileSizeLimitBytes(),
                retainedFileTimeLimit: settings.GetRetainedFileTimeLimit(),
                rollOnFileSizeLimit: true,
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
    }

    private static void MapLogStaticFiles(
        WebApplication app,
        LoggingSettings loggingSettings,
        LogStaticFilesSettings settings)
    {
        if (!settings.Enabled)
        {
            return;
        }

        string logDirectoryPath = settings.GetDirectoryPath(loggingSettings);
        string fullLogDirectoryPath = Path.GetFullPath(
            Path.IsPathRooted(logDirectoryPath)
                ? logDirectoryPath
                : Path.Combine(app.Environment.ContentRootPath, logDirectoryPath));

        Directory.CreateDirectory(fullLogDirectoryPath);

        var fileProvider = new PhysicalFileProvider(fullLogDirectoryPath);
        var contentTypeProvider = new FileExtensionContentTypeProvider();
        contentTypeProvider.Mappings[".log"] = "text/plain";

        var staticFileOptions = new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = settings.GetRequestPath(),
            ContentTypeProvider = contentTypeProvider
        };

        app.UseStaticFiles(staticFileOptions);

        if (settings.EnableDirectoryBrowsing)
        {
            app.UseDirectoryBrowser(new DirectoryBrowserOptions
            {
                FileProvider = fileProvider,
                RequestPath = settings.GetRequestPath()
            });
        }

        Log.Information(
            "Log static files enabled. Directory={LogDirectory}. RequestPath={RequestPath}. DirectoryBrowsing={DirectoryBrowsing}",
            fullLogDirectoryPath,
            settings.GetRequestPath(),
            settings.EnableDirectoryBrowsing);
    }
}

internal sealed class TaskMonitor(IOptionsMonitor<MonitoringSettings> optionsMonitor) : BackgroundService
{
    private readonly Dictionary<int, ProcessSample> _processSamples = [];
    private readonly Dictionary<string, NetworkSample> _networkSamples = [];
    private readonly Dictionary<string, TrackedTaskSample> _trackedTaskSamples = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable? _settingsChangeRegistration = optionsMonitor.OnChange(LogSettingsReloaded);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogSettingsReloaded(optionsMonitor.CurrentValue);

        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();

            MonitoringSettings settings = optionsMonitor.CurrentValue;
            DateTimeOffset sampleTime = DateTimeOffset.UtcNow;

            CheckProcesses(sampleTime, settings);

            if (settings.Network.Enabled)
            {
                CheckNetworkInterfaces(sampleTime, settings);
            }

            await Task.Delay(settings.GetSampleInterval(), stoppingToken).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _settingsChangeRegistration?.Dispose();
        base.Dispose();
    }

    private static void LogSettingsReloaded(MonitoringSettings settings)
    {
        Log.Information(
            "Monitoring settings loaded. Process CPU threshold: {CpuThreshold:P2}. Total CPU threshold: {TotalCpuThreshold:P2}. Top CPU processes: {TopProcessCount}. Tracked task names: {TrackedTaskNames}. Network total threshold: {NetworkThreshold} B/s. Sample interval: {IntervalSeconds}s",
            settings.ProcessCpuThresholdPercent / 100,
            settings.TotalCpuThresholdPercent / 100,
            settings.GetTopCpuProcessesCount(),
            string.Join(", ", settings.TrackedTasks.GetNormalizedNames()),
            settings.Network.TotalBytesPerSecondThreshold,
            settings.GetSampleInterval().TotalSeconds);
    }

    private void CheckProcesses(DateTimeOffset sampleTime, MonitoringSettings settings)
    {
        HashSet<int> observedProcessIds = [];
        List<ProcessCpuUsage> currentCpuUsages = [];
        List<TrackedProcessUsage> trackedTaskUsages = [];
        double totalCpuSeconds = 0;
        double longestElapsedSeconds = 0;
        HashSet<string> trackedTaskNames = settings.TrackedTasks.GetNormalizedNameSet();

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                ProcessSnapshot? snapshot = TryReadProcess(process);
                if (snapshot is null)
                {
                    continue;
                }

                observedProcessIds.Add(snapshot.ProcessId);

                if (!_processSamples.TryGetValue(snapshot.ProcessId, out ProcessSample? previous))
                {
                    _processSamples[snapshot.ProcessId] = new ProcessSample(snapshot.TotalProcessorTime, sampleTime);
                    AddTrackedTaskUsage(settings, trackedTaskNames, trackedTaskUsages, snapshot, cpuPercent: 0);
                    continue;
                }

                double elapsedSeconds = Math.Max(0.001, (sampleTime - previous.SampleTime).TotalSeconds);
                double cpuSeconds = Math.Max(0, (snapshot.TotalProcessorTime - previous.TotalProcessorTime).TotalSeconds);
                double cpuPercent = cpuSeconds / elapsedSeconds / Environment.ProcessorCount * 100;
                totalCpuSeconds += cpuSeconds;
                longestElapsedSeconds = Math.Max(longestElapsedSeconds, elapsedSeconds);
                currentCpuUsages.Add(new ProcessCpuUsage(
                    snapshot.ProcessId,
                    snapshot.ProcessName,
                    cpuPercent));

                AddTrackedTaskUsage(settings, trackedTaskNames, trackedTaskUsages, snapshot, cpuPercent);

                _processSamples[snapshot.ProcessId] = new ProcessSample(snapshot.TotalProcessorTime, sampleTime);

                if (cpuPercent > settings.ProcessCpuThresholdPercent)
                {
                    Log.Warning(
                        "Process CPU threshold exceeded. Process={ProcessName} Pid={ProcessId} CpuPercent={CpuPercent:F2} ThresholdPercent={ThresholdPercent:F2}",
                        snapshot.ProcessName,
                        snapshot.ProcessId,
                        cpuPercent,
                        settings.ProcessCpuThresholdPercent);
                }
            }
        }

        CheckTotalCpuThreshold(settings, currentCpuUsages, totalCpuSeconds, longestElapsedSeconds);
        TraceConfiguredTasks(sampleTime, settings, trackedTaskUsages);
        RemoveExitedProcesses(observedProcessIds);
    }

    private static void AddTrackedTaskUsage(
        MonitoringSettings settings,
        HashSet<string> trackedTaskNames,
        List<TrackedProcessUsage> trackedTaskUsages,
        ProcessSnapshot snapshot,
        double cpuPercent)
    {
        if (!settings.TrackedTasks.Enabled || !trackedTaskNames.Contains(snapshot.ProcessName))
        {
            return;
        }

        trackedTaskUsages.Add(new TrackedProcessUsage(
            snapshot.ProcessId,
            snapshot.ProcessName,
            cpuPercent,
            snapshot.WorkingSetBytes));
    }

    private void TraceConfiguredTasks(
        DateTimeOffset sampleTime,
        MonitoringSettings settings,
        List<TrackedProcessUsage> trackedTaskUsages)
    {
        if (!settings.TrackedTasks.Enabled)
        {
            return;
        }

        List<string> taskNames = settings.TrackedTasks.GetNormalizedNames();
        if (taskNames.Count == 0)
        {
            return;
        }

        var runningTasksByName = trackedTaskUsages
            .GroupBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (string taskName in taskNames)
        {
            if (!runningTasksByName.TryGetValue(taskName, out List<TrackedProcessUsage>? runningTasks))
            {
                double cpuChangePercent = GetRelativeChangePercent(_trackedTaskSamples.GetValueOrDefault(taskName)?.CpuPercent, 0);
                double memoryChangePercent = GetRelativeChangePercent(_trackedTaskSamples.GetValueOrDefault(taskName)?.WorkingSetBytes, 0);

                Log.Information(
                    "Tracked task sample. SampleTime={SampleTime:O} TaskName={TaskName} RunningCount=0 CPU: {CpuPercent:F2}% ({CpuChangePercent:+0.##;-0.##;0}%) RAM: {MemoryMegabytes:F2} MB ({MemoryChangePercent:+0.##;-0.##;0}%)",
                    sampleTime,
                    taskName,
                    0,
                    cpuChangePercent,
                    0,
                    memoryChangePercent);
                _trackedTaskSamples[taskName] = new TrackedTaskSample(0, 0, sampleTime);
                continue;
            }

            double cpuPercent = runningTasks.Sum(process => process.CpuPercent);
            long workingSetBytes = runningTasks.Sum(process => process.WorkingSetBytes);
            _trackedTaskSamples.TryGetValue(taskName, out TrackedTaskSample? previous);
            double cpuIncrementPercent = GetRelativeChangePercent(previous?.CpuPercent, cpuPercent);
            double memoryIncrementPercent = GetRelativeChangePercent(previous?.WorkingSetBytes, workingSetBytes);

            Log.Information(
                "Tracked task sample. SampleTime={SampleTime:O} TaskName={TaskName} RunningCount={RunningCount} CPU: {CpuPercent:F2}% ({CpuIncrementPercent:+0.##;-0.##;0}%) RAM: {MemoryMegabytes:F2} MB ({MemoryIncrementPercent:+0.##;-0.##;0}%) Pids={ProcessIds}",
                sampleTime,
                taskName,
                runningTasks.Count,
                cpuPercent,
                cpuIncrementPercent,
                workingSetBytes / 1024d / 1024d,
                memoryIncrementPercent,
                string.Join(", ", runningTasks.Select(process => process.ProcessId)));

            _trackedTaskSamples[taskName] = new TrackedTaskSample(cpuPercent, workingSetBytes, sampleTime);
        }
    }

    private static double GetRelativeChangePercent(double? previousValue, double currentValue)
    {
        if (previousValue is null)
        {
            return 0;
        }

        if (Math.Abs(previousValue.Value) < double.Epsilon)
        {
            return currentValue > 0 ? 100 : 0;
        }

        return (currentValue - previousValue.Value) / previousValue.Value * 100;
    }

    private static void CheckTotalCpuThreshold(
        MonitoringSettings settings,
        List<ProcessCpuUsage> currentCpuUsages,
        double totalCpuSeconds,
        double elapsedSeconds)
    {
        if (elapsedSeconds <= 0 || currentCpuUsages.Count == 0)
        {
            return;
        }

        double totalCpuPercent = totalCpuSeconds / elapsedSeconds / Environment.ProcessorCount * 100;
        if (totalCpuPercent <= settings.TotalCpuThresholdPercent)
        {
            return;
        }

        int processCount = settings.GetTopCpuProcessesCount();
        Log.Warning(
            "Total CPU threshold exceeded. CpuPercent={CpuPercent:F2} ThresholdPercent={ThresholdPercent:F2}. Top {ProcessCount} processes by CPU:",
            totalCpuPercent,
            settings.TotalCpuThresholdPercent,
            processCount);

        foreach (ProcessCpuUsage process in currentCpuUsages
            .OrderByDescending(process => process.CpuPercent)
            .Take(processCount))
        {
            Log.Warning(
                "Top CPU process. Process={ProcessName} Pid={ProcessId} CpuPercent={CpuPercent:F2}",
                process.ProcessName,
                process.ProcessId,
                process.CpuPercent);
        }
    }

    private void CheckNetworkInterfaces(DateTimeOffset sampleTime, MonitoringSettings settings)
    {
        HashSet<string> observedInterfaceIds = [];

        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!ShouldMonitor(settings, networkInterface))
            {
                continue;
            }

            IPv4InterfaceStatistics statistics;
            try
            {
                statistics = networkInterface.GetIPv4Statistics();
            }
            catch (NetworkInformationException exception)
            {
                Log.Debug(exception, "Cannot read network statistics for interface {InterfaceName}.", networkInterface.Name);
                continue;
            }

            observedInterfaceIds.Add(networkInterface.Id);

            var current = new NetworkSample(
                statistics.BytesReceived,
                statistics.BytesSent,
                sampleTime);

            if (!_networkSamples.TryGetValue(networkInterface.Id, out NetworkSample? previous))
            {
                _networkSamples[networkInterface.Id] = current;
                continue;
            }

            double elapsedSeconds = Math.Max(0.001, (sampleTime - previous.SampleTime).TotalSeconds);
            double receivedPerSecond = Math.Max(0, current.BytesReceived - previous.BytesReceived) / elapsedSeconds;
            double sentPerSecond = Math.Max(0, current.BytesSent - previous.BytesSent) / elapsedSeconds;
            double totalPerSecond = receivedPerSecond + sentPerSecond;

            _networkSamples[networkInterface.Id] = current;

            if (IsOverNetworkThreshold(settings, receivedPerSecond, sentPerSecond, totalPerSecond))
            {
                Log.Warning(
                    "Network threshold exceeded. Interface={InterfaceName} Id={InterfaceId} ReceivedBps={ReceivedBps:F0} SentBps={SentBps:F0} TotalBps={TotalBps:F0}",
                    networkInterface.Name,
                    networkInterface.Id,
                    receivedPerSecond,
                    sentPerSecond,
                    totalPerSecond);
            }
        }

        RemoveUnavailableNetworkInterfaces(observedInterfaceIds);
    }

    private static ProcessSnapshot? TryReadProcess(Process process)
    {
        try
        {
            return new ProcessSnapshot(
                process.Id,
                process.ProcessName,
                process.TotalProcessorTime,
                process.WorkingSet64);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Log.Debug(exception, "Cannot read process information.");
            return null;
        }
    }

    private static bool ShouldMonitor(MonitoringSettings settings, NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        if (settings.Network.InterfaceNameContains.Count == 0)
        {
            return true;
        }

        return settings.Network.InterfaceNameContains.Any(value =>
            networkInterface.Name.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            networkInterface.Description.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOverNetworkThreshold(
        MonitoringSettings settings,
        double receivedPerSecond,
        double sentPerSecond,
        double totalPerSecond)
    {
        return receivedPerSecond > settings.Network.ReceivedBytesPerSecondThreshold ||
            sentPerSecond > settings.Network.SentBytesPerSecondThreshold ||
            totalPerSecond > settings.Network.TotalBytesPerSecondThreshold;
    }

    private void RemoveExitedProcesses(HashSet<int> observedProcessIds)
    {
        foreach (int processId in _processSamples.Keys.Except(observedProcessIds).ToArray())
        {
            _processSamples.Remove(processId);
        }
    }

    private void RemoveUnavailableNetworkInterfaces(HashSet<string> observedInterfaceIds)
    {
        foreach (string interfaceId in _networkSamples.Keys.Except(observedInterfaceIds).ToArray())
        {
            _networkSamples.Remove(interfaceId);
        }
    }
}

internal sealed record ProcessSnapshot(
    int ProcessId,
    string ProcessName,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes);

internal sealed record ProcessCpuUsage(
    int ProcessId,
    string ProcessName,
    double CpuPercent);

internal sealed record TrackedProcessUsage(
    int ProcessId,
    string ProcessName,
    double CpuPercent,
    long WorkingSetBytes);

internal sealed record ProcessSample(TimeSpan TotalProcessorTime, DateTimeOffset SampleTime);

internal sealed record NetworkSample(long BytesReceived, long BytesSent, DateTimeOffset SampleTime);

internal sealed record TrackedTaskSample(double CpuPercent, long WorkingSetBytes, DateTimeOffset SampleTime);

internal sealed class MonitoringSettings
{
    public int SampleIntervalSeconds { get; init; } = 5;

    public TimeSpan GetSampleInterval()
    {
        return TimeSpan.FromSeconds(Math.Max(1, SampleIntervalSeconds));
    }

    public double ProcessCpuThresholdPercent { get; init; } = 25.0;

    public double TotalCpuThresholdPercent { get; init; } = 80.0;

    public int TopCpuProcessesCount { get; init; } = 5;

    public int GetTopCpuProcessesCount()
    {
        return Math.Max(1, TopCpuProcessesCount);
    }

    public NetworkMonitoringSettings Network { get; init; } = new();

    public TrackedTasksSettings TrackedTasks { get; init; } = new();
}

internal sealed class NetworkMonitoringSettings
{
    public bool Enabled { get; init; } = true;

    public List<string> InterfaceNameContains { get; init; } = [];

    public double ReceivedBytesPerSecondThreshold { get; init; } = 10 * 1024 * 1024;

    public double SentBytesPerSecondThreshold { get; init; } = 10 * 1024 * 1024;

    public double TotalBytesPerSecondThreshold { get; init; } = 20 * 1024 * 1024;
}

internal sealed class TrackedTasksSettings
{
    public bool Enabled { get; init; }

    public List<string> Names { get; init; } = [];

    public List<string> GetNormalizedNames()
    {
        return Names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public HashSet<string> GetNormalizedNameSet()
    {
        return GetNormalizedNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class LoggingSettings
{
    public string MinimumLevel { get; init; } = "Information";

    public string FilePath { get; init; } = "logs/pcmonitor-.log";

    public string RollingInterval { get; init; } = "Day";

    public int RetainedDays { get; init; } = 30;

    public long FileSizeLimitBytes { get; init; } = 10 * 1024 * 1024;

    public LogEventLevel GetMinimumLevel()
    {
        return Enum.TryParse(MinimumLevel, ignoreCase: true, out LogEventLevel level)
            ? level
            : LogEventLevel.Information;
    }

    public RollingInterval GetRollingInterval()
    {
        return Enum.TryParse(RollingInterval, ignoreCase: true, out RollingInterval interval)
            ? interval
            : Serilog.RollingInterval.Day;
    }

    public TimeSpan GetRetainedFileTimeLimit()
    {
        return TimeSpan.FromDays(RetainedDays > 0 ? RetainedDays : 30);
    }

    public long GetFileSizeLimitBytes()
    {
        return FileSizeLimitBytes > 0 ? FileSizeLimitBytes : 10 * 1024 * 1024;
    }
}

internal sealed class LogStaticFilesSettings
{
    public bool Enabled { get; init; } = true;

    public string RequestPath { get; init; } = "/logs";

    public string? DirectoryPath { get; init; }

    public bool EnableDirectoryBrowsing { get; init; } = true;

    public string GetRequestPath()
    {
        if (string.IsNullOrWhiteSpace(RequestPath))
        {
            return "/logs";
        }

        return RequestPath.StartsWith('/')
            ? RequestPath
            : $"/{RequestPath}";
    }

    public string GetDirectoryPath(LoggingSettings loggingSettings)
    {
        if (!string.IsNullOrWhiteSpace(DirectoryPath))
        {
            return DirectoryPath;
        }

        string? logDirectoryPath = Path.GetDirectoryName(loggingSettings.FilePath);
        return string.IsNullOrWhiteSpace(logDirectoryPath) ? "." : logDirectoryPath;
    }
}
