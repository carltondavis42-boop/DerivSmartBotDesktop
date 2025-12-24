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

        public event Action<bool>? TrainingCompleted;
        public event Action<string, bool>? StatusChanged;
        public bool IsAvailable { get; private set; }
        public string LastStatus { get; private set; } = string.Empty;

        public AutoTrainingService(string scriptPath, int tradesPerTrain, Action<string> log)
        {
            _scriptPath = scriptPath;
            _tradesPerTrain = tradesPerTrain;
            _log = log;
            _ = CheckDependenciesAsync();
        }

        public void TrainNow()
        {
            if (!IsAvailable)
            {
                UpdateStatus("[AutoTrain] Not available. Check Python deps.", false);
                return;
            }

            _ = RunTrainingAsync(force: true);
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
            _ = Task.Run(() => RunTrainingAsync(force: false));
        }

        private async Task RunTrainingAsync(bool force)
        {
            try
            {
                if (!File.Exists(_scriptPath))
                {
                    UpdateStatus($"[AutoTrain] Script not found: {_scriptPath}", false);
                    return;
                }

                if (!IsAvailable)
                {
                    UpdateStatus("[AutoTrain] Not available. Check Python deps.", false);
                    return;
                }

                UpdateStatus("[AutoTrain] Starting training...", IsAvailable);

                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = force ? $"\"{_scriptPath}\" --force" : $"\"{_scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    UpdateStatus("[AutoTrain] Failed to start python process.", false);
                    return;
                }

                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                var updated = !stdout.Contains("No improvement", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(stdout))
                    _log("[AutoTrain] " + stdout.Trim());
                if (!string.IsNullOrWhiteSpace(stderr))
                    _log("[AutoTrain][ERR] " + stderr.Trim());

                UpdateStatus(updated ? "[AutoTrain] Model updated." : "[AutoTrain] No improvement.", IsAvailable);
                TrainingCompleted?.Invoke(updated);
            }
            catch (Exception ex)
            {
                UpdateStatus($"[AutoTrain] Failed: {ex.Message}", false);
                TrainingCompleted?.Invoke(false);
            }
            finally
            {
                Interlocked.Exchange(ref _isRunning, 0);
            }
        }

        private async Task CheckDependenciesAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-c \"import pandas, sklearn\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    UpdateStatus("[AutoTrain] Python not available.", false);
                    return;
                }

                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode == 0)
                {
                    UpdateStatus("[AutoTrain] Ready", true);
                    return;
                }

                var err = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                UpdateStatus("[AutoTrain] Missing deps: " + err.Trim(), false);
            }
            catch (Exception ex)
            {
                UpdateStatus($"[AutoTrain] Dependency check failed: {ex.Message}", false);
            }
        }

        private void UpdateStatus(string status, bool available)
        {
            LastStatus = status;
            IsAvailable = available;
            _log(status);
            StatusChanged?.Invoke(status, available);
        }
    }
}
