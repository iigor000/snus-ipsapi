using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace IndustrialProcessingSystem
{
    public class ProcessingSystem
    {
        // Thread-safe komponente
        private readonly PriorityQueue<Job, int> _queue = new PriorityQueue<Job, int>();
        private readonly Dictionary<Guid, Job> _allJobs = new Dictionary<Guid, Job>();
        private readonly Dictionary<Guid, TaskCompletionSource<int>> _tcsMap = new Dictionary<Guid, TaskCompletionSource<int>>();
        private readonly List<JobRecord> _history = new List<JobRecord>();
        private readonly object _lock = new object();

        private readonly int _maxQueueSize;
        private int _reportIndex = 0; // Za cirkularnih 10 fajlova

        // Događaji (Func koristimo za asinhrono okidanje)
        public event Func<Guid, int, string, Task> JobCompleted;
        public event Func<Guid, int, string, Task> JobFailed;

        public ProcessingSystem(int workerCount, int maxQueueSize)
        {
            _maxQueueSize = maxQueueSize;

            // Pokretanje worker niti (Consumeri)
            for (int i = 0; i < workerCount; i++)
            {
                Thread worker = new Thread(WorkerLoop) { IsBackground = true };
                worker.Start();
            }

            // Pokretanje generatora izveštaja (svaki minut)
            Task.Run(ReportLoopAsync);
        }

        public JobHandle Submit(Job job)
        {
            lock (_lock)
            {
                // Idempotentnost: Ako posao već postoji, odbij
                if (_allJobs.ContainsKey(job.Id))
                    return null;

                // MaxQueueSize provera
                if (_queue.Count >= _maxQueueSize)
                    throw new InvalidOperationException("Queue is full!");

                var tcs = new TaskCompletionSource<int>();

                _allJobs[job.Id] = job;
                _tcsMap[job.Id] = tcs;
                _queue.Enqueue(job, job.Priority);

                Monitor.Pulse(_lock); // Budi worker nit

                return new JobHandle { Id = job.Id, Result = tcs.Task };
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                Job currentJob = null;
                TaskCompletionSource<int> currentTcs = null;

                lock (_lock)
                {
                    while (_queue.Count == 0)
                    {
                        Monitor.Wait(_lock); // Nit spava dok nema posla
                    }
                    currentJob = _queue.Dequeue();
                    currentTcs = _tcsMap[currentJob.Id];
                }

                // Obrada posla van lock-a da ne bismo blokirali ostale niti
                _ = ProcessJobWithRetryAsync(currentJob, currentTcs);
            }
        }

        private async Task ProcessJobWithRetryAsync(Job job, TaskCompletionSource<int> tcs)
        {
            int attempts = 0;
            bool success = false;
            Stopwatch sw = new Stopwatch();

            while (attempts < 3 && !success)
            {
                attempts++;
                sw.Restart();

                var processTask = ExecuteJobLogicAsync(job);
                var timeoutTask = Task.Delay(2000); // Fail ako traje duže od 2 sekunde

                var completedTask = await Task.WhenAny(processTask, timeoutTask);

                sw.Stop();

                if (completedTask == processTask)
                {
                    // Uspesno zavrseno unutar 2 sekunde
                    int result = await processTask;
                    success = true;

                    tcs.TrySetResult(result);
                    if (JobCompleted != null) await JobCompleted.Invoke(job.Id, result, "SUCCESS");

                    lock (_lock) _history.Add(new JobRecord { Job = job, Status = "SUCCESS", Duration = sw.Elapsed });
                }
                else
                {
                    // Timeout (Fail)
                    if (JobFailed != null) await JobFailed.Invoke(job.Id, 0, $"FAILED (Attempt {attempts})");
                }
            }

            if (!success)
            {
                // Abort posle 3 pokusaja
                if (JobFailed != null) await JobFailed.Invoke(job.Id, 0, "ABORT");
                tcs.TrySetException(new TimeoutException("Job aborted after 3 failed attempts."));
                lock (_lock) _history.Add(new JobRecord { Job = job, Status = "ABORT", Duration = sw.Elapsed });
            }
        }

        private async Task<int> ExecuteJobLogicAsync(Job job)
        {
            if (job.Type == JobType.Prime)
            {
                // Payload format: "10000,3" (maxVal,threads)
                var parts = job.Payload.Split(',');
                int maxVal = int.Parse(parts[0]);
                int threads = Math.Clamp(int.Parse(parts[1]), 1, 8);

                return await Task.Run(() => CalculatePrimes(maxVal, threads));
            }
            else // IO
            {
                // Payload format: "1000" (delay in ms)
                int delay = int.Parse(job.Payload);
                await Task.Delay(delay);
                return new Random().Next(0, 101);
            }
        }

        private int CalculatePrimes(int maxVal, int threads)
        {
            int count = 0;
            object primeLock = new object();

            Parallel.For(2, maxVal + 1, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
            {
                bool isPrime = true;
                for (int j = 2; j <= Math.Sqrt(i); j++)
                {
                    if (i % j == 0) { isPrime = false; break; }
                }
                if (isPrime)
                {
                    lock (primeLock) count++;
                }
            });
            return count;
        }

        public IEnumerable<Job> GetTopJobs(int n)
        {
            lock (_lock)
            {
                return _queue.UnorderedItems
                    .OrderBy(x => x.Priority)
                    .Select(x => x.Element)
                    .Take(n)
                    .ToList();
            }
        }

        public Job GetJob(Guid id)
        {
            lock (_lock)
            {
                return _allJobs.ContainsKey(id) ? _allJobs[id] : null;
            }
        }

        private async Task ReportLoopAsync()
        {
            if (!Directory.Exists("Reports")) Directory.CreateDirectory("Reports");

            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));

                List<JobRecord> currentHistory;
                lock (_lock)
                {
                    currentHistory = _history.ToList();
                }

                // Generisanje izveštaja LINQ-om
                var reportData = from x in currentHistory
                                 group x by x.Job.Type into g
                                 select new
                                 {
                                     Type = g.Key,
                                     CompletedCount = g.Count(x => x.Status == "SUCCESS"),
                                     AvgTimeMs = g.Any(x => x.Status == "SUCCESS")
                                                 ? g.Where(x => x.Status == "SUCCESS").Average(x => x.Duration.TotalMilliseconds)
                                                 : 0,
                                     FailedRecords = (from f in g
                                                      where f.Status == "ABORT"
                                                      orderby f.Job.Priority
                                                      select f).ToList()
                                 };

                // Zapis u XML fajl (Cirkularno čuvanje, prepisuje najstariji)
                XElement xml = new XElement("Report",
                    reportData.Select(r => new XElement("JobGroup",
                        new XAttribute("Type", r.Type),
                        new XElement("Completed", r.CompletedCount),
                        new XElement("AverageTimeMs", r.AvgTimeMs),
                        new XElement("FailedJobs",
                            r.FailedRecords.Select(f => new XElement("Job", new XAttribute("Id", f.Job.Id), new XAttribute("Priority", f.Job.Priority)))
                        )
                    ))
                );

                string filePath = Path.Combine("Reports", $"report_{_reportIndex}.xml");
                xml.Save(filePath);

                _reportIndex = (_reportIndex + 1) % 10; // Vraća na 0 nakon 9 (čuva poslednjih 10 fajlova)
            }
        }
    }
}
