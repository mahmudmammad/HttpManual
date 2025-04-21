// RawHttpClient.cs
using System;
using System.Buffers; // Required for ArrayPool
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ManualHttpClient
{
 
    public class RawHttpResponse
    {
        public string HttpVersion { get; internal set; }
        public int StatusCode { get; internal set; }
        public string ReasonPhrase { get; internal set; }
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body { get; internal set; }
        public byte[] RawBody { get; internal set; } 

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"HTTP Version: {HttpVersion}");
            sb.AppendLine($"Status Code: {StatusCode} {ReasonPhrase}");
            sb.AppendLine("Headers:");
            foreach (var header in Headers)
            {
                sb.AppendLine($"  {header.Key}: {header.Value}");
            }
            sb.AppendLine("Body:");
            sb.AppendLine(Body ?? "(No body)");
            return sb.ToString();
        }
    }


    public class RawTcpHttpClient
    {
        private readonly string _host;
        private readonly int _port;

        public RawTcpHttpClient(string host, int port)
        {
            
             _host = host == "*" ? "127.0.0.1" : host; 
             if (_host == "localhost") _host = "127.0.0.1"; 

            _port = port;
        }

        public async Task<RawHttpResponse> SendGetRequestAsync(string path, Dictionary<string, string> customHeaders = null)
        {
            
            if (string.IsNullOrEmpty(path)) path = "/";
            if (!path.StartsWith("/")) path = "/" + path;

            using (var client = new TcpClient())
            {
                Console.WriteLine($"Connecting to {_host}:{_port}...");
                try
                {
                    await client.ConnectAsync(_host, _port);
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"Connection failed: {ex.Message}");
                    throw; 
                }
                Console.WriteLine("Connected.");

                using (var stream = client.GetStream())
                {
      
                    var requestBuilder = new StringBuilder();
                    requestBuilder.Append($"GET {path} HTTP/1.1\r\n");
                    requestBuilder.Append($"Host: {_host}:{_port}\r\n"); 
                    requestBuilder.Append("Connection: close\r\n");   
                    requestBuilder.Append("User-Agent: RawTcpHttpClient/1.0\r\n");
                    requestBuilder.Append("Accept: */*\r\n");       
                    if (customHeaders != null)
                    {
                        foreach (var header in customHeaders)
                        {
                            requestBuilder.Append($"{header.Key}: {header.Value}\r\n");
                        }
                    }
                    requestBuilder.Append("\r\n"); 

                    string requestString = requestBuilder.ToString();
                    Console.WriteLine("--- Sending Request ---");
                    Console.Write(requestString); 
                    Console.WriteLine("--- End Request ---");


                    byte[] requestBytes = Encoding.ASCII.GetBytes(requestString);
                    await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
                    await stream.FlushAsync(); 

  
                    Console.WriteLine("Waiting for response...");
              
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(8192); 
                    var responseStream = new MemoryStream();
                    int bytesRead;
                    try
                    {
                      
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await responseStream.WriteAsync(buffer, 0, bytesRead);
                        }
                    }
                    catch (IOException ex)
                    {
             
                         Console.WriteLine($"IO Error reading response: {ex.Message}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer); 
                    }
                    Console.WriteLine($"Received {responseStream.Length} bytes.");

        
                    responseStream.Position = 0; 
                    return ParseHttpResponse(responseStream.ToArray());
                }
            }  
        }

        private RawHttpResponse ParseHttpResponse(byte[] responseBytes)
        {
            var response = new RawHttpResponse();
            if (responseBytes == null || responseBytes.Length == 0)
            {
                Console.WriteLine("Warning: Received empty response.");
                response.StatusCode = 0; 
                response.ReasonPhrase = "Empty Response";
                return response;
            }


        
             byte[] separator = { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
            int headerEndIndex = FindBytes(responseBytes, separator);

            if (headerEndIndex == -1)
            {
                Console.WriteLine("Warning: Could not find header/body separator (\\r\\n\\r\\n). Assuming headers only or malformed response.");
                 headerEndIndex = responseBytes.Length; 
                 response.RawBody = Array.Empty<byte>();
                 response.Body = string.Empty;
            }
            else
            {
 
                 int bodyStartIndex = headerEndIndex + separator.Length;
                 if (bodyStartIndex < responseBytes.Length)
                 {
                     response.RawBody = new byte[responseBytes.Length - bodyStartIndex];
                     Array.Copy(responseBytes, bodyStartIndex, response.RawBody, 0, response.RawBody.Length);
                     try
                     {
                         response.Body = Encoding.UTF8.GetString(response.RawBody);
                     }
                     catch (Exception ex)
                     {
                          Console.WriteLine($"Warning: Could not decode body as UTF-8: {ex.Message}");
                          response.Body = "Could not decode body (see RawBody property).";
                     }
                 }
                 else
                 {
                      response.RawBody = Array.Empty<byte>();
                      response.Body = string.Empty;
                 }
            }



            string headersPartAsString;
            try
            {
                headersPartAsString = Encoding.UTF8.GetString(responseBytes, 0, headerEndIndex);
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"Error decoding headers: {ex.Message}");
                 throw new InvalidDataException("Failed to decode response headers.", ex);
            }

            string[] headerLines = headersPartAsString.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            if (headerLines.Length == 0)
            {
                 Console.WriteLine("Warning: No header lines found in response.");
                 response.StatusCode = 0; // Indicate error
                 response.ReasonPhrase = "No Header Lines";
                 return response;

            }

          
            string statusLine = headerLines[0];
            string[] statusParts = statusLine.Split(' ', 3);
            if (statusParts.Length < 2) 
            {
                 Console.WriteLine($"Warning: Malformed status line: {statusLine}");
                 response.StatusCode = 0; 
                 response.ReasonPhrase = "Malformed Status Line";

                 if (statusParts.Length > 0) response.HttpVersion = statusParts[0];
                 return response;
              
            }

            response.HttpVersion = statusParts[0];
            if (!int.TryParse(statusParts[1], out int statusCode))
            {
                 Console.WriteLine($"Warning: Invalid status code in status line: {statusLine}");
                  response.StatusCode = 0; // Indicate error
                  response.ReasonPhrase = "Invalid Status Code";
                 if (statusParts.Length > 2) response.ReasonPhrase = statusParts[2];
                 return response;

            }
            response.StatusCode = statusCode;
            response.ReasonPhrase = statusParts.Length > 2 ? statusParts[2] : string.Empty; // Phrase is optional


            for (int i = 1; i < headerLines.Length; i++)
            {
                string line = headerLines[i];
                int colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    string key = line.Substring(0, colonIndex).Trim();
                    string value = line.Substring(colonIndex + 1).Trim();

                    
                     if (response.Headers.ContainsKey(key))
                     {
                         response.Headers[key] += ", " + value; 
                     }
                     else
                     {
                         response.Headers[key] = value;
                     }
                }
                else
                {
                    Console.WriteLine($"Warning: Malformed header line ignored: {line}");
                }
            }

            return response;
        }


        private int FindBytes(byte[] haystack, byte[] needle)
        {
            if (needle == null || haystack == null || needle.Length == 0 || haystack.Length < needle.Length)
            {
                return -1;
            }

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    return i;
                }
            }
            return -1;
        }
    }



    class Program
    {
        static async Task Main(string[] args)
        {
            
            string serverHost = "127.0.0.1";
            int serverPort = 8080;       
            string requestPath = "/test/path?query=1"; 

            Console.WriteLine($"Attempting to connect to HTTP server at {serverHost}:{serverPort}");

            var httpClient = new RawTcpHttpClient(serverHost, serverPort);

            try
            {
       
                Console.WriteLine($"Sending GET request for path: {requestPath}");
                RawHttpResponse response = await httpClient.SendGetRequestAsync(requestPath);

                Console.WriteLine("\n--- Received Response ---");
                Console.WriteLine(response.ToString()); 

                

            }
            catch (SocketException sockEx)
            {
                 Console.ForegroundColor = ConsoleColor.Red;
                 Console.WriteLine($"\nNetwork Error: {sockEx.Message}");
                 Console.WriteLine("Ensure the ManualHttpServer is running and configured for the correct host/port.");
                 Console.ResetColor();
            }
            catch (InvalidDataException dataEx)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nResponse Parsing Error: {dataEx.Message}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                 Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nAn unexpected error occurred: {ex.Message}");
                 Console.WriteLine(ex.StackTrace);
                 Console.ResetColor();
            }

            Console.WriteLine("\nClient finished.");
       
        }
    }
}