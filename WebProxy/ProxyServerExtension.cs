using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WebProxy
{
    public static class ProxyServerExtension
    {
        private static readonly string[] NotForwardedWebSocketHeaders =
        {
            "Connection", "Host", "Upgrade", "Sec-WebSocket-Accept", "Sec-WebSocket-Protocol", "Sec-WebSocket-Key",
            "Sec-WebSocket-Version", "Sec-WebSocket-Extensions"
        };
        private const int DefaultWebSocketBufferSize = 4096;
        private const int StreamCopyBufferSize = 81920;

        public static void RunProxy(this IApplicationBuilder app, Uri baseUri)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }
            if (baseUri == null)
            {
                throw new ArgumentNullException(nameof(baseUri));
            }

            var options = new ProxyOptions
            {
                Scheme = baseUri.Scheme,
                Host = new HostString(baseUri.Authority),
                PathBase = baseUri.AbsolutePath,
                AppendQuery = new QueryString(baseUri.Query)
            };
            app.UseMiddleware<ProxyServer>(Options.Create(options));
        }

        public static HttpRequestMessage CreateProxyHttpRequest(this HttpContext context, Uri uri)
        {
            var request = context.Request;
            var requestMessage = new HttpRequestMessage();
            var requestMethod = request.Method;
            if (!HttpMethods.IsGet(requestMethod) &&
                !HttpMethods.IsHead(requestMethod) &&
                !HttpMethods.IsDelete(requestMethod) &&
                !HttpMethods.IsTrace(requestMethod))
            {
                requestMessage.Content = new StreamContent(request.Body);
            }

            foreach (var header in request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            requestMessage.Headers.Host = uri.Authority;
            requestMessage.RequestUri = uri;
            requestMessage.Method = new HttpMethod(requestMethod);

            return requestMessage;
        }

        public static async Task<bool> AcceptProxyWebSocketRequestAsync(this HttpContext context, Uri destinationUri)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (destinationUri == null)
            {
                throw new ArgumentNullException(nameof(destinationUri));
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                throw new InvalidOperationException();
            }

            var proxyService = context.RequestServices.GetRequiredService<ProxyService>();

            using (var client = new ClientWebSocket())
            {
                foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
                {
                    client.Options.AddSubProtocol(protocol);
                }

                foreach (var header in context.Request.Headers)
                {
                    if (!NotForwardedWebSocketHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        client.Options.SetRequestHeader(header.Key, header.Value);
                    }
                }

                if (proxyService.Options.WebSocketKeepAliveInterval.HasValue)
                {
                    client.Options.KeepAliveInterval = proxyService.Options.WebSocketKeepAliveInterval.Value;
                }

                try
                {
                    await client.ConnectAsync(destinationUri, context.RequestAborted);
                }
                catch (WebSocketException)
                {
                    context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
                    return false;
                }

                using (var server = await context.WebSockets.AcceptWebSocketAsync(client.SubProtocol))
                {
                    var bufferSize = proxyService.Options.WebSocketBufferSize ?? DefaultWebSocketBufferSize;
                    await Task.WhenAll(
                        PumpWebSocket(client, server, bufferSize, context.RequestAborted),
                        PumpWebSocket(server, client, bufferSize, context.RequestAborted));
                }

                return true;
            }
        }

        private static async Task PumpWebSocket(WebSocket source, WebSocket destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (bufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferSize));
            }

            var buffer = new byte[bufferSize];
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                }
                catch (Exception e)
                {
                    await destination.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, cancellationToken);
                    return;
                }
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (source.CloseStatus != null)
                        await destination.CloseOutputAsync(source.CloseStatus.Value, source.CloseStatusDescription, cancellationToken);
                }
                await destination.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
            }
        }

        public static Task<HttpResponseMessage> SendProxyHttpRequest(this HttpContext context, HttpRequestMessage requestMessage)
        {
            if (requestMessage == null)
            {
                throw new ArgumentNullException(nameof(requestMessage));
            }

            var proxyService = context.RequestServices.GetRequiredService<ProxyService>();
            return proxyService.Client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }

        public static async Task CopyProxyHttpResponse(this HttpContext context, HttpResponseMessage responseMessage)
        {
            if (responseMessage == null)
            {
                throw new ArgumentNullException(nameof(responseMessage));
            }

            var response = context.Response;
            response.StatusCode = (int)responseMessage.StatusCode;
            foreach (var header in responseMessage.Headers)
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var contentHeader in responseMessage.Content.Headers)
            {
                response.Headers[contentHeader.Key] = contentHeader.Value.ToArray();
            }

            response.Headers.Remove("transfer-encoding");

            using (var responseStream = await responseMessage.Content.ReadAsStreamAsync())
            {
                var headers = responseMessage.Content.Headers;
                if (headers.ContentEncoding.Contains("gzip") &&
                    headers.ContentType.MediaType == "text/html")
                {
                    string str;
                    using (var gZipStream = new GZipStream(responseStream, CompressionMode.Decompress))
                    using (var streamReader = new StreamReader(gZipStream))
                    {
                        str = await streamReader.ReadToEndAsync();
                    }
                    str = ChangeContent(str);
                    var bytes = Encoding.UTF8.GetBytes(str);
                    using (var ms = new MemoryStream(bytes))
                    using (var gZipStream = new GZipStream(response.Body, CompressionMode.Compress))
                    {
                        await ms.CopyToAsync(gZipStream, StreamCopyBufferSize, context.RequestAborted);
                    }
                }
                else
                {
                    await responseStream.CopyToAsync(response.Body, StreamCopyBufferSize, context.RequestAborted);
                }
            }
        }

        private static string ChangeContent(string str)
        {
            var regEx = new Regex(@"https://habr\.com");
            var result = regEx.Replace(str, "http://localhost:58192");
            var regEx2 = new Regex(@"habr\.com");
            var result2 = regEx2.Replace(result, "localhost:58192");
            
            var doc = new HtmlDocument();
            doc.LoadHtml(result2);
            var layout = doc.DocumentNode.Descendants("div").First(e => e.GetAttributeValue("class", "").Equals("layout"));
            AddTrademark(layout);
            return doc.DocumentNode.OuterHtml;
        }

        private static void AddTrademark(HtmlNode htmlNode)
        {
            if (htmlNode.HasChildNodes /*&& htmlNode.InnerText == ""*/)
            {
                for (var i = 0; i < htmlNode.ChildNodes.Count; i++)
                {
                    AddTrademark(htmlNode.ChildNodes[i]);
                }
            }
            var regEx = new Regex(@"(^|\s+)\p{L}{6}(\s|\.|\!|\?|\,|\:|\;|\n|$)");

            if (!htmlNode.InnerText.Contains("™"))
            {
                htmlNode.ParentNode.ReplaceChild(
                        HtmlNode.CreateNode(regEx.Replace(htmlNode.OuterHtml,
                            e => e.Value.Insert(SelectPosition(e.Value), "™"))), htmlNode); 
            }
        }

        private static int SelectPosition(string str)
        {
            switch (str.Length)
            {
                case 6:
                    return str.Length;
                case 7:
                    return str.TrimStart().Length == str.Length ? str.Length - 1 : str.Length;
                //case 8:
                //    return str.Length - 1;
                default:
                    return str.Length - 1;
            }
        }
    }
}