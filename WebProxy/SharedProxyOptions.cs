using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WebProxy
{
    public class SharedProxyOptions
    {
        private int? _webSocketBufferSize;

        public HttpMessageHandler MessageHandler { get; set; }

        public Func<HttpRequest, HttpRequestMessage, Task> PrepareRequest { get; set; }

        public TimeSpan? WebSocketKeepAliveInterval { get; set; }

        public int? WebSocketBufferSize
        {
            get { return _webSocketBufferSize; }
            set
            {
                if (value.HasValue && value.Value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _webSocketBufferSize = value;
            }
        }
    }
}