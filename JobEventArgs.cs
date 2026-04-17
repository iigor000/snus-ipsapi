namespace IndustrialProcessingSystem
{
    /// <summary>
    /// Event arguments for job completion event
    /// </summary>
    public class JobCompletedEventArgs : EventArgs
    {
        public Guid JobId { get; set; }
        public int Result { get; set; }
        public DateTime CompletedAt { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public JobType JobType { get; set; }
    }

    /// <summary>
    /// Event arguments for job failed event
    /// </summary>
    public class JobFailedEventArgs : EventArgs
    {
        public Guid JobId { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime FailedAt { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public JobType JobType { get; set; }
        public int Attempts { get; set; }
    }
}
