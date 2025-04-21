using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace ManualHttpServer
{
    class Program
    {
        private static SemaphoreSlim connectionSemaphore;
        private static int keepAliveTimeout;
        private static int port;
        private static string host;
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
            host = configuration.GetValue<string>("Server:Host", "127.0.0.1");
            int maxConnections = configuration.GetValue<int>("Server:MaxConnections", 1000); // Increased default
            keepAliveTimeout = configuration.GetValue<int>("Server:KeepAliveTimeoutMs", 15000);
            
            connectionSemaphore = new SemaphoreSlim(initialCount: maxConnections, maxCount: maxConnections);
            
            IPAddress ipAddress = host.Equals("*") ? IPAddress.Any : IPAddress.Parse(host);
            TcpListener listener = new TcpListener(ipAddress, port);
            
            try
            {
                listener.Start();
                
                Console.WriteLine($"HTTP Server started on {(host.Equals("*") ? "all interfaces" : host)}:{port}");
                Console.WriteLine($"Configuration: MaxConnections={maxConnections}, KeepAliveTimeout={keepAliveTimeout}ms");
                
                while (true)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    client.NoDelay = true; // Disable Nagle's algorithm
                    client.SendBufferSize = 16384;
                    client.ReceiveBufferSize = 16384;
                    
                    _ = Task.Run(async () => 
                    {
                        await connectionSemaphore.WaitAsync();
                        try 
                        {
                            await HandleConnectionAsync(client);
                        }
                        finally 
                        {
                            connectionSemaphore.Release();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
            }
            finally
            {
                listener.Stop();
                Console.WriteLine("Server stopped");
            }
        }
        
        static async Task HandleConnectionAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    bool keepConnectionAlive = true;
                    
                    while (keepConnectionAlive)
                    {
                        using (var timeoutCts = new CancellationTokenSource(keepAliveTimeout))
                        {
                            byte[] buffer = ArrayPool<byte>.Shared.Rent(8192); 
                            
                            try
                            {
                                keepConnectionAlive = false;
                                
                                int totalBytesRead = 0;
                                int bytesRead;
                                bool headersComplete = false;
                                
                                while (!headersComplete && totalBytesRead < buffer.Length)
                                {
                                    try
                                    {
                                        bytesRead = await stream.ReadAsync(buffer, totalBytesRead, buffer.Length - totalBytesRead, timeoutCts.Token);
                                        
                                        if (bytesRead == 0)
                                        {
               
                                            return;
                                        }
                                        
                                        totalBytesRead += bytesRead;
                                        
                                        for (int i = 0; i < totalBytesRead - 3; i++)
                                        {
                                            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && 
                                                buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                                            {
                                                headersComplete = true;
                                                break;
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                
                                        return;
                                    }
                                    catch (IOException)
                                    {
                                     
                                        return;
                                    }
                                }
                                
                                if (!headersComplete)
                                {
                                    await SendErrorResponse(stream, "400 Bad Request", "Request headers too large", false);
                                    return;
                                }
                                
                                int headerEndPos = 0;
                                for (int i = 0; i < totalBytesRead - 3; i++)
                                {
                                    if (buffer[i] == '\r' && buffer[i + 1] == '\n' && 
                                        buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                                    {
                                        headerEndPos = i + 3; 
                                        break;
                                    }
                                }
                                
                                string headerString = Encoding.ASCII.GetString(buffer, 0, headerEndPos + 1);
                                string[] headerLines = headerString.Split(new[] { "\r\n" }, StringSplitOptions.None);
                                
                                if (headerLines.Length == 0)
                                {
                                    await SendErrorResponse(stream, "400 Bad Request", "Missing request line", false);
                                    return;
                                }
                                
                                string requestLine = headerLines[0];
                                string[] requestParts = requestLine.Split(' ');
                                
                                if (requestParts.Length < 3)
                                {
                                    await SendErrorResponse(stream, "400 Bad Request", "Malformed request line", false);
                                    return;
                                }
                                
                                string method = requestParts[0];
                                string path = requestParts[1];
                                string httpVersion = requestParts[2];
                                
                                if (method != "GET")
                                {
                                    await SendErrorResponse(stream, "501 Not Implemented", 
                                        "Only GET requests are supported at this time", false);
                                    return;
                                }
                                
                                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                
                                for (int i = 1; i < headerLines.Length; i++)
                                {
                                    string line = headerLines[i];
                              
                                    if (string.IsNullOrWhiteSpace(line))
                                        continue;
                                    
                                    int colonPos = line.IndexOf(':');
                                    if (colonPos > 0)
                                    {
                                        string key = line.Substring(0, colonPos).Trim();
                                        string value = line.Substring(colonPos + 1).Trim();
                    
                                        if (headers.ContainsKey(key))
                                        {
                                            headers[key] += ", " + value;
                                        }
                                        else
                                        {
                                            headers[key] = value;
                                        }
                                    }
                                }
                                
                                if (headers.TryGetValue("Connection", out string connectionValue))
                                {
                                    keepConnectionAlive = connectionValue.Equals("keep-alive", StringComparison.OrdinalIgnoreCase);
                                }
                                else if (httpVersion.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase))
                                {
                                    keepConnectionAlive = true;
                                }
                                
                                int currentRequest = Interlocked.Increment(ref requestCount);
                                if (currentRequest % 10000 == 0)
                                {
                                    Console.WriteLine($"Processed {currentRequest} requests");
                                }
                                
                                await SendBasicResponse(stream, path, keepConnectionAlive);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        } 
                    }
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is SocketException))
                    {
                        Console.WriteLine($"Error handling client: {ex.Message}");
                    }
                    
                    try
                    {
                        await SendErrorResponse(client.GetStream(), "500 Internal Server Error", 
                            "An error occurred while processing your request", false);
                    }
                    catch
                    {
                        // Ignore errors when sending error responses
                    }
                }
            }
        }
        
        static async Task SendBasicResponse(NetworkStream stream, string path, bool keepAlive)
        {
            string content = $"<html><body><h1>OK</h1><p>{path}</p></body></html>";
            byte[] contentBytes = Encoding.UTF8.GetBytes(content);
            
            // Pre-allocate for better performance
            byte[] response = new byte[512 + contentBytes.Length];
            int offset = 0;
            
            
            string headers = 
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {contentBytes.Length}\r\n" +
                $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n" +
                (keepAlive ? $"Keep-Alive: timeout={keepAliveTimeout / 1000}\r\n" : "") +
                "\r\n";
                
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            Buffer.BlockCopy(headerBytes, 0, response, offset, headerBytes.Length);
            offset += headerBytes.Length;
            
            Buffer.BlockCopy(contentBytes, 0, response, offset, contentBytes.Length);
            offset += contentBytes.Length;
            
            await stream.WriteAsync(response, 0, offset);
            await stream.FlushAsync();
        }
        
        static async Task SendErrorResponse(NetworkStream stream, string statusCode, string message, bool keepAlive)
        {
            string content = $"<html><body><h1>{statusCode}</h1><p>{message}</p></body></html>";
            byte[] contentBytes = Encoding.UTF8.GetBytes(content);
            
            // Prepare the complete response in a single buffer
            string headers = 
                $"HTTP/1.1 {statusCode}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {contentBytes.Length}\r\n" +
                "Connection: close\r\n" +
                "\r\n";
                
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            byte[] response = new byte[headerBytes.Length + contentBytes.Length];
            
            Buffer.BlockCopy(headerBytes, 0, response, 0, headerBytes.Length);
            Buffer.BlockCopy(contentBytes, 0, response, headerBytes.Length, contentBytes.Length);
            
            await stream.WriteAsync(response, 0, response.Length);
            await stream.FlushAsync();
        }
    }
}