// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.WebApiCompatShim;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Builder.Streaming;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Streaming;
using Microsoft.Bot.Streaming.Transport.WebSockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StreamingBotHttpProxy.Streaming;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Web.Http;

namespace StreamingBotHttpProxy.Controllers
{
    [ApiController]
    public class ProxyController : Controller
    {
        private static string _instanceId = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID");
        private BufferBlock<Tuple<Activity, TaskCompletionSource<ResourceResponse>>> _replyBuffer = 
            new BufferBlock<Tuple<Activity, TaskCompletionSource<ResourceResponse>>>();
        private IHttpClientFactory _httpClientFactory;
        private ICredentialProvider _credentialProvider;
        private IChannelProvider _channelProvider;
        private ILogger<ProxyController> _logger;
        private WebSocketServer _server;
        private Uri _botEndpoint;
        private Uri _serviceEndpoint;
        private string _msaAppId;
        private string _botAppId;
        private ConcurrentDictionary<string, HttpClient> httpClients = new ConcurrentDictionary<string, HttpClient>();

        public ProxyController(IHttpClientFactory httpClientFactory, ILogger<ProxyController> logger, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _credentialProvider = new SimpleCredentialProvider();
            _channelProvider = new SimpleChannelProvider();
            _logger = logger;
            _botEndpoint = new Uri(configuration["BotEndpoint"]);
            _serviceEndpoint = new Uri(configuration["ServiceEndpoint"]);
            _msaAppId = configuration["MicrosoftAppId"];
            _botAppId = configuration["BotAppId"];
        }

        [HttpGet]
        [Route("api/channelmessages")]
        public async Task<IActionResult> ChannelMessages()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await ProcessSocketAsync(webSocket);
                return Ok();
            }

            return BadRequest();
        }


        [HttpPost]
        [Route("api/botmessages/{instanceId}/{routeCounter}")]
        public async Task<IActionResult> BotMessages(string instanceId, int routeCounter)
        {
            if (routeCounter > 1)
            {
                // Node is gone. Return 404, bot should queue messages until the connection is reestablished.
                return NotFound();
            }

            IActionResult result;
            if (instanceId == _instanceId)
            {
                // This is the node with the web socket, send the message
                var activity = await HttpHelper.ReadRequestAsync<Activity>(Request);
                var authHeader = Request.Headers["Authorization"];
                var claimsIdentity = await JwtTokenValidation.AuthenticateRequest(activity, authHeader, _credentialProvider, _channelProvider);

                var resultTcs = new TaskCompletionSource<ResourceResponse>();
                if (!_replyBuffer.Post(Tuple.Create(activity, resultTcs)))
                {
                    return new InternalServerErrorResult();
                }
                var receiveResponse = await resultTcs.Task;
                result = Ok();
            }
            else
            {
                // The websocket is on another node, route to it
                result = await RerouteAsync(instanceId, routeCounter);
            }

            return result;
        }

        private async Task ProcessSocketAsync(WebSocket webSocket)
        {
            var botClient = new BotFrameworkHttpClient(
                _httpClientFactory.CreateClient(),
                new SimpleCredentialProvider());

            var proxyBot = new DelegateStreamingBot(activity =>
            {
                return botClient.PostActivityAsync(
                    _msaAppId, 
                    _botAppId, 
                    _botEndpoint,
                    _serviceEndpoint,
                    activity.Conversation.Id,
                    activity);
            });

            var streamingRequestHandler = new StreamingRequestHandler(proxyBot, proxyBot, webSocket);
            _server = new WebSocketServer(webSocket, streamingRequestHandler);

            var closedTask = _server.StartAsync();

            var buffer = new byte[1024 * 4];

            var cts = new CancellationTokenSource();
            Task<Tuple<Activity, TaskCompletionSource<ResourceResponse>>> bufferReceiveTask = null;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (bufferReceiveTask == null)
                    {
                        bufferReceiveTask = Task.Run(() => _replyBuffer.ReceiveAsync(cts.Token));
                    }

                    await Task.WhenAny(closedTask, bufferReceiveTask);

                    // Send from bot to socket
                    if (bufferReceiveTask.IsCompleted)
                    {
                        var activity = bufferReceiveTask.Result.Item1;
                        var tcs = bufferReceiveTask.Result.Item2;
                        bufferReceiveTask = null;

                        var response = await streamingRequestHandler.SendActivityAsync(activity);
                        tcs.SetResult(response);
                    }

                    if (closedTask.IsCompleted)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _server.Disconnect();
                cts.Cancel();

                if (bufferReceiveTask != null)
                {
                    try
                    {
                        await bufferReceiveTask;
                    }
                    catch (TaskCanceledException)
                    {
                    }
                }

                if (closedTask != null)
                {
                    try
                    {
                        await closedTask;
                    }
                    catch (TaskCanceledException)
                    {
                    }
                }
            }
        }

        private async Task<IActionResult> RerouteAsync(string instanceId, int routeCounter)
        {
            var uri = Request.GetEncodedUrl();

            if (routeCounter >= 1)
            {
                // Stop the routing and return NotFound error
                // as the target instanceID can't match the current instance after re-routing once.
                _logger.LogWarning($"incorrect re-route uri {uri.ToString()}");

                return NotFound();
            }

            // Create a new callback uri with new routeCounter value.
            var newUri = new Uri(uri.Substring(0, uri.LastIndexOf('/') + 1) + (routeCounter + 1).ToString());

            // Create new request message based on the new uri.
            var newRequestMessage = HttpContext.GetHttpRequestMessage();
            newRequestMessage.RequestUri = newUri;

            // Remove the content to make httpClient.SendAsync check that.
            var requestMethod = Request.Method.Normalize();
            if (requestMethod != HttpMethods.Post && requestMethod != HttpMethods.Put && requestMethod != HttpMethods.Patch)
            {
                newRequestMessage.Content = null;
            }

            // Get or create a http client for the specific web instance.
            if (!this.httpClients.TryGetValue(instanceId, out HttpClient httpClient))
            {
                var cookieContainer = new CookieContainer();
                var httpClientHandler = new HttpClientHandler() { CookieContainer = cookieContainer };
                cookieContainer.Add(newUri, new Cookie("ARRAffinity", instanceId));
                var newHttpClient = new HttpClient(httpClientHandler);

                httpClient = this.httpClients.AddOrUpdate(instanceId, newHttpClient, (k, v) => v);
            }

            var responseMessage = await httpClient.SendAsync(newRequestMessage);

            var response = Response;

            // set content back to HttpContext.Response.
            if (responseMessage.Content != null)
            {
                return new ContentResult()
                {
                    Content = await responseMessage.Content.ReadAsStringAsync(),
                    ContentType = responseMessage.Content.Headers.ContentType?.ToString(),
                    StatusCode = (int)responseMessage.StatusCode
                };
            }
            else
            {
                return new StatusCodeResult((int)responseMessage.StatusCode);
            }
        }
    }
}
