using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WebProxy
{
    public class ProxyServer
    {
        private readonly RequestDelegate _next;
        private readonly ProxyOptions _options;

        public ProxyServer(RequestDelegate next, IOptions<ProxyOptions> options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (options.Value.Scheme == null)
            {
                throw new ArgumentException("Options parameter must specify scheme.", nameof(options));
            }
            if (!options.Value.Host.HasValue)
            {
                throw new ArgumentException("Options parameter must specify host.", nameof(options));
            }

            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options.Value;
        }

        public async Task Invoke(HttpContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var uri = new Uri(UriHelper.BuildAbsolute(_options.Scheme, _options.Host, _options.PathBase,
                context.Request.Path, context.Request.QueryString.Add(_options.AppendQuery)));
            await ProxyRequestAsync(context, uri);
        }

        private static async Task ProxyRequestAsync(HttpContext context, Uri destinationUri)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (destinationUri == null)
            {
                throw new ArgumentNullException(nameof(destinationUri));
            }

            if (context.WebSockets.IsWebSocketRequest)
            {
                await context.AcceptProxyWebSocketRequestAsync(destinationUri);
            }
            else
            {
                var proxyService = context.RequestServices.GetRequiredService<ProxyService>();

                using (var requestMessage = context.CreateProxyHttpRequest(destinationUri))
                {
                    var prepareRequestHandler = proxyService.Options.PrepareRequest;
                    if (prepareRequestHandler != null)
                    {
                        await prepareRequestHandler(context.Request, requestMessage);
                    }

                    using (var responseMessage = await context.SendProxyHttpRequest(requestMessage))
                    {
                        await context.CopyProxyHttpResponse(responseMessage);
                    }
                }
            }
        }
    }
}