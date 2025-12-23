using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DerivSmartBotDesktop.Services
{
    public sealed class AutoTrainingService
    {
        private readonly string _scriptPath;
        private readonly int _tradesPerTrain;
        private readonly Action<string> _log;
        private int _lastTrainedTradeCount;
        private int _isRunning;

        public AutoTrainingService(string scriptPath, int tradesPerTrain, Action<string> log)
        {
            _scriptPath = scriptPath;
            _tradesPerTrain = tradesPerTrain;
            _log = log;
        }

        public void TryQueueTraining(int totalTrades)
        {
            if (totalTrades < _tradesPerTrain)
                return;

            if (totalTrades - _lastTrainedTradeCount < _tradesPerTrain)
                return;

            if (Interlocked.Exchange(ref _isRunning, 1) == 1)
                return;

            _lastTrainedTradeCount = totalTrades;
            _ = Task.Run(RunTrainingAsync);
        }

        private async Task RunTrainingAsync()
        {
            try
            {
                if (!File.Exists(_scriptPath))
                {
                    _log($"[AutoTrain] Script not found: {_scriptPath}");
                    return;
                }

                _log("[AutoTrain] Starting training...");

                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{_scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _log("[AutoTrain] Failed to start python process.");
                    return;
                }

                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(stdout))
                    _log("[AutoTrain] " + stdout.Trim());
                if (!string.IsNullOrWhiteSpace(stderr))
                    _log("[AutoTrain][ERR] " + stderr.Trim());
            }
            catch (Exception ex)
            {
                _log($"[AutoTrain] Failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isRunning, 0);
            }
        }
    }
}
