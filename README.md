# Industrial Processing System API

A thread-safe, asynchronous job processing system implemented in C# that simulates industrial task processing with support for concurrent execution, priority-based scheduling, event-driven architecture, and automatic reporting.

## Features

### Core Components

1. **Job Management**
   - `Job` class: Represents a task with unique ID, type, payload, and priority
   - `JobType` enum: Defines supported job types (Prime, IO)
   - `JobHandle` class: Represents the async result of a submitted job

2. **Processing System**
   - Thread-safe job queue with priority-based ordering
   - Configurable worker threads
   - Asynchronous job execution using `Task`
   - Idempotent processing (prevent duplicate execution)
   - Automatic retry mechanism (2 retries on failure)
   - Timeout handling (2-second limit per job)

3. **Job Types**
   - **Prime**: Calculates the number of prime numbers up to a given limit
     - Parallel processing with configurable thread count (1-8)
     - Uses Sieve of Eratosthenes algorithm
   - **IO**: Simulates I/O operations with configurable delay
     - Returns random value between 0-100

4. **Configuration System**
   - XML-based configuration loading
   - Configurable worker count and queue size
   - Initial job batch loading from config

5. **Event System**
   - `JobCompleted` event: Fired when a job succeeds
   - `JobFailed` event: Fired when a job fails after all retries
   - Async event logging to file

6. **Reporting System**
   - Automatic report generation every minute
   - LINQ-based statistics aggregation:
     - Completed jobs count by type
     - Average execution time by type
     - Failed jobs count by type
   - XML report format
   - Rotating storage (keeps last 10 reports)

7. **Additional Features**
   - `GetTopJobs(n)`: Retrieve top N jobs by priority from queue
   - `GetJob(id)`: Retrieve job by ID
   - Producer-consumer pattern with multiple producer threads
   - Time-independent testing with `TaskCompletionSource` and `SemaphoreSlim`

## Project Structure

```
snus-ipsapi/
├── Job.cs                    # Job class definition
├── JobType.cs                # Job type enumeration
├── JobHandle.cs              # Job handle/result class
├── JobEventArgs.cs           # Event argument classes
├── JobProcessor.cs           # Job type processors (Prime, IO)
├── ProcessingSystem.cs       # Core processing service
├── ConfigurationLoader.cs    # XML configuration loader
├── EventLogger.cs            # Async event logging
├── ReportGenerator.cs        # Periodic report generation
├── Program.cs                # Main entry point
├── SystemConfig.xml          # Configuration file
└── snus-ipsapi.csproj        # Project file
```

## Configuration (SystemConfig.xml)

```xml
<?xml version="1.0" encoding="utf-8"?>
<SystemConfig>
    <WorkerCount>5</WorkerCount>
    <MaxQueueSize>100</MaxQueueSize>
    <Jobs>
        <Job Type="Prime" Payload="numbers:10_000,threads:3" Priority="1"/>
        <Job Type="IO" Payload="delay:1_000" Priority="3"/>
    </Jobs>
</SystemConfig>
```

### Configuration Parameters

- **WorkerCount**: Number of worker threads for processing
- **MaxQueueSize**: Maximum number of jobs in the queue
- **Jobs**: Initial batch of jobs to load
  - **Type**: Job type (Prime or IO)
  - **Payload**: Job-specific parameters
  - **Priority**: Job priority (lower number = higher priority)

## API Usage

### Basic Usage

```csharp
// Load configuration
var (workerCount, maxQueueSize, initialJobs) = 
    ConfigurationLoader.LoadConfiguration("SystemConfig.xml");

// Create processing system
var system = new ProcessingSystem(workerCount, maxQueueSize);

// Subscribe to events
system.JobCompleted += (s, e) => 
    Console.WriteLine($"Job {e.JobId} completed with result {e.Result}");

system.JobFailed += (s, e) => 
    Console.WriteLine($"Job {e.JobId} failed: {e.Reason}");

// Submit jobs
var job = new Job(JobType.Prime, "numbers:10000,threads:4", priority: 1);
var handle = system.Submit(job);

// Wait for result
int result = await handle.Result;
```

### Query Jobs

```csharp
// Get top 10 jobs by priority
var topJobs = system.GetTopJobs(10);

// Get specific job
var job = system.GetJob(jobId);
```

## Running the Application

### Build

```bash
dotnet build -c Release
```

### Run

```bash
dotnet run
```

The application will:
1. Load configuration from SystemConfig.xml
2. Initialize worker threads
3. Submit initial jobs
4. Start producer threads that randomly generate jobs
5. Log events to `job_events.log`
6. Generate reports every minute to the `reports/` directory
7. Continue until you press ENTER

### Output Files

- **job_events.log**: Event log with timestamps and job statuses
- **reports/**: Directory containing rolling reports (last 10)
  - report_01.xml through report_10.xml

## Thread Safety

The system uses the following mechanisms for thread safety:

- `PriorityQueue<T>`: Priority-based ordering
- `ConcurrentDictionary<K,V>`: Lock-free concurrent storage
- `SemaphoreSlim`: Coordination between workers and queue
- `TaskCompletionSource<T>`: Async result handling
- Explicit locking for queue operations

## Timeout and Retry Logic

- **Timeout**: 2 seconds per job
- **Retries**: Automatic 2 retries on timeout
- **Final Status**: ABORT if fails after all retries
- **Status Codes**: COMPLETED, FAILED, or ABORT in logs

## LINQ Usage

### Report Generation

```csharp
var completedByType = _completedJobs.Values
    .GroupBy(j => j.JobType)
    .Select(g => new
    {
        JobType = g.Key,
        Count = g.Count(),
        AvgExecutionTime = g.Average(j => j.ExecutionTime.TotalMilliseconds)
    });

var failedByType = _failedJobs.Values
    .GroupBy(j => j.JobType)
    .OrderBy(g => g.Key.ToString())
    .Select(g => new { JobType = g.Key, Count = g.Count() });
```

## Key Design Decisions

1. **Priority Queue**: Ensures high-priority jobs are processed first
2. **Task-based Async**: Leverages modern async/await patterns
3. **Event-Driven**: Decoupled architecture for logging and reporting
4. **Idempotent Processing**: Prevents duplicate job execution
5. **Configurable Worker Pool**: Adapts to system resources
6. **Time-Independent Testing**: Uses `TaskCompletionSource` instead of `Thread.Sleep`

## Requirements Met

✅ Thread-safe concurrent job processing  
✅ Asynchronous execution with Task  
✅ Priority-based job scheduling  
✅ Event-driven architecture (JobCompleted, JobFailed)  
✅ XML configuration loading  
✅ Prime number calculation (parallel, 1-8 threads)  
✅ IO simulation with configurable delay  
✅ Idempotent processing  
✅ Timeout handling (2 seconds)  
✅ Retry mechanism (2 retries)  
✅ Async event logging  
✅ LINQ-based reporting  
✅ Rolling report storage (last 10)  
✅ GetTopJobs and GetJob methods  
✅ Producer-consumer pattern  
✅ Time-independent testing  

## Notes

- The system automatically skips duplicate job submissions based on ID
- Jobs are processed with a maximum timeout of 2 seconds
- Queue size is limited by configuration to prevent unbounded growth
- Reports are generated asynchronously every 60 seconds
- Producer threads generate random jobs to demonstrate concurrent submission
