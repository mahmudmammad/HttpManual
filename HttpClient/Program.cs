using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;

namespace HttpClientExample
{
    class Program
    {

        
        private static readonly HttpClient client = new HttpClient();

        static async Task Main(string[] args)
        {

            string serverUrl = "http://localhost:8080/";
            
            Console.WriteLine($"HTTP Client starting...");
            Console.WriteLine($"Connecting to server at {serverUrl}");
            

            client.Timeout = TimeSpan.FromSeconds(10);
            
            try
            {

                await GetRequest(serverUrl);
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"HTTP Request error: {e.Message}");
                
     
                if (IsServerRunning("localhost", 8080))
                {
                    Console.WriteLine("Server appears to be running but request failed.");
                    Console.WriteLine("Check server logs for more details.");
                }
                else
                {
                    Console.WriteLine("Server is not running on port 8080.");
                    Console.WriteLine("Please start the server first.");
                }
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("Request timed out. The server is not responding.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"General error: {e.Message}");
            }
            
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        static async Task GetRequest(string url)
        {
            Console.WriteLine($"Sending GET request to {url}");
            
        
            HttpResponseMessage response = await client.GetAsync(url);
          
            Console.WriteLine($"Response status: {(int)response.StatusCode} {response.StatusCode}");
      
            response.EnsureSuccessStatusCode();
            

            string responseBody = await response.Content.ReadAsStringAsync();
            
            Console.WriteLine("\nResponse content:");
            Console.WriteLine(responseBody);
        }
        
        static bool IsServerRunning(string host, int port)
        {
            try
            {
                using (TcpClient tcpClient = new TcpClient())
                {
            
                    var result = tcpClient.BeginConnect(host, port, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                    
                    if (success)
                    {
               
                        tcpClient.EndConnect(result);
                        return true;
                    }
                    
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}