using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;

namespace SnusProj;

public sealed class ProcessingSystem : IAsyncDisposable
{
    private sealed class ProcessingConfig
    {
        public int WorkerCount { get; init; }
        public int MaxQueueSize { get; init; }
        public string LogFilePath { get; init; } = string.Empty;
        public string ReportDirectory { get; init; } = string.Empty;
        public List<Job> InitialJobs { get; init; } = new();
    }

    public event Func<JobEventArgs, Task>? JobCompleted;
    public event Func<JobEventArgs, Task>? JobFailed;

    private readonly PriorityQueue<Job, (int Priority, long Sequence)> _priorityQueue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly SemaphoreSlim _logLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();

    private readonly ConcurrentDictionary<Guid, Job> _knownJobs = new();
    private readonly ConcurrentDictionary<Guid, JobHandle> _handles = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<int>> _results = new();

    private readonly ConcurrentDictionary<JobType, int> _completedByType = new();
    private readonly ConcurrentDictionary<JobType, int> _failedByType = new();
    private readonly ConcurrentDictionary<JobType, long> _durationSumByType = new();

    private readonly TimeSpan _jobTimeout = TimeSpan.FromSeconds(2);
    private readonly int _workerCount;
    private readonly int _maxQueueSize;
    private readonly string _logFilePath;
    private readonly string _reportDirectory;

    private long _sequenceNumber;
    private int _reportSlot;
    private readonly Task _reportTask;

    public ProcessingSystem(string configPath)
    {
        var config = ReadConfig(configPath);

        _workerCount = config.WorkerCount;
        _maxQueueSize = config.MaxQueueSize;
        _logFilePath = config.LogFilePath;
        _reportDirectory = config.ReportDirectory;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logFilePath)) ?? ".");
        Directory.CreateDirectory(_reportDirectory);

        JobCompleted += eventArgs => WriteLogAsync(eventArgs, _cts.Token);
        JobFailed += eventArgs => WriteLogAsync(eventArgs, _cts.Token);

        for (var i = 0; i < _workerCount; i++)
        {
            _workers.Add(Task.Run(() => WorkerLoopAsync(_cts.Token), _cts.Token));
        }

        foreach (var job in config.InitialJobs)
        {
            Submit(job);
        }

        _reportTask = Task.Run(() => ReportLoopAsync(_cts.Token), _cts.Token);
    }

    public JobHandle Submit(Job job)
    {
        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        if (_handles.TryGetValue(job.Id, out var existingHandle))
        {
            return existingHandle;
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new JobHandle
        {
            Id = job.Id,
            Result = tcs.Task
        };

        var newHandleAdded = _handles.TryAdd(job.Id, handle);
        if (!newHandleAdded)
        {
            return _handles[job.Id];
        }

        _knownJobs.TryAdd(job.Id, job);
        _results.TryAdd(job.Id, tcs);

        _queueLock.Wait();
        try
        {
            if (_priorityQueue.Count >= _maxQueueSize)
            {
                _handles.TryRemove(job.Id, out _);
                _knownJobs.TryRemove(job.Id, out _);
                _results.TryRemove(job.Id, out _);

                tcs.TrySetException(new InvalidOperationException("Queue is full. Job rejected."));
                return handle;
            }

            var sequence = Interlocked.Increment(ref _sequenceNumber);
            _priorityQueue.Enqueue(job, (job.Priority, sequence));
        }
        finally
        {
            _queueLock.Release();
        }

        _queueSignal.Release();
        return handle;
    }

    public IEnumerable<Job> GetTopJobs(int n)
    {
        if (n <= 0)
        {
            return Enumerable.Empty<Job>();
        }

        _queueLock.Wait();
        try
        {
            return _priorityQueue.UnorderedItems
                .Select(item => item.Element)
                .OrderBy(job => job.Priority)
                .ThenBy(job => job.Id)
                .Take(n)
                .ToList();
        }
        finally
        {
            _queueLock.Release();
        }
    }

    public Job GetJob(Guid id)
    {
        if (_knownJobs.TryGetValue(id, out var job))
        {
            return job;
        }

        throw new KeyNotFoundException($"Job with Id {id} not found.");
    }

    public async Task GenerateReportAsync(CancellationToken cancellationToken = default)
    {
        var completedSnapshot = _completedByType.ToArray();
        var failedSnapshot = _failedByType.ToArray();
        var durationSnapshot = _durationSumByType.ToArray();

        var averageByType = completedSnapshot
            .Select(entry =>
            {
                var totalDuration = durationSnapshot
                    .FirstOrDefault(duration => duration.Key == entry.Key)
                    .Value;

                var avg = entry.Value == 0 ? 0d : (double)totalDuration / entry.Value;
                return new { entry.Key, Average = avg };
            })
            .OrderBy(x => x.Key)
            .ToList();

        var report = new XElement("ProcessingReport",
            new XAttribute("GeneratedAt", DateTime.UtcNow.ToString("o")),
            new XElement("CompletedByType",
                completedSnapshot
                    .OrderBy(x => x.Key)
                    .Select(x => new XElement("Type",
                        new XAttribute("Name", x.Key),
                        new XAttribute("Count", x.Value)))),
            new XElement("AverageExecutionTimeMsByType",
                averageByType
                    .Select(x => new XElement("Type",
                        new XAttribute("Name", x.Key),
                        new XAttribute("AverageMs", x.Average.ToString("F2"))))),
            new XElement("FailedByType",
                failedSnapshot
                    .OrderBy(x => x.Key)
                    .Select(x => new XElement("Type",
                        new XAttribute("Name", x.Key),
                        new XAttribute("Count", x.Value)))));

        var slot = Interlocked.Increment(ref _reportSlot) % 10;
        var filePath = Path.Combine(_reportDirectory, $"report_{slot}.xml");

        await using var stream = File.Create(filePath);
        await report.SaveAsync(stream, SaveOptions.None, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        _queueSignal.Release(_workerCount);

        try
        {
            await Task.WhenAll(_workers);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _reportTask;
        }
        catch (OperationCanceledException)
        {
        }

        _queueSignal.Dispose();
        _queueLock.Dispose();
        _logLock.Dispose();
        _cts.Dispose();
    }

    private static ProcessingConfig ReadConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Configuration file not found: {configPath}");
        }

        var document = XDocument.Load(configPath);
        var root = document.Root ?? throw new InvalidOperationException("Configuration XML has no root element.");

        if (!string.Equals(root.Name.LocalName, "SystemConfig", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Configuration root must be SystemConfig.");
        }

        var workerCountNode = root.Element("WorkerCount")
            ?? throw new InvalidOperationException("WorkerCount element is required.");
        var maxQueueSizeNode = root.Element("MaxQueueSize")
            ?? throw new InvalidOperationException("MaxQueueSize element is required.");
        var jobsNode = root.Element("Jobs")
            ?? throw new InvalidOperationException("Jobs element is required.");

        var workerCount = ParseRequiredInt(workerCountNode.Value, "WorkerCount");
        var maxQueueSize = ParseRequiredInt(maxQueueSizeNode.Value, "MaxQueueSize");

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        var logFilePath = Path.Combine(baseDirectory, "processing.log");
        var reportDirectory = Path.Combine(baseDirectory, "reports");

        var jobs = jobsNode
            .Elements("Job")
            .Select(x => new Job
            {
                Id = ParseGuid(x.Attribute("Id")?.Value),
                Type = ParseRequiredEnum<JobType>(x.Attribute("Type")?.Value, "Job Type"),
                Payload = ParseRequiredString(x.Attribute("Payload")?.Value, "Job Payload"),
                Priority = ParseRequiredInt(x.Attribute("Priority")?.Value, "Job Priority")
            })
            .ToList();

        return new ProcessingConfig
        {
            WorkerCount = Math.Max(1, workerCount),
            MaxQueueSize = Math.Max(1, maxQueueSize),
            LogFilePath = logFilePath,
            ReportDirectory = reportDirectory,
            InitialJobs = jobs
        };
    }

    private static Guid ParseGuid(string? text)
    {
        return Guid.TryParse(text, out var value) ? value : Guid.NewGuid();
    }

    private static string ParseRequiredString(string? text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return text;
    }

    private static TEnum ParseRequiredEnum<TEnum>(string? text, string fieldName) where TEnum : struct
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        if (!Enum.TryParse<TEnum>(text, ignoreCase: true, out var value))
        {
            throw new InvalidOperationException($"Invalid {fieldName}: {text}");
        }

        return value;
    }

    private static int ParseRequiredInt(string? text, string fieldName)
    {
        var normalized = NormalizeNumericToken(text);
        if (!int.TryParse(normalized, out var value))
        {
            throw new InvalidOperationException($"Invalid integer for {fieldName}: {text}");
        }

        return value;
    }

    private static int ParseInt(string? text, int fallback)
    {
        var normalized = NormalizeNumericToken(text);
        return int.TryParse(normalized, out var value) ? value : fallback;
    }

    private static string NormalizeNumericToken(string? text)
    {
        return (text ?? string.Empty).Trim().Replace("_", string.Empty, StringComparison.Ordinal);
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _queueSignal.WaitAsync(cancellationToken);

            Job? job = null;
            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                if (_priorityQueue.Count > 0)
                {
                    job = _priorityQueue.Dequeue();
                }
            }
            finally
            {
                _queueLock.Release();
            }

            if (job is null)
            {
                continue;
            }

            await ProcessWithRetryAsync(job, cancellationToken);
        }
    }

    private async Task ProcessWithRetryAsync(Job job, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var executionTask = ExecuteJobAsync(job, cancellationToken);
                var result = await executionTask.WaitAsync(_jobTimeout, cancellationToken);

                stopwatch.Stop();
                RecordSuccess(job.Type, stopwatch.ElapsedMilliseconds);

                if (_results.TryGetValue(job.Id, out var tcs))
                {
                    tcs.TrySetResult(result);
                }

                await RaiseEventAsync(JobCompleted, new JobEventArgs
                {
                    JobId = job.Id,
                    Type = job.Type,
                    Result = result,
                    Status = "COMPLETED",
                    Attempt = attempt
                });

                return;
            }
            catch (TimeoutException timeoutEx)
            {
                stopwatch.Stop();

                var isAbort = attempt == maxAttempts;
                if (isAbort)
                {
                    RecordFinalFailure(job.Type);

                    if (_results.TryGetValue(job.Id, out var tcs))
                    {
                        tcs.TrySetException(new TimeoutException($"Job {job.Id} timed out 3 times.", timeoutEx));
                    }
                }

                await RaiseEventAsync(JobFailed, new JobEventArgs
                {
                    JobId = job.Id,
                    Type = job.Type,
                    Status = isAbort ? "ABORT" : "FAILED",
                    Attempt = attempt,
                    ErrorMessage = timeoutEx.Message
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();

                var isAbort = attempt == maxAttempts;
                if (isAbort)
                {
                    RecordFinalFailure(job.Type);

                    if (_results.TryGetValue(job.Id, out var tcs))
                    {
                        tcs.TrySetException(new InvalidOperationException($"Job {job.Id} failed after retries.", ex));
                    }
                }

                await RaiseEventAsync(JobFailed, new JobEventArgs
                {
                    JobId = job.Id,
                    Type = job.Type,
                    Status = isAbort ? "ABORT" : "FAILED",
                    Attempt = attempt,
                    ErrorMessage = ex.Message
                });
            }
        }
    }

    private async Task<int> ExecuteJobAsync(Job job, CancellationToken cancellationToken)
    {
        return job.Type switch
        {
            JobType.Prime => await ExecutePrimeAsync(job.Payload, cancellationToken),
            JobType.IO => await ExecuteIoAsync(job.Payload, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported JobType: {job.Type}")
        };
    }

    private static Task<int> ExecutePrimeAsync(string payload, CancellationToken cancellationToken)
    {
        var payloadMap = ParsePayloadMap(payload);
        var limit = ParseInt(RequirePayloadValue(payloadMap, "numbers"), 1);
        var requestedThreads = ParseInt(RequirePayloadValue(payloadMap, "threads"), 1);
        var threadCount = Math.Clamp(requestedThreads, 1, 8);

        if (limit < 2)
        {
            return Task.FromResult(0);
        }

        return Task.Run(() =>
        {
            var count = 0;
            var sync = new object();

            Parallel.For(2, limit + 1, new ParallelOptions
            {
                MaxDegreeOfParallelism = threadCount,
                CancellationToken = cancellationToken
            }, number =>
            {
                if (!IsPrime(number))
                {
                    return;
                }

                lock (sync)
                {
                    count++;
                }
            });

            return count;
        }, cancellationToken);
    }

    private static async Task<int> ExecuteIoAsync(string payload, CancellationToken cancellationToken)
    {
        var payloadMap = ParsePayloadMap(payload);
        var delayMs = ParseInt(RequirePayloadValue(payloadMap, "delay"), 0);
        await Task.Delay(Math.Max(0, delayMs), cancellationToken);
        return Random.Shared.Next(0, 101);
    }

    private static Dictionary<string, string> ParsePayloadMap(string payload)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                throw new FormatException($"Invalid payload segment: {pair}");
            }

            result[parts[0]] = parts[1];
        }

        return result;
    }

    private static string RequirePayloadValue(IReadOnlyDictionary<string, string> payloadMap, string key)
    {
        if (!payloadMap.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"Payload must contain '{key}'.");
        }

        return value;
    }

    private static bool IsPrime(int number)
    {
        if (number < 2)
        {
            return false;
        }

        if (number == 2)
        {
            return true;
        }

        if (number % 2 == 0)
        {
            return false;
        }

        var boundary = (int)Math.Sqrt(number);
        for (var divisor = 3; divisor <= boundary; divisor += 2)
        {
            if (number % divisor == 0)
            {
                return false;
            }
        }

        return true;
    }

    private void RecordSuccess(JobType type, long elapsedMs)
    {
        _completedByType.AddOrUpdate(type, 1, (_, value) => value + 1);
        _durationSumByType.AddOrUpdate(type, elapsedMs, (_, value) => value + elapsedMs);
    }

    private void RecordFinalFailure(JobType type)
    {
        _failedByType.AddOrUpdate(type, 1, (_, value) => value + 1);
    }

    private async Task RaiseEventAsync(Func<JobEventArgs, Task>? eventHandler, JobEventArgs args)
    {
        if (eventHandler is null)
        {
            return;
        }

        var invocationList = eventHandler.GetInvocationList();
        var tasks = new List<Task>(invocationList.Length);

        foreach (var singleHandler in invocationList.Cast<Func<JobEventArgs, Task>>())
        {
            tasks.Add(singleHandler(args));
        }

        await Task.WhenAll(tasks);
    }

    private async Task WriteLogAsync(JobEventArgs args, CancellationToken cancellationToken)
    {
        var line = $"[{DateTime.UtcNow:O}] [{args.Status}] {args.JobId}, {args.Result?.ToString() ?? "N/A"}{Environment.NewLine}";

        await _logLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_logFilePath, line, cancellationToken);
        }
        finally
        {
            _logLock.Release();
        }
    }

    private async Task ReportLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await GenerateReportAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep report loop alive even if writing one report fails.
            }
        }
    }
}