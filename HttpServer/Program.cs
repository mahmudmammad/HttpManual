using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace HttpServer
{
    class Program
    {
        private static SemaphoreSlim connectionSemaphore;
        private static int keepAliveTimeout;
        private static string host;
        private static int port;
        private static int requestCount = 0;

        static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables("HTTP_SERVER_")
                .AddCommandLine(args)
                .Build();

            port = configuration.GetValue<int>("Server:Port", 8080);
            host = configuration.GetValue<string>("Server:Host", "localhost");
            int maxConnections = configuration.GetValue<int>("Server:MaxConnections", 100);
            keepAliveTimeout = configuration.GetValue<int>("Server:KeepAliveTimeoutMs", 15000);


            ServicePointManager.DefaultConnectionLimit = maxConnections;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            connectionSemaphore = new SemaphoreSlim(maxConnections, maxConnections);

    
            string prefix = $"http://{(host == "*" ? "+" : host)}:{port}/";
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add(prefix);

            try
            {
                listener.Start();
                Console.WriteLine($"HTTP Server started on {prefix}");
                Console.WriteLine($"Configuration: MaxConnections={maxConnections}, KeepAliveTimeout={keepAliveTimeout}ms");

           
                var tasks = new List<Task>();
                for (int i = 0; i < Environment.ProcessorCount; i++)
                {
                    tasks.Add(AcceptConnectionsAsync(listener));
                }


                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
            }
            finally
            {
                listener.Close();
                Console.WriteLine("Server stopped");
            }
        }

        private static async Task AcceptConnectionsAsync(HttpListener listener)
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    _ = ProcessRequestAsync(context);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accepting connection: {ex.Message}");
                }
            }
        }

        private static async Task ProcessRequestAsync(HttpListenerContext context)
        {
            await connectionSemaphore.WaitAsync();
            try
            {
                await HandleRequestAsync(context);
            }
            finally
            {
                connectionSemaphore.Release();
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                string method = request.HttpMethod;
                string path = request.Url.AbsolutePath;

              
                if (method != "GET")
                {
                    await SendErrorResponse(response, "501 Not Implemented", 
                        "Only GET requests are supported at this time", false);
                    return;
                }


                bool keepAlive = request.KeepAlive;

                int currentRequest = Interlocked.Increment(ref requestCount);
                if (currentRequest % 10000 == 0)
                {
                    Console.WriteLine($"Processed {currentRequest} requests");
                }
                
   
                await SendBasicResponse(response, path, keepAlive);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling request: {ex.Message}");
                try
                {
                    await SendErrorResponse(context.Response, "500 Internal Server Error", 
                        "An error occurred while processing your request", false);
                }
                catch
                {
                    
                }
            }
        }

        private static async Task SendBasicResponse(HttpListenerResponse response, string path, bool keepAlive)
        {
            string content = $"<html><body><h1>OK</h1><p>{path}</p></body></html>";
            byte[] contentBytes = Encoding.UTF8.GetBytes(content);

            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = contentBytes.Length;
            
    
            response.AddHeader("Connection", keepAlive ? "keep-alive" : "close");
            try
            {
                await response.OutputStream.WriteAsync(contentBytes, 0, contentBytes.Length);
                await response.OutputStream.FlushAsync();
            }
            finally
            {
                response.Close();
            }
        }

        private static async Task SendErrorResponse(HttpListenerResponse response, string statusCode, string message, bool keepAlive)
        {
            string content = $"<html><body><h1>{statusCode}</h1><p>{message}</p></body></html>";
            byte[] contentBytes = Encoding.UTF8.GetBytes(content);

            string[] parts = statusCode.Split(' ');
            response.StatusCode = int.Parse(parts[0]);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = contentBytes.Length;
            response.KeepAlive = keepAlive;

            try
            {
                await response.OutputStream.WriteAsync(contentBytes, 0, contentBytes.Length);
                await response.OutputStream.FlushAsync();
            }
            finally
            {
                response.Close();
            }
        }
    }
}