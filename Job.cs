namespace IndustrialProcessingSystem
{
    /// <summary>
    /// Represents a job to be processed by the system
    /// </summary>
    public class Job
    {
        /// <summary>
        /// Unique identifier for the job
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Type of the job (Prime or IO)
        /// </summary>
        public JobType Type { get; set; }

        /// <summary>
        /// Payload containing job-specific parameters
        /// </summary>
        public string Payload { get; set; }

        /// <summary>
        /// Priority of the job (lower number = higher priority)
        /// </summary>
        public int Priority { get; set; }

        /// <summary>
        /// Timestamp when the job was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        public Job()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
            Payload = string.Empty;
        }

        public Job(JobType type, string payload, int priority)
        {
            Id = Guid.NewGuid();
            Type = type;
            Payload = payload;
            Priority = priority;
            CreatedAt = DateTime.UtcNow;
        }
    }
}
