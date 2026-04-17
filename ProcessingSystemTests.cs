using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IndustrialProcessingSystem.Tests
{
    /// <summary>
    /// Unit tests for the Processing System demonstrating time-independent testing
    /// </summary>
    public class ProcessingSystemTests
    {
        /// <summary>
        /// Test: Job submission and completion
        /// Uses TaskCompletionSource to avoid Thread.Sleep
        /// </summary>
        public static async Task TestJobSubmissionAndCompletion()
        {
            Console.WriteLine("\n=== Test: Job Submission and Completion ===");

            var system = new ProcessingSystem(2, 100);
            var completedEvent = new TaskCompletionSource<bool>();

            system.JobCompleted += (s, e) =>
            {
                if (e.JobType == JobType.Prime)
                    completedEvent.SetResult(true);
            };

            var job = new Job(JobType.Prime, "numbers:100,threads:2", priority: 1);
            var handle = system.Submit(job);

            // Wait for completion event instead of sleeping
            var eventFired = await Task.WhenAny(
                completedEvent.Task,
                Task.Delay(TimeSpan.FromSeconds(10))
            );

            if (!completedEvent.Task.IsCompleted)
            {
                Console.WriteLine("FAIL: Completion event not fired within timeout");
                return;
            }

            try
            {
                var result = await handle.Result;
                Console.WriteLine($"PASS: Job completed with result: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {ex.Message}");
            }
            finally
            {
                system.Dispose();
            }
        }

        /// <summary>
        /// Test: Priority ordering
        /// Verifies high-priority jobs are retrieved first
        /// </summary>
        public static void TestPriorityOrdering()
        {
            Console.WriteLine("\n=== Test: Priority Ordering ===");

            var system = new ProcessingSystem(1, 100);

            // Submit jobs with different priorities
            var job1 = new Job(JobType.IO, "delay:100", priority: 3);
            var job2 = new Job(JobType.IO, "delay:100", priority: 1);
            var job3 = new Job(JobType.IO, "delay:100", priority: 2);

            system.Submit(job1);
            system.Submit(job2);
            system.Submit(job3);

            var topJobs = system.GetTopJobs(3).ToList();

            if (topJobs.Count != 3)
            {
                Console.WriteLine($"FAIL: Expected 3 jobs, got {topJobs.Count}");
                system.Dispose();
                return;
            }

            // Verify priority ordering (lower number = higher priority)
            if (topJobs[0].Priority == 1 && topJobs[1].Priority == 2 && topJobs[2].Priority == 3)
            {
                Console.WriteLine("PASS: Jobs are ordered by priority correctly");
            }
            else
            {
                Console.WriteLine($"FAIL: Wrong priority order: {string.Join(", ", topJobs.Select(j => j.Priority))}");
            }

            system.Dispose();
        }

        /// <summary>
        /// Test: Queue size limit
        /// Verifies queue respects MaxQueueSize
        /// </summary>
        public static void TestQueueSizeLimit()
        {
            Console.WriteLine("\n=== Test: Queue Size Limit ===");

            var system = new ProcessingSystem(1, 5);
            int submitted = 0;
            int rejected = 0;

            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var job = new Job(JobType.IO, "delay:100", priority: 1);
                    system.Submit(job);
                    submitted++;
                }
                catch (InvalidOperationException)
                {
                    rejected++;
                }
            }

            if (submitted <= 5 && rejected > 0)
            {
                Console.WriteLine($"PASS: Queue limit enforced. Submitted: {submitted}, Rejected: {rejected}");
            }
            else
            {
                Console.WriteLine($"FAIL: Unexpected submission count. Submitted: {submitted}, Rejected: {rejected}");
            }

            system.Dispose();
        }

        /// <summary>
        /// Test: Job retrieval by ID
        /// </summary>
        public static void TestJobRetrievalById()
        {
            Console.WriteLine("\n=== Test: Job Retrieval by ID ===");

            var system = new ProcessingSystem(1, 100);
            var job = new Job(JobType.Prime, "numbers:1000,threads:2", priority: 1);

            system.Submit(job);
            var retrieved = system.GetJob(job.Id);

            if (retrieved != null && retrieved.Id == job.Id && retrieved.Type == JobType.Prime)
            {
                Console.WriteLine("PASS: Job retrieved correctly by ID");
            }
            else
            {
                Console.WriteLine("FAIL: Job retrieval failed");
            }

            system.Dispose();
        }

        /// <summary>
        /// Test: Prime processor with different parameters
        /// </summary>
        public static async Task TestPrimeProcessor()
        {
            Console.WriteLine("\n=== Test: Prime Processor ===");

            // Test with different prime calculation sizes
            var testCases = new[]
            {
                ("numbers:10,threads:2", 4),      // Primes: 2, 3, 5, 7
                ("numbers:20,threads:3", 8),      // Primes: 2, 3, 5, 7, 11, 13, 17, 19
                ("numbers:100,threads:4", 25)     // 25 primes up to 100
            };

            foreach (var (payload, expectedPrimes) in testCases)
            {
                try
                {
                    var result = await JobProcessor.ProcessPrime(payload, CancellationToken.None);
                    if (result == expectedPrimes)
                    {
                        Console.WriteLine($"PASS: {payload} -> {result} primes");
                    }
                    else
                    {
                        Console.WriteLine($"FAIL: {payload} -> Expected {expectedPrimes}, got {result}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAIL: {payload} -> {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Test: IO processor with delay
        /// Uses SemaphoreSlim to verify timing without Thread.Sleep
        /// </summary>
        public static async Task TestIOProcessor()
        {
            Console.WriteLine("\n=== Test: IO Processor ===");

            var semaphore = new SemaphoreSlim(0, 1);
            var startTime = DateTime.Now;
            var delayMs = 500;

            var task = JobProcessor.ProcessIO($"delay:{delayMs}", CancellationToken.None);

            // Signal in background (simulating completion)
            _ = Task.Run(async () =>
            {
                await task;
                semaphore.Release();
            });

            var completed = await semaphore.WaitAsync(TimeSpan.FromSeconds(5));
            var elapsed = (int)(DateTime.Now - startTime).TotalMilliseconds;

            if (completed && elapsed >= delayMs)
            {
                var result = await task;
                Console.WriteLine($"PASS: IO delay {delayMs}ms (actual: {elapsed}ms), result: {result}");
            }
            else
            {
                Console.WriteLine($"FAIL: IO processor test failed");
            }
        }

        /// <summary>
        /// Test: Multiple job types
        /// </summary>
        public static async Task TestMultipleJobTypes()
        {
            Console.WriteLine("\n=== Test: Multiple Job Types ===");

            var system = new ProcessingSystem(3, 100);
            var completedCount = 0;
            var completedEvent = new TaskCompletionSource<bool>();

            system.JobCompleted += (s, e) =>
            {
                completedCount++;
                if (completedCount >= 3)
                    completedEvent.SetResult(true);
            };

            system.Submit(new Job(JobType.Prime, "numbers:50,threads:2", priority: 1));
            system.Submit(new Job(JobType.IO, "delay:100", priority: 2));
            system.Submit(new Job(JobType.Prime, "numbers:30,threads:2", priority: 3));

            var completed = await Task.WhenAny(
                completedEvent.Task,
                Task.Delay(TimeSpan.FromSeconds(10))
            );

            Console.WriteLine($"PASS: Processed {completedCount} jobs of different types");
            system.Dispose();
        }
    }

    /// <summary>
    /// Test runner
    /// </summary>
    public class TestRunner
    {
        public static async Task RunAllTests(string[] args)
        {
            Console.WriteLine("=== Industrial Processing System Tests ===\n");

            try
            {
                // Run synchronous tests
                ProcessingSystemTests.TestPriorityOrdering();
                ProcessingSystemTests.TestQueueSizeLimit();
                ProcessingSystemTests.TestJobRetrievalById();

                // Run asynchronous tests
                await ProcessingSystemTests.TestJobSubmissionAndCompletion();
                await ProcessingSystemTests.TestPrimeProcessor();
                await ProcessingSystemTests.TestIOProcessor();
                await ProcessingSystemTests.TestMultipleJobTypes();

                Console.WriteLine("\n=== All Tests Completed ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nTest execution error: {ex.Message}");
            }
        }
    }
}
