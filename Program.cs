using System.Collections.Concurrent;

namespace IndustrialProcessingSystem
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string configPath = "SystemConfig.xml";

            // Load configuration
            var (workerCount, maxQueueSize, initialJobs) = ConfigurationLoader.LoadConfiguration(configPath);

            Console.WriteLine($"[STARTUP] Loading configuration from {configPath}");
            Console.WriteLine($"[STARTUP] Worker threads: {workerCount}");
            Console.WriteLine($"[STARTUP] Max queue size: {maxQueueSize}");
            Console.WriteLine($"[STARTUP] Initial jobs to load: {initialJobs.Count}");
            Console.WriteLine();

            // Create processing system
            var processingSystem = new ProcessingSystem(workerCount, maxQueueSize);

            // Create event logger
            var eventLogger = new EventLogger("job_events.log");

            // Subscribe to events
            processingSystem.JobCompleted += (sender, e) =>
            {
                Console.WriteLine($"[COMPLETED] Job {e.JobId} ({e.JobType}): Result={e.Result}, Time={e.ExecutionTime.TotalMilliseconds}ms");
                eventLogger.LogJobCompleted(e.JobId, e.Result, e.JobType);
            };

            processingSystem.JobFailed += (sender, e) =>
            {
                Console.WriteLine($"[FAILED] Job {e.JobId} ({e.JobType}): {e.Reason}, Attempts={e.Attempts}");
                eventLogger.LogJobFailed(e.JobId, e.Reason, e.JobType, e.Attempts);
            };

            // Create report generator
            var reportGenerator = new ReportGenerator(processingSystem);
            processingSystem.JobCompleted += reportGenerator.OnJobCompleted;
            processingSystem.JobFailed += reportGenerator.OnJobFailed;

            // Submit initial jobs from configuration
            Console.WriteLine("[INFO] Submitting initial jobs from configuration...");
            foreach (var job in initialJobs)
            {
                try
                {
                    var handle = processingSystem.Submit(job);
                    Console.WriteLine($"[SUBMITTED] Job {job.Id} ({job.Type}), Priority: {job.Priority}");
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine($"[ERROR] Failed to submit job: {ex.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("[INFO] Starting producer threads...");

            // Create producer threads that randomly add new jobs
            var producerCount = Math.Max(2, workerCount / 2);
            var producerTasks = new List<Task>();
            var producerCts = new CancellationTokenSource();

            for (int i = 0; i < producerCount; i++)
            {
                var producerTask = ProducerLoop(
                    processingSystem,
                    i,
                    producerCts.Token
                );
                producerTasks.Add(producerTask);
            }

            Console.WriteLine($"[INFO] Started {producerCount} producer threads");
            Console.WriteLine();
            Console.WriteLine("Press ENTER to stop the system...");
            Console.ReadLine();

            Console.WriteLine("\n[SHUTDOWN] Stopping producers...");
            producerCts.Cancel();
            Task.WaitAll(producerTasks.ToArray(), TimeSpan.FromSeconds(10));

            Console.WriteLine("[SHUTDOWN] Waiting for remaining jobs to complete...");
            await Task.Delay(5000); // Give some time for jobs to complete

            Console.WriteLine("[SHUTDOWN] Disposing resources...");
            reportGenerator.Dispose();
            eventLogger.Dispose();
            processingSystem.Dispose();

            Console.WriteLine("[SHUTDOWN] System shutdown complete");
        }

        static async Task ProducerLoop(ProcessingSystem processingSystem, int producerId, CancellationToken cancellationToken)
        {
            var random = new Random(producerId);
            var jobTypes = Enum.GetValues(typeof(JobType)).Cast<JobType>().ToArray();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait random time between 500ms and 2000ms
                    await Task.Delay(random.Next(500, 2000), cancellationToken);

                    var jobType = jobTypes[random.Next(jobTypes.Length)];
                    var priority = random.Next(1, 5);
                    var job = GenerateRandomJob(jobType, random);
                    job.Priority = priority;

                    var handle = processingSystem.Submit(job);
                    Console.WriteLine($"[PRODUCER-{producerId}] Generated job {job.Id} ({job.Type}), Priority: {priority}");
                }
                catch (InvalidOperationException)
                {
                    // Queue is full, wait and retry
                    await Task.Delay(1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PRODUCER-{producerId}] Error: {ex.Message}");
                }
            }
        }

        static Job GenerateRandomJob(JobType jobType, Random random)
        {
            return jobType switch
            {
                JobType.Prime =>
                    new Job(
                        JobType.Prime,
                        $"numbers:{random.Next(5000, 50000)},threads:{random.Next(1, 8)}",
                        0
                    ),
                JobType.IO =>
                    new Job(
                        JobType.IO,
                        $"delay:{random.Next(100, 2000)}",
                        0
                    ),
                _ => throw new ArgumentException($"Unknown job type: {jobType}")
            };
        }
    }
}
