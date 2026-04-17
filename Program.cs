namespace SnusProj;

internal static class Program
{
    private static async Task Main()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "SystemConfig.xml");
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException($"Required configuration file is missing: {configPath}");
            }

            CleanupRunArtifacts(configPath);

            var producerThreadCount = ReadProducerThreadCount(configPath);

            await using var system = new ProcessingSystem(configPath);
            using var shutdownCts = new CancellationTokenSource();
            var shutdownToken = shutdownCts.Token;

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                if (!shutdownCts.IsCancellationRequested)
                {
                    Console.WriteLine("Shutdown requested. Stopping producers...");
                    shutdownCts.Cancel();
                }
            };

            system.JobCompleted += eventArgs =>
            {
                Console.WriteLine($"COMPLETED: {eventArgs.JobId} -> {eventArgs.Result}");
                return Task.CompletedTask;
            };

            system.JobFailed += eventArgs =>
            {
                Console.WriteLine($"{eventArgs.Status}: {eventArgs.JobId} (attempt {eventArgs.Attempt})");
                return Task.CompletedTask;
            };

            var producerTasks = Enumerable.Range(0, producerThreadCount)
                .Select(index => Task.Run(async () =>
                {
                    while (!shutdownToken.IsCancellationRequested)
                    {
                        try
                        {
                            var job = CreateRandomJob();
                            var handle = system.Submit(job);
                            _ = ObserveHandleAsync(handle);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Producer {index} submit error: {ex.Message}");
                        }

                        try
                        {
                            await Task.Delay(Random.Shared.Next(20, 90), shutdownToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }, shutdownToken))
                .ToArray();

            Console.WriteLine("Producers are running. Press Ctrl+C to stop.");

            await Task.WhenAll(producerTasks);

            await system.GenerateReportAsync();

            Console.WriteLine($"Top 5 jobs currently in queue: {system.GetTopJobs(5).Count()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
        }
    }

    private static async Task ObserveHandleAsync(JobHandle handle)
    {
        try
        {
            _ = await handle.Result;
        }
        catch
        {
            // Job can fail and be aborted after retries.
        }
    }

    private static void CleanupRunArtifacts(string configPath)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        var logFilePath = Path.Combine(baseDirectory, "processing.log");
        var reportDirectory = Path.Combine(baseDirectory, "reports");

        if (File.Exists(logFilePath))
        {
            File.Delete(logFilePath);
        }

        if (Directory.Exists(reportDirectory))
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static int ReadProducerThreadCount(string configPath)
    {
        var document = System.Xml.Linq.XDocument.Load(configPath);
        var workerCountText = document.Root?.Element("WorkerCount")?.Value;

        if (!int.TryParse(workerCountText, out var parsed))
        {
            throw new InvalidOperationException("WorkerCount must be present and valid in SystemConfig.xml.");
        }

        return Math.Max(1, parsed);
    }

    private static Job CreateRandomJob()
    {
        var type = Random.Shared.Next(0, 2) == 0 ? JobType.Prime : JobType.IO;

        var payload = type switch
        {
            JobType.Prime => $"numbers:{Random.Shared.Next(20_000, 40_000)},threads:{Random.Shared.Next(1, 12)}",
            JobType.IO => $"delay:{Random.Shared.Next(100, 2_600)}",
            _ => "delay:100"
        };

        return new Job
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = payload,
            Priority = Random.Shared.Next(1, 11)
        };
    }
}