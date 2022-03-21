using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;

namespace Microwalk;

/// <summary>
/// Utility class for keeping track of the process resource usage.
/// </summary>
internal class ProcessMonitor : IDisposable
{
    private readonly ILogger _logger;

    private readonly Timer _timer;

    private readonly Process _thisProcess;

    private long _maxMemoryUsage = 0;

    /// <summary>
    /// Starts a new process monitor with the given configuration.
    /// </summary>
    /// <param name="configuration">Process monitor configuration.</param>
    /// <param name="logger">Logger.</param>
    internal ProcessMonitor(MappingNode configuration, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Get process info
        _thisProcess = Process.GetCurrentProcess();

        // Read configuration
        int sampleRate = configuration.GetChildNodeOrDefault("sample-rate")?.AsInteger() ?? 500;

        // Start timer
        _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(sampleRate));
    }

    /// <summary>
    /// Queries current resource usage and stores the result.
    /// </summary>
    /// <param name="state">Ignored.</param>
    private void Update(object? state)
    {
        // Refresh information
        _thisProcess.Refresh();

        // Memory usage
        if(_thisProcess.PrivateMemorySize64 > _maxMemoryUsage)
            _maxMemoryUsage = _thisProcess.PrivateMemorySize64;
    }

    /// <summary>
    /// Ends data gathering and prints the results.
    /// </summary>
    public async Task ConcludeAsync()
    {
        // Clean up
        Dispose();

        // Print results
        await _logger.LogInfoAsync($"[monitor] Maximum private memory size: {_maxMemoryUsage} bytes ({(double)_maxMemoryUsage / (1024 * 1024):N3} MB)");
    }

    public void Dispose()
    {
        _timer.Dispose();
        _thisProcess.Dispose();
    }
}