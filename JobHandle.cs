namespace IndustrialProcessingSystem
{
    /// <summary>
    /// Represents the result handle of a submitted job
    /// </summary>
    public class JobHandle
    {
        /// <summary>
        /// Unique identifier of the job
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Task representing the asynchronous result of job execution
        /// </summary>
        public Task<int> Result { get; set; }

        public JobHandle(Guid id, Task<int> result)
        {
            Id = id;
            Result = result;
        }
    }
}
