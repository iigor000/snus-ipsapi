using System.Collections.Concurrent;
using System.Xml.Linq;

namespace IndustrialProcessingSystem
{
    /// <summary>
    /// Generates and manages periodic reports about job processing
    /// </summary>
    public class ReportGenerator : IDisposable
    {
        private readonly ProcessingSystem _processingSystem;
        private readonly List<Job> _allJobs;
        private readonly ConcurrentDictionary<Guid, JobCompletedEventArgs> _completedJobs;
        private readonly ConcurrentDictionary<Guid, JobFailedEventArgs> _failedJobs;
        private readonly string _reportDirectory;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _reportTask;
        private int _reportCount;

        public ReportGenerator(ProcessingSystem processingSystem, string reportDirectory = "reports")
        {
            _processingSystem = processingSystem;
            _reportDirectory = reportDirectory;
            _completedJobs = new ConcurrentDictionary<Guid, JobCompletedEventArgs>();
            _failedJobs = new ConcurrentDictionary<Guid, JobFailedEventArgs>();
            _allJobs = new List<Job>();
            _reportCount = 0;
            _cancellationTokenSource = new CancellationTokenSource();

            if (!Directory.Exists(_reportDirectory))
                Directory.CreateDirectory(_reportDirectory);

            _reportTask = Task.Run(async () => await ReportLoop());
        }

        /// <summary>
        /// Registers job completion event
        /// </summary>
        public void OnJobCompleted(object? sender, JobCompletedEventArgs e)
        {
            _completedJobs.TryAdd(e.JobId, e);
        }

        /// <summary>
        /// Registers job failure event
        /// </summary>
        public void OnJobFailed(object? sender, JobFailedEventArgs e)
        {
            _failedJobs.TryAdd(e.JobId, e);
        }

        private async Task ReportLoop()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Wait for 1 minute
                    await Task.Delay(60000, _cancellationTokenSource.Token);

                    GenerateReport();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void GenerateReport()
        {
            try
            {
                // LINQ queries for report generation
                var completedByType = _completedJobs.Values
                    .GroupBy(j => j.JobType)
                    .Select(g => new
                    {
                        JobType = g.Key,
                        Count = g.Count(),
                        AvgExecutionTime = g.Average(j => j.ExecutionTime.TotalMilliseconds)
                    })
                    .ToList();

                var failedByType = _failedJobs.Values
                    .GroupBy(j => j.JobType)
                    .OrderBy(g => g.Key.ToString())
                    .Select(g => new
                    {
                        JobType = g.Key,
                        Count = g.Count()
                    })
                    .ToList();

                // Create XML report
                var report = new XDocument(
                    new XElement("Report",
                        new XAttribute("GeneratedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                        new XElement("CompletedJobs",
                            completedByType.Select(item =>
                                new XElement("JobType",
                                    new XAttribute("Type", item.JobType),
                                    new XAttribute("Count", item.Count),
                                    new XAttribute("AvgExecutionTimeMs", Math.Round(item.AvgExecutionTime, 2))
                                )
                            )
                        ),
                        new XElement("FailedJobs",
                            failedByType.Select(item =>
                                new XElement("JobType",
                                    new XAttribute("Type", item.JobType),
                                    new XAttribute("Count", item.Count)
                                )
                            )
                        )
                    )
                );

                // Save report with rotation (keep last 10)
                SaveReportWithRotation(report);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Report generation error: {ex.Message}");
            }
        }

        private void SaveReportWithRotation(XDocument report)
        {
            _reportCount++;

            // Calculate which file to overwrite (cycling through 1-10)
            int fileIndex = ((_reportCount - 1) % 10) + 1;
            var reportPath = Path.Combine(_reportDirectory, $"report_{fileIndex:D2}.xml");

            report.Save(reportPath);
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _reportTask?.Wait(TimeSpan.FromSeconds(5));
            _cancellationTokenSource?.Dispose();
        }
    }
}
