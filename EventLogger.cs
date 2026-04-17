using System.Collections.Concurrent;

namespace IndustrialProcessingSystem
{
    /// <summary>
    /// Asynchronously logs job events to a file
    /// </summary>
    public class EventLogger : IDisposable
    {
        private readonly string _logFilePath;
        private readonly ConcurrentQueue<string> _logQueue;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _loggerTask;
        private readonly SemaphoreSlim _itemAvailable;

        public EventLogger(string logFilePath = "job_events.log")
        {
            _logFilePath = logFilePath;
            _logQueue = new ConcurrentQueue<string>();
            _itemAvailable = new SemaphoreSlim(0);
            _cancellationTokenSource = new CancellationTokenSource();

            // Ensure the file exists
            if (!File.Exists(_logFilePath))
                File.Create(_logFilePath).Close();

            _loggerTask = Task.Run(async () => await LoggerLoop());
        }

        /// <summary>
        /// Logs a job completion event
        /// </summary>
        public void LogJobCompleted(Guid jobId, int result, JobType jobType)
        {
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [COMPLETED] JobId: {jobId}, Type: {jobType}, Result: {result}";
            _logQueue.Enqueue(logEntry);
            _itemAvailable.Release();
        }

        /// <summary>
        /// Logs a job failure event
        /// </summary>
        public void LogJobFailed(Guid jobId, string reason, JobType jobType, int attempts)
        {
            var status = attempts > 2 ? "ABORT" : "FAILED";
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{status}] JobId: {jobId}, Type: {jobType}, Reason: {reason}, Attempts: {attempts}";
            _logQueue.Enqueue(logEntry);
            _itemAvailable.Release();
        }

        private async Task LoggerLoop()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await _itemAvailable.WaitAsync(_cancellationTokenSource.Token);

                    if (_logQueue.TryDequeue(out var logEntry))
                    {
                        await File.AppendAllTextAsync(_logFilePath, logEntry + Environment.NewLine);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            // Flush remaining logs
            while (_logQueue.TryDequeue(out var logEntry))
            {
                await File.AppendAllTextAsync(_logFilePath, logEntry + Environment.NewLine);
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _loggerTask?.Wait(TimeSpan.FromSeconds(5));
            _cancellationTokenSource?.Dispose();
            _itemAvailable?.Dispose();
        }
    }
}
