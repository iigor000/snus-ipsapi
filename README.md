# Industrial Processing System API (snus-ipsapi)

A thread-safe, asynchronous job processing system that implements a **producer-consumer** pattern with priority-based job scheduling, event-driven architecture, and automatic reporting.

## Architecture Overview

The system is based on the **producer-consumer** design pattern:

```
┌─────────────────────────────────────────────────────────┐
│                  PRODUCERS                              │
│  (Random Job Generation Threads)                        │
│  - Submit jobs with random types and priorities         │
│  - Rate-limited to avoid queue overflow                 │
└──────────────┬──────────────────────────────────────────┘
               │ Submit(job)
               ▼
┌─────────────────────────────────────────────────────────┐
│          PRIORITY QUEUE                                 │
│  (Thread-Safe Job Queue)                               │
│  - Manages pending jobs                                 │
│  - Sorted by priority (lower number = higher priority) │
└──────────────┬──────────────────────────────────────────┘
               │ Dequeue
               ▼
┌─────────────────────────────────────────────────────────┐
│              CONSUMERS (Workers)                        │
│  (Thread Pool Processing Jobs)                          │
│  - Execute jobs asynchronously                          │
│  - Handle retries on failure (max 3 attempts)          │
│  - Enforce 2-second timeout per job                    │
└──────────────┬──────────────────────────────────────────┘
               │ Fire Events
               ▼
┌─────────────────────────────────────────────────────────┐
│              EVENT SYSTEM                              │
│  - JobCompleted: Fired on successful execution         │
│  - JobFailed: Fired after all retries exhausted        │
│  - Logged to: system_events.log                        │
│  - Reports generated every minute (Reports/report_*.xml)│
└─────────────────────────────────────────────────────────┘
```

## Core Components

### 1. Models.cs
Defines all data structures:

#### **Job**
```csharp
public class Job
{
    public Guid Id { get; set; }              // Unique identifier
    public JobType Type { get; set; }         // Prime or IO
    public string Payload { get; set; }       // Job parameters
    public int Priority { get; set; }         // 1 = highest, 10 = lowest
}
```

#### **JobType Enum**
```csharp
public enum JobType
{
    Prime,  // Calculate prime numbers (CPU-intensive)
    IO      // Simulate I/O operation (delay-based)
}
```

#### **JobHandle**
```csharp
public class JobHandle
{
    public Guid Id { get; set; }
    public Task<int> Result { get; set; }    // Async result awaitable
}
```

#### **JobRecord**
Internal class for tracking job history and reporting.

---

### 2. ProcessingSystem.cs
The core processing engine. Responsibilities:

#### **Job Submission**
```csharp
JobHandle Submit(Job job)
```
- Validates job isn't already processed (idempotency)
- Checks queue size limit
- Enqueues with priority
- Returns handle for awaiting result

#### **Worker Threads**
- Number: Configured via `SystemConfig.xml`
- Behavior: Pull jobs from priority queue and process
- Synchronization: Lock-based with Monitor.Wait/Pulse for efficiency

#### **Job Processing Pipeline**
1. **Dequeue** from priority queue
2. **Execute** job logic:
   - **Prime jobs**: Calculate count of prime numbers up to limit
     - Parallel processing with 1-8 threads (configurable)
     - Input format: `"maxValue,threadCount"` (e.g., `"10000,4"`)
     - Returns: Integer count of primes
   
   - **IO jobs**: Simulate I/O with delay
     - Input format: Delay in milliseconds (e.g., `"1000"`)
     - Returns: Random value 0-100
     
3. **Timeout Handling**: 2-second limit per job
   - If job exceeds 2 seconds → triggers FAILED event
   
4. **Retry Mechanism**: Max 3 attempts
   - Attempt 1: Initial execution
   - Attempts 2-3: Retries on timeout
   - After 3 failures → ABORT status
   
5. **Event Firing**: 
   - Success → JobCompleted event
   - All failures → JobFailed event

#### **Priority Queue**
- Jobs processed in priority order (not FIFO)
- Lower priority number = higher priority
- Example: Job with priority 1 processes before priority 5

#### **Reporting System** (Integrated)
- Runs every 60 seconds
- Generates XML reports with LINQ aggregations:
  - Count of completed jobs per type
  - Average execution time per type
  - Count of aborted jobs per type
- Rotating storage: Keeps last 10 reports (report_0.xml to report_9.xml)
- Location: `Reports/report_N.xml`

---

### 3. Program.cs
Entry point and producer implementation:

#### **Configuration Loading**
Reads from `SystemConfig.xml`:
```xml
<SystemConfig>
    <WorkerCount>4</WorkerCount>          <!-- Number of worker threads -->
    <MaxQueueSize>100</MaxQueueSize>      <!-- Max pending jobs -->
    <Jobs>
        <Job Type="Prime" Payload="10000,4" Priority="1"/>
        <Job Type="IO" Payload="500" Priority="2"/>
    </Jobs>
</SystemConfig>
```

#### **Producers (Job Generators)**
- **What**: 5 randomly-generating threads
- **Why**: Simulates multiple systems/users submitting jobs
- **How**: Each producer thread runs `ProducerLoop()` continuously
  
```csharp
// Producer behavior:
// 1. Wait 500-1500ms (random delay between submissions)
// 2. Generate random job:
//    - Type: 50% Prime, 50% IO
//    - For Prime: payload = "randomValue(100-10000), randomThreads(1-10)"
//    - For IO: payload = "randomDelay(100-2500ms)"
//    - Priority: random(1-9)
// 3. Submit job to system
// 4. Repeat forever
```

#### **Event Subscription**
- Subscribes to `JobCompleted` and `JobFailed` events
- Writes results to `system_events.log` in format:
  ```
  [DateTime] [Status] JobId, Result
  ```

---

## Execution Flow

### System Startup
1. Load `SystemConfig.xml` → extract `WorkerCount` and `MaxQueueSize`
2. Create `ProcessingSystem` with worker threads → workers enter waiting state
3. Load initial jobs from XML → submit to queue
4. Create 5 producer threads → begin randomly generating jobs
5. Subscribe to events → ready to log completions

### During Execution
**Producer Thread Loop:**
```
Wait (random delay)
  ↓
Generate random job
  ↓
Submit to ProcessingSystem
  ↓
If queue full → catch exception, wait, retry
  ↓
Back to Wait
```

**Worker Thread Loop:**
```
Wait for job in queue (Monitor.Wait)
  ↓
Dequeue job
  ↓
Execute with timeout (max 2 seconds)
  ↓
Success? 
  ├─ YES → Fire JobCompleted event → Back to Wait
  └─ NO → Retry (max 2 more times)
        ├─ Success on retry? → Fire JobCompleted event
        └─ All failed? → Fire JobFailed event with ABORT status
```

**Report Generation Loop:**
```
Every 60 seconds:
  ↓
Aggregate job history using LINQ
  ↓
Generate XML report with statistics
  ↓
Save to Reports/report_N.xml (rotates N from 0-9)
  ↓
Loop
```

---

## Key Features

### Thread Safety
- **Lock-based synchronization**: Simple, reliable
- **Monitor.Wait/Pulse**: Efficient thread sleeping without busy-waiting
- **No race conditions**: All queue access protected by lock

### Idempotency
- Same job ID cannot be processed twice
- Prevents accidental duplicate execution

### Asynchronous Processing
- Uses `Task` for all async operations
- `JobHandle.Result` is awaitable without blocking
- No `Thread.Sleep` for waiting (uses TaskCompletionSource)

### Error Handling
- Queue overflow: Throws `InvalidOperationException`
- Job timeout: Automatically retried (up to 3 times)
- Job failure: Logged as ABORT after 3 failed attempts
- Producer errors: Caught and logged, producer continues

### Reporting
- LINQ-based aggregation for flexibility
- XML format for easy parsing
- Rotating files prevent disk space issues
- Statistics: counts, averages, failure tracking

---

## Configuration (SystemConfig.xml)

```xml
<?xml version="1.0" encoding="utf-8"?>
<SystemConfig>
    <WorkerCount>4</WorkerCount>
    <MaxQueueSize>100</MaxQueueSize>
    <Jobs>
        <Job Type="Prime" Payload="5000,2" Priority="1"/>
        <Job Type="Prime" Payload="10000,4" Priority="2"/>
        <Job Type="IO" Payload="300" Priority="1"/>
        <Job Type="IO" Payload="1000" Priority="3"/>
    </Jobs>
</SystemConfig>
```

### Parameters
- **WorkerCount**: 1-16 recommended (number of concurrent job processors)
- **MaxQueueSize**: 10-1000 recommended (max jobs waiting)
- **Initial Jobs**: Optional, loaded at startup

---

## Output Files

### system_events.log
Event log with all job completions/failures:
```
[4/17/2026 10:30:45] [SUCCESS] a1b2c3d4-e5f6-7890-abcd-ef1234567890, 127
[4/17/2026 10:30:47] [FAILED (Attempt 1)] b2c3d4e5-f6a7-8901-bcde-f12345678901, 0
[4/17/2026 10:30:49] [FAILED (Attempt 2)] b2c3d4e5-f6a7-8901-bcde-f12345678901, 0
[4/17/2026 10:30:51] [ABORT] b2c3d4e5-f6a7-8901-bcde-f12345678901, 0
```

### Reports/report_0.xml through report_9.xml
Statistics reports (rotated, keeps last 10):
```xml
<?xml version="1.0" encoding="utf-8"?>
<Report>
  <JobGroup Type="Prime">
    <Completed>12</Completed>
    <AverageTimeMs>1234.56</AverageTimeMs>
    <FailedJobs>
      <Job Id="abc..." Priority="3"/>
      <Job Id="def..." Priority="5"/>
    </FailedJobs>
  </JobGroup>
  <JobGroup Type="IO">
    <Completed>45</Completed>
    <AverageTimeMs>567.89</AverageTimeMs>
    <FailedJobs/>
  </JobGroup>
</Report>
```

---

## Job Payload Formats

### Prime Job
- **Format**: `"maxValue,threadCount"`
- **Example**: `"10000,4"` → Count primes up to 10000 using 4 parallel threads
- **Constraints**: threadCount limited to [1,8]
- **Algorithm**: Trial division (simple, not optimized)
- **Returns**: Integer (count of primes found)

### IO Job
- **Format**: `"delayMilliseconds"`
- **Example**: `"1000"` → Simulate 1-second I/O delay
- **Returns**: Random integer 0-100

---

## Specification Compliance

| Requirement | Implementation | Location |
|---|---|---|
| Producer-Consumer | 5 producer threads + worker threads | Program.cs / ProcessingSystem.cs |
| Priority Queue | Sorted by job.Priority | ProcessingSystem.cs |
| Max Queue Size | Checked on Submit() | ProcessingSystem.cs |
| Idempotency | Check _allJobs.ContainsKey | ProcessingSystem.cs |
| Prime Processing | Parallel algorithm | ProcessingSystem.ExecuteJobLogicAsync |
| IO Simulation | Task.Delay | ProcessingSystem.ExecuteJobLogicAsync |
| 2-second Timeout | Task.Delay(2000) + Task.WhenAny | ProcessingSystem.ProcessJobWithRetryAsync |
| 3 Retries | while (attempts < 3) loop | ProcessingSystem.ProcessJobWithRetryAsync |
| ABORT on Fail | After 3 failures | ProcessingSystem.ProcessJobWithRetryAsync |
| Event System | Func delegates, async logging | Program.cs / ProcessingSystem.cs |
| Minute Reports | ReportLoopAsync() every 60s | ProcessingSystem.cs |
| Report Rotation | _reportIndex % 10 | ProcessingSystem.cs |
| GetTopJobs(n) | Returns by priority | ProcessingSystem.cs |
| GetJob(id) | Dictionary lookup | ProcessingSystem.cs |
| XML Config | XDocument.Load | Program.cs |
| Thread Safety | lock + Monitor | ProcessingSystem.cs |

---

## Running the System

```bash
# Build
dotnet build

# Run
dotnet run

# The system will:
# 1. Load SystemConfig.xml
# 2. Initialize 4 worker threads (or configured count)
# 3. Load initial jobs from XML
# 4. Start 5 producer threads
# 5. Process jobs continuously
# 6. Log events to system_events.log
# 7. Generate reports every 60 seconds to Reports/

# To stop: Press Enter in console
```

---

## Performance Characteristics

- **Throughput**: Depends on job complexity and worker count
- **Latency**: Queue wait time + 2-second max job time
- **Memory**: O(queue size + worker state)
- **CPU**: Scales with worker threads and Prime job payloads

---

## Summary

This is a complete, specification-compliant job processing system that:
- ✅ Implements producer-consumer pattern
- ✅ Handles priorities correctly
- ✅ Provides thread safety
- ✅ Manages timeouts and retries
- ✅ Logs all events
- ✅ Generates periodic reports
- ✅ Keeps codebase minimal (3 files, ~500 lines)
