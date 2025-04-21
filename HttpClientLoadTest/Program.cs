// VegetaTestWrapper.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ManualHttpClient;
using Microsoft.Extensions.Configuration;

namespace HttpClientLoadTest
{
    class Program
    {
        static async Task Main(string[] args)
        {

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables("HTTP_SERVER_") 
                .AddCommandLine(args)
                .Build();

       
            string serverHost = configuration.GetValue<string>("Server:Host", "127.0.0.1");
            int serverPort = configuration.GetValue<int>("Server:Port", 8080);
            string testPath = configuration.GetValue<string>("Test:Path", "/test");
            int concurrentUsers = configuration.GetValue<int>("Test:ConcurrentUsers", 100);
            int requestsPerUser = configuration.GetValue<int>("Test:RequestsPerUser", 10);
            int warmupRequests = configuration.GetValue<int>("Test:WarmupRequests", 20);

            Console.WriteLine($"==== RawTcpHttpClient Load Test ====");
            Console.WriteLine($"Target: {serverHost}:{serverPort}{testPath}");
            Console.WriteLine($"Concurrent users: {concurrentUsers}");
            Console.WriteLine($"Requests per user: {requestsPerUser}");
            Console.WriteLine($"Total requests: {concurrentUsers * requestsPerUser}");
            Console.WriteLine();

            // Initialize client
            var httpClient = new RawTcpHttpClient(serverHost, serverPort);

            // Warm-up phase
            Console.WriteLine("Performing warm-up requests...");
            for (int i = 0; i < warmupRequests; i++)
            {
                try
                {
                    await httpClient.SendGetRequestAsync(testPath);
                }
                catch (Exception)
                {
       
                }
            }

           
            var metrics = new TestMetrics();
            var tasks = new List<Task>();
            var cancellationTokenSource = new CancellationTokenSource();
            var totalRequestsCompleted = 0;

            Console.WriteLine("Starting load test...");
            var testStartTime = Stopwatch.StartNew();

           
            for (int user = 0; user < concurrentUsers; user++)
            {
                tasks.Add(Task.Run(async () =>
                {
                   
                    var userClient = new RawTcpHttpClient(serverHost, serverPort);

                    for (int i = 0; i < requestsPerUser; i++)
                    {
                        if (cancellationTokenSource.Token.IsCancellationRequested)
                            break;

                        var requestStartTime = Stopwatch.StartNew();
                        bool isSuccess = false;

                        try
                        {
                            var response = await userClient.SendGetRequestAsync(testPath);
                            isSuccess = response.StatusCode >= 200 && response.StatusCode < 400;
                            
                            metrics.RecordLatency(requestStartTime.ElapsedMilliseconds);
                            if (isSuccess)
                            {
                                metrics.RecordSuccess();
                            }
                            else
                            {
                                metrics.RecordFailure($"HTTP {response.StatusCode}");
                            }
                        }
                        catch (Exception ex)
                        {
                            metrics.RecordFailure(ex.Message);
                        }

                        Interlocked.Increment(ref totalRequestsCompleted);
                        if (totalRequestsCompleted % 100 == 0 || totalRequestsCompleted == concurrentUsers * requestsPerUser)
                        {
                            Console.Write($"\rProgress: {totalRequestsCompleted}/{concurrentUsers * requestsPerUser} requests completed");
                        }
                    }
                }));
            }


            var reportingTask = Task.Run(async () =>
            {
                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationTokenSource.Token);
                    var elapsed = testStartTime.ElapsedMilliseconds / 1000.0;
                    var rps = totalRequestsCompleted / elapsed;
                    Console.Write($"\rProgress: {totalRequestsCompleted}/{concurrentUsers * requestsPerUser} " +
                                 $"requests completed ({rps:F1} req/sec)");
                }
            });

            try
            {
              
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nTest error: {ex.Message}");
            }
            finally
            {
                // Stop the reporting task
                cancellationTokenSource.Cancel();
                try { await reportingTask; } catch { }
            }

            var testDuration = testStartTime.ElapsedMilliseconds / 1000.0;
            
            Console.WriteLine("\n\n==== Test Results ====");
            Console.WriteLine($"Total time: {testDuration:F2} seconds");
            Console.WriteLine($"Requests completed: {totalRequestsCompleted}");
            Console.WriteLine($"Throughput: {totalRequestsCompleted / testDuration:F2} requests/second");
            Console.WriteLine();
            
            metrics.PrintResults();
            GenerateVegetaScript(serverHost, serverPort, testPath);
        }


        static void GenerateVegetaScript(string host, int port, string path)
        {
            Console.WriteLine("\n==== Vegeta Test Script ====");
            Console.WriteLine("Save this to 'targets.txt':");
            Console.WriteLine($"GET http://{host}:{port}{path}");
            Console.WriteLine("\nRun with Vegeta:");
            Console.WriteLine("vegeta attack -targets=targets.txt -rate=100 -duration=30s | vegeta report");
            Console.WriteLine("vegeta attack -targets=targets.txt -rate=100 -duration=30s | vegeta plot > results.html");
        }
    }


    class TestMetrics
    {
        private int _successCount = 0;
        private int _failureCount = 0;
        private readonly List<long> _latencies = new List<long>();
        private readonly Dictionary<string, int> _errorTypes = new Dictionary<string, int>();
        private readonly object _lock = new object();

        public void RecordSuccess()
        {
            lock (_lock)
            {
                _successCount++;
            }
        }

        public void RecordFailure(string reason)
        {
            lock (_lock)
            {
                _failureCount++;
                if (!_errorTypes.ContainsKey(reason))
                {
                    _errorTypes[reason] = 0;
                }
                _errorTypes[reason]++;
            }
        }

        public void RecordLatency(long milliseconds)
        {
            lock (_lock)
            {
                _latencies.Add(milliseconds);
            }
        }

        public void PrintResults()
        {
            lock (_lock)
            {
                Console.WriteLine("Success rate: " + 
                    ((_successCount * 100.0) / (_successCount + _failureCount)).ToString("F2") + "%");
                Console.WriteLine($"Successful requests: {_successCount}");
                Console.WriteLine($"Failed requests: {_failureCount}");
                
                if (_latencies.Count > 0)
                {
                    _latencies.Sort();
                    var min = _latencies[0];
                    var max = _latencies[_latencies.Count - 1];
                    var p50 = _latencies[_latencies.Count / 2];
                    var p95 = _latencies[(int)(_latencies.Count * 0.95)];
                    var p99 = _latencies[(int)(_latencies.Count * 0.99)];
                    var avg = _latencies.Average();

                    Console.WriteLine("\nLatency statistics (ms):");
                    Console.WriteLine($"Min: {min}");
                    Console.WriteLine($"Max: {max}");
                    Console.WriteLine($"Avg: {avg:F2}");
                    Console.WriteLine($"p50: {p50}");
                    Console.WriteLine($"p95: {p95}");
                    Console.WriteLine($"p99: {p99}");
                }

                if (_failureCount > 0)
                {
                    Console.WriteLine("\nError breakdown:");
                    foreach (var error in _errorTypes.OrderByDescending(e => e.Value))
                    {
                        Console.WriteLine($"  {error.Value} requests: {error.Key}");
                    }
                }
            }
        }
    }
}