using System.Text.Json;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Metadata.Proxy.MetadataProvider.Mixed
{
    public interface IProvideAdaptiveThreshold
    {
        public void LoadConfig(string? configPath);

        public int GetDynamicThreshold(string proxyName, int aggregatedCount, int maxThreshold);

        public void UpdateMetrics(string proxyName, double responseTimeMs, int newCount, bool success);
    }

    public class AdaptiveThresholdManager : IProvideAdaptiveThreshold, IDisposable
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(AdaptiveThresholdManager));

        private string? _configFilePath;
        private bool _disposed;
        private System.Timers.Timer? _saveTimer;
        private bool _isDirty;
        private bool _unreadable;
        private readonly object _lock = new();

        public AdaptiveThresholdConfig Config { get; private set; } = new();

        public void LoadConfig(string? configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath))
            {
                // Blank = not kept: a path cleared after a load must stop the saves to it.
                lock (_lock)
                {
                    _configFilePath = null;
                    _unreadable = false;
                }
                return;
            }

            string filePath = ConfigFile(configPath);
            lock (_lock)
            {
                if (filePath == _configFilePath)
                    return;
                _configFilePath = filePath;
                _unreadable = false;

                if (File.Exists(filePath))
                {
                    try
                    {
                        Config = JsonSerializer.Deserialize<AdaptiveThresholdConfig>(File.ReadAllText(filePath)) ?? new AdaptiveThresholdConfig();
                    }
                    catch (Exception ex)
                    {
                        // Never saved over: the weights it holds may only be unreadable for now.
                        _unreadable = true;
                        Logger.Warn(ex, "Could not read the dynamic threshold from {Path}; learning afresh without saving over it", filePath);
                    }
                }

                // A first run has no file yet: saving must not wait for one to exist.
                if (_saveTimer != null)
                    return;
                _saveTimer = new System.Timers.Timer(300000) { AutoReset = true };
                _saveTimer.Elapsed += (s, e) => SaveConfig();
                _saveTimer.Start();
                AppDomain.CurrentDomain.ProcessExit += (s, e) => SaveConfig();
            }
        }

        /// <summary>Storing Path is a folder, made if missing; an existing file or a .json path is used as the file.</summary>
        public static string ConfigFile(string configPath) =>
            File.Exists(configPath) || configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? configPath
                : Path.Combine(configPath, "dynamic-threshold.json");

        private void SaveConfig()
        {
            lock (_lock)
            {
                if (!_isDirty || _configFilePath == null || _unreadable) return;
                try
                {
                    string? folder = Path.GetDirectoryName(_configFilePath);
                    if (!string.IsNullOrEmpty(folder))
                        Directory.CreateDirectory(folder);
                    // Not in place: a cut-off write would make the next load start afresh.
                    AtomicFile.Write(_configFilePath, JsonSerializer.Serialize(Config, _jsonOptions));
                    _isDirty = false;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Could not save the dynamic threshold to {Path}", _configFilePath);
                }
            }
        }

        /// <summary>
        /// Applies exponential decay to the stored metrics based on the time elapsed since the last update.
        /// </summary>
        /// <param name="metrics">The proxy metrics to decay.</param>
        private void DecayMetrics(ProxyMetrics metrics)
        {
            double hoursElapsed = (DateTime.UtcNow - metrics.LastUpdate).TotalHours;
            if (hoursElapsed > 0)
            {
                double decayFactor = Math.Exp(-Config.DecayRatePerHour * hoursElapsed);
                metrics.Calls *= decayFactor;
                metrics.Failures *= decayFactor;
                metrics.TotalResponseTime *= decayFactor;
                metrics.TotalNewResults *= decayFactor;
                metrics.LastUpdate = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Computes a dynamic threshold for a given proxy based on its historical metrics and the aggregated result count.
        /// </summary>
        /// <param name="proxyName">The name of the proxy.</param>
        /// <param name="aggregatedCount">The aggregated result count.</param>
        /// <param name="maxThreshold">The Maximal usable threshold setting; the result never exceeds it.</param>
        /// <returns>The computed dynamic threshold.</returns>
        public int GetDynamicThreshold(string proxyName, int aggregatedCount, int maxThreshold)
        {
            // Locked: searches run in parallel, and decay writes the metrics UpdateMetrics adds to.
            lock (_lock)
                return DynamicThreshold(proxyName, aggregatedCount, maxThreshold);
        }

        private int DynamicThreshold(string proxyName, int aggregatedCount, int maxThreshold)
        {
            int baseThreshold = Math.Min(aggregatedCount < 10 ? Config.BaseThresholdLowCount : aggregatedCount < 50 ? Config.BaseThresholdMediumCount : Config.BaseThresholdHighCount, maxThreshold);

            if (!Config.ProxyMetrics.TryGetValue(proxyName, out ProxyMetrics? metrics) || metrics.Calls < Config.MinCallsForReliableMetrics)
                return Math.Max(baseThreshold, 1);

            DecayMetrics(metrics);

            double failureRatio = metrics.Calls > 0 ? metrics.Failures / metrics.Calls : 0.0;
            double qualityAdjustment = 1.0 - metrics.QualityScore;
            double performanceAdjustment = 1.0 - metrics.PerformanceScore;

            // Weights can be tuned; here we use a weighted sum.
            double adjustment = (0.5 * qualityAdjustment) + (0.3 * performanceAdjustment) + (0.2 * failureRatio);
            int maxAdjustment = maxThreshold - baseThreshold;
            int dynamicThreshold = baseThreshold + (int)Math.Round(adjustment * maxAdjustment);
            return Math.Clamp(dynamicThreshold, 1, Math.Max(maxThreshold, 1));
        }

        /// <summary>
        /// Updates the metrics for a given proxy based on the latest call.
        /// </summary>
        /// <param name="proxyName">The name of the proxy.</param>
        /// <param name="responseTimeMs">The response time (in milliseconds) for the call.</param>
        /// <param name="newCount">The number of new results returned by the call.</param>
        /// <param name="success">Whether the call was considered a valid success.</param>
        public void UpdateMetrics(string proxyName, double responseTimeMs, int newCount, bool success)
        {
            lock (_lock)
            {
                if (!Config.ProxyMetrics.TryGetValue(proxyName, out ProxyMetrics? metrics))
                {
                    metrics = new ProxyMetrics();
                    Config.ProxyMetrics[proxyName] = metrics;
                }

                DecayMetrics(metrics);

                metrics.Calls++;
                metrics.TotalResponseTime += responseTimeMs;
                metrics.TotalNewResults += newCount;
                if (!success) metrics.Failures++;

                double learningRate = Config.InitialLearningRate / (1 + (Config.LearningRateDecay * metrics.Calls));

                double expectedCount = metrics.ExpectedNewResults;
                double measuredQuality = expectedCount > 0 ? Math.Min(1.0, newCount / expectedCount) : 0.0;
                metrics.QualityScore += learningRate * (measuredQuality - metrics.QualityScore);

                double measuredPerformance = 1.0;
                if (responseTimeMs > 0)
                {
                    double resultsPerSecond = newCount / (responseTimeMs / 1000.0);
                    double expectedRate = metrics.AverageResponseTime > 0 ? metrics.ExpectedNewResults / (metrics.AverageResponseTime / 1000.0) : 1.0;
                    if (expectedRate > 0)
                        measuredPerformance = Math.Min(1.0, resultsPerSecond / expectedRate);
                }
                metrics.PerformanceScore += learningRate * (measuredPerformance - metrics.PerformanceScore);
                _isDirty = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    SaveConfig();
                    if (_saveTimer != null)
                    {
                        _saveTimer.Stop();
                        _saveTimer.Dispose();
                        _saveTimer = null;
                    }
                }
                _disposed = true;
            }
        }

        ~AdaptiveThresholdManager() => Dispose(false);
    }
}