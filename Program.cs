using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace IndustrialProcessingSystem
{
    class Program
    {
        private static object _fileLock = new object();

        static async Task Main(string[] args)
        {
            // 1. Čitanje konfiguracije
            Console.WriteLine($"Tražim fajl na: {Path.GetFullPath("SystemConfig.xml")}");
            XDocument config = XDocument.Load("SystemConfig.xml");
            var root = config.Root;

            int workerCount = int.Parse(root.Element("WorkerCount").Value);
            int maxQueue = int.Parse(root.Element("MaxQueueSize").Value);

            ProcessingSystem system = new ProcessingSystem(workerCount, maxQueue);

            // Pretplata na evente
            system.JobCompleted += async (id, result, status) => { await WriteLogAsync($"[{DateTime.Now}] [{status}] {id}, {result}"); };
            system.JobFailed += async (id, result, status) => { await WriteLogAsync($"[{DateTime.Now}] [{status}] {id}, {result}"); };

            Console.WriteLine("Sistem inicijalizovan. Učitavam početne poslove iz XML-a...");

            // 2. Inicijalno učitavanje poslova iz XML-a
            foreach (var jobXml in root.Descendants("Job"))
            {
                Job initialJob = new Job
                {
                    Id = Guid.NewGuid(),
                    Type = Enum.Parse<JobType>(jobXml.Attribute("Type").Value),
                    Payload = jobXml.Attribute("Payload").Value,
                    Priority = int.Parse(jobXml.Attribute("Priority").Value)
                };

                system.Submit(initialJob);
            }

            Console.WriteLine("Početni poslovi ubačeni. Pokrećem nasumične producente...");

            // 3. Pokretanje "Producenata"
            for (int i = 0; i < 5; i++)
            {
                Task.Run(() => ProducerLoop(system));
            }

            Console.ReadLine();
        }

        private static async Task WriteLogAsync(string message)
        {
            SemaphoreSlim fileSemaphore = new SemaphoreSlim(1, 1);
            await fileSemaphore.WaitAsync();
            try
            {
                await File.AppendAllTextAsync("system_events.log", message + Environment.NewLine);
                Console.WriteLine(message);
            }
            finally
            {
                fileSemaphore.Release();
            }
        }

        private static async Task ProducerLoop(ProcessingSystem system)
        {
            Random rnd = new Random();
            while (true)
            {
                try
                {
                    JobType type = rnd.NextDouble() > 0.5 ? JobType.Prime : JobType.IO;
                    string payload = type == JobType.Prime
                        ? $"{rnd.Next(100, 10000)},{rnd.Next(1, 10)}"
                        : $"{rnd.Next(100, 2500)}";

                    Job newJob = new Job
                    {
                        Id = Guid.NewGuid(),
                        Type = type,
                        Payload = payload,
                        Priority = rnd.Next(1, 10)
                    };

                    JobHandle handle = system.Submit(newJob);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Producer warning: {ex.Message}");
                }

                await Task.Delay(rnd.Next(500, 1500));
            }
        }
    }
}
