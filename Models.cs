namespace SnusProj;

public enum JobType
{
    Prime,
    IO
}

public sealed class Job
{
    public Guid Id { get; init; }
    public JobType Type { get; init; }
    public string Payload { get; init; } = string.Empty;
    public int Priority { get; init; }
}

public sealed class JobHandle
{
    public Guid Id { get; init; }
    public Task<int> Result { get; init; } = Task.FromException<int>(new InvalidOperationException("No result available."));
}

public sealed class JobEventArgs
{
    public Guid JobId { get; init; }
    public JobType Type { get; init; }
    public int? Result { get; init; }
    public string Status { get; init; } = string.Empty;
    public int Attempt { get; init; }
    public string? ErrorMessage { get; init; }
}