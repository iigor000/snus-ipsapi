namespace IndustrialProcessingSystem
{
    /// <summary>
    /// Processor for different job types
    /// </summary>
    public static class JobProcessor
    {
        /// <summary>
        /// Processes a Prime job by counting prime numbers up to a given value
        /// </summary>
        /// <param name="payload">Payload format: "numbers:value,threads:count"</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Count of prime numbers</returns>
        public static async Task<int> ProcessPrime(string payload, CancellationToken cancellationToken)
        {
            var parameters = ParsePayload(payload);

            if (!int.TryParse(parameters["numbers"], out int limit))
                throw new ArgumentException("Invalid 'numbers' parameter");

            if (!int.TryParse(parameters["threads"], out int threadCount))
                throw new ArgumentException("Invalid 'threads' parameter");

            // Limit threads to [1, 8]
            threadCount = Math.Max(1, Math.Min(8, threadCount));

            return await Task.Run(() => CountPrimesParallel(limit, threadCount), cancellationToken);
        }

        /// <summary>
        /// Processes an IO job by simulating I/O delay
        /// </summary>
        /// <param name="payload">Payload format: "delay:milliseconds"</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Random value between 0 and 100</returns>
        public static async Task<int> ProcessIO(string payload, CancellationToken cancellationToken)
        {
            var parameters = ParsePayload(payload);

            if (!int.TryParse(parameters["delay"], out int delayMs))
                throw new ArgumentException("Invalid 'delay' parameter");

            await Task.Delay(delayMs, cancellationToken);

            // Return random value between 0 and 100
            return new Random().Next(0, 101);
        }

        /// <summary>
        /// Counts prime numbers up to a limit using parallel processing
        /// </summary>
        private static int CountPrimesParallel(int limit, int threadCount)
        {
            if (limit < 2)
                return 0;

            bool[] isPrime = new bool[limit + 1];
            for (int i = 2; i <= limit; i++)
                isPrime[i] = true;

            // Sieve of Eratosthenes
            for (int i = 2; i * i <= limit; i++)
            {
                if (isPrime[i])
                {
                    Parallel.For(i * i, limit + 1, new ParallelOptions { MaxDegreeOfParallelism = threadCount },
                        j =>
                        {
                            if (j % i == 0)
                                isPrime[j] = false;
                        });
                }
            }

            return isPrime.Count(p => p);
        }

        /// <summary>
        /// Parses payload string into a dictionary of key-value pairs
        /// </summary>
        private static Dictionary<string, string> ParsePayload(string payload)
        {
            var result = new Dictionary<string, string>();
            var pairs = payload.Split(',');

            foreach (var pair in pairs)
            {
                var keyValue = pair.Split(':');
                if (keyValue.Length == 2)
                    result[keyValue[0].Trim()] = keyValue[1].Trim();
            }

            return result;
        }
    }
}
