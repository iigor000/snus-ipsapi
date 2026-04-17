using System.Xml.Linq;

namespace IndustrialProcessingSystem
{
    /// <summary>
    /// Loads system configuration from XML file
    /// </summary>
    public class ConfigurationLoader
    {
        public static (int WorkerCount, int MaxQueueSize, List<Job> InitialJobs) LoadConfiguration(string configPath)
        {
            var doc = XDocument.Load(configPath);
            var root = doc.Root ?? throw new InvalidOperationException("Invalid XML configuration");

            var workerCount = int.Parse(root.Element("WorkerCount")?.Value ?? "1");
            var maxQueueSize = int.Parse(root.Element("MaxQueueSize")?.Value ?? "100");

            var jobs = new List<Job>();
            var jobsElement = root.Element("Jobs");

            if (jobsElement != null)
            {
                foreach (var jobElement in jobsElement.Elements("Job"))
                {
                    var typeStr = jobElement.Attribute("Type")?.Value ?? string.Empty;
                    var payload = jobElement.Attribute("Payload")?.Value ?? string.Empty;
                    var priority = int.Parse(jobElement.Attribute("Priority")?.Value ?? "0");

                    if (Enum.TryParse<JobType>(typeStr, out var jobType))
                    {
                        jobs.Add(new Job(jobType, payload, priority));
                    }
                }
            }

            return (workerCount, maxQueueSize, jobs);
        }
    }
}
