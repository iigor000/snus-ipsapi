using System;
using System.Threading.Tasks;

namespace IndustrialProcessingSystem
{
    public enum JobType
    {
        Prime,
        IO
    }

    public class Job
    {
        public Guid Id { get; set; }
        public JobType Type { get; set; }
        public string Payload { get; set; }
        public int Priority { get; set; }
    }

    public class JobHandle
    {
        public Guid Id { get; set; }
        public Task<int> Result { get; set; }
    }

    public class JobRecord
    {
        public Job Job { get; set; }
        public string Status { get; set; } // "SUCCESS", "FAILED", "ABORT"
        public TimeSpan Duration { get; set; }
    }
}
