using System.Collections.Concurrent;
using System.Xml.Linq;

namespace IndustrialProcessingSystem
{
    /// <summary>
    /// Thread-safe service for processing industrial jobs
    /// </summary>
    public class ProcessingSystem : IDisposable
    {
        private readonly PriorityQueue<Job, (int Priority, DateTime CreatedAt)> _jobQueue;
        private readonly ConcurrentDictionary<Guid, Job> _jobs;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<int>> _jobResults;
        private readonly ConcurrentDictionary<Guid, JobExecutionState> _executionStates;
        private readonly int _maxQueueSize;
        private readonly int _workerCount;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly List<Task> _workerTasks;
        private readonly SemaphoreSlim _jobAvailable;
        private readonly object _queueLock = new object();

        public event EventHandler<JobCompletedEventArgs>? JobCompleted;
        public event EventHandler<JobFailedEventArgs>? JobFailed;

        private const int JOB_TIMEOUT_MS = 2000;
        private const int MAX_RETRIES = 2;

        public ProcessingSystem(int workerCount, int maxQueueSize)
        {
            _workerCount = workerCount;
            _maxQueueSize = maxQueueSize;
            _jobQueue = new PriorityQueue<Job, (int, DateTime)>();
            _jobs = new ConcurrentDictionary<Guid, Job>();
            _jobResults = new ConcurrentDictionary<Guid, TaskCompletionSource<int>>();
            _executionStates = new ConcurrentDictionary<Guid, JobExecutionState>();
            _cancellationTokenSource = new CancellationTokenSource();
            _jobAvailable = new SemaphoreSlim(0);
            _workerTasks = new List<Task>();

            StartWorkers();
        }

        /// <summary>
        /// Submits a job for processing
        /// </summary>
        public JobHandle Submit(Job job)
        {
            lock (_queueLock)
            {
                if (_jobQueue.Count >= _maxQueueSize)
                    throw new InvalidOperationException("Queue is full");

                var tcs = new TaskCompletionSource<int>();
                _jobs.TryAdd(job.Id, job);
                _jobResults.TryAdd(job.Id, tcs);
                _executionStates.TryAdd(job.Id, new JobExecutionState());

                _jobQueue.Enqueue(job, (job.Priority, job.CreatedAt));
                _jobAvailable.Release();

                return new JobHandle(job.Id, tcs.Task);
            }
        }

        /// <summary>
        /// Gets the top N jobs by priority from the current queue
        /// </summary>
        public IEnumerable<Job> GetTopJobs(int n)
        {
            lock (_queueLock)
            {
                var jobs = new List<Job>();
                var tempQueue = new PriorityQueue<Job, (int Priority, DateTime CreatedAt)>();

                // Copy all jobs from main queue
                while (_jobQueue.Count > 0)
                {
                    var job = _jobQueue.Dequeue();
                    tempQueue.Enqueue(job, (job.Priority, job.CreatedAt));
                }

                // Get top N
                for (int i = 0; i < n && tempQueue.Count > 0; i++)
                {
                    var job = tempQueue.Dequeue();
                    jobs.Add(job);
                }

                // Restore queue
                foreach (var job in jobs.Union(tempQueue.UnorderedItems.Select(x => x.Element)))
                {
                    _jobQueue.Enqueue(job, (job.Priority, job.CreatedAt));
                }

                return jobs;
            }
        }

        /// <summary>
        /// Gets a job by its ID
        /// </summary>
        public Job? GetJob(Guid id)
        {
            _jobs.TryGetValue(id, out var job);
            return job;
        }

        private void StartWorkers()
        {
            for (int i = 0; i < _workerCount; i++)
            {
                var workerTask = Task.Run(async () => await WorkerLoop());
                _workerTasks.Add(workerTask);
            }
        }

        private async Task WorkerLoop()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await _jobAvailable.WaitAsync(_cancellationTokenSource.Token);

                    Job? job = null;
                    lock (_queueLock)
                    {
                        if (_jobQueue.Count > 0)
                            job = _jobQueue.Dequeue();
                    }

                    if (job == null)
                        continue;

                    await ProcessJob(job);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Worker error: {ex.Message}");
                }
            }
        }

        private async Task ProcessJob(Job job)
        {
            var state = _executionStates[job.Id];
            var startTime = DateTime.UtcNow;
            int result = 0;
            bool success = false;

            try
            {
                int attempt = 0;
                while (attempt <= MAX_RETRIES)
                {
                    state.Attempts++;
                    attempt++;

                    try
                    {
                        using (var cts = new CancellationTokenSource(JOB_TIMEOUT_MS))
                        {
                            result = job.Type switch
                            {
                                JobType.Prime => await JobProcessor.ProcessPrime(job.Payload, cts.Token),
                                JobType.IO => await JobProcessor.ProcessIO(job.Payload, cts.Token),
                                _ => throw new InvalidOperationException($"Unknown job type: {job.Type}")
                            };

                            success = true;
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (attempt > MAX_RETRIES)
                        {
                            throw;
                        }
                    }
                }

                if (success && _jobResults.TryGetValue(job.Id, out var tcs))
                {
                    tcs.SetResult(result);
                    var executionTime = DateTime.UtcNow - startTime;
                    state.ExecutionTime = executionTime;

                    OnJobCompleted(new JobCompletedEventArgs
                    {
                        JobId = job.Id,
                        Result = result,
                        CompletedAt = DateTime.UtcNow,
                        ExecutionTime = executionTime,
                        JobType = job.Type
                    });
                }
            }
            catch (Exception ex)
            {
                var executionTime = DateTime.UtcNow - startTime;
                state.ExecutionTime = executionTime;
                state.Failed = true;

                if (_jobResults.TryGetValue(job.Id, out var tcs))
                {
                    tcs.SetException(ex);
                }

                OnJobFailed(new JobFailedEventArgs
                {
                    JobId = job.Id,
                    Reason = ex.Message,
                    FailedAt = DateTime.UtcNow,
                    ExecutionTime = executionTime,
                    JobType = job.Type,
                    Attempts = state.Attempts
                });
            }
        }

        private void OnJobCompleted(JobCompletedEventArgs e)
        {
            JobCompleted?.Invoke(this, e);
        }

        private void OnJobFailed(JobFailedEventArgs e)
        {
            JobFailed?.Invoke(this, e);
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            Task.WaitAll(_workerTasks.ToArray(), TimeSpan.FromSeconds(10));
            _cancellationTokenSource?.Dispose();
            _jobAvailable?.Dispose();
        }

        /// <summary>
        /// Internal class to track job execution state
        /// </summary>
        private class JobExecutionState
        {
            public int Attempts { get; set; }
            public bool Failed { get; set; }
            public TimeSpan ExecutionTime { get; set; }
        }
    }
}
