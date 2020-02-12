// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Streaming;
using Microsoft.Bot.Schema;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace StreamingBotHttpProxy.Streaming
{
    public class DelegateStreamingBot : IBot, IStreamingActivityProcessor
    {
        private Func<Activity, Task<InvokeResponse>> _handler;

        public DelegateStreamingBot(Func<Activity, Task<InvokeResponse>> handler)
        {
            _handler = handler;
        }

        ////public async override Task<StreamingResponse> ProcessRequestAsync(
        ////    ReceiveRequest request,
        ////    ILogger<RequestHandler> logger,
        ////    object context = null,
        ////    CancellationToken cancellationToken = default)
        ////{
        ////    var invokeResponse = await _botClient.PostActivityAsync(
        ////        "fromBotId",
        ////        "toBotId",
        ////        new Uri("toUrl"),
        ////        new Uri("serviceUrl"),
        ////        "conversationId",
        ////        new Microsoft.Bot.Schema.Activity()
        ////        {
        ////        },
        ////        cancellationToken);

        ////    var bodyContent = new StringContent(
        ////        JObject.FromObject(invokeResponse.Body).ToString(),
        ////        Encoding.UTF8,
        ////        "application/json"
        ////    );

        ////    return StreamingResponse.CreateResponse((HttpStatusCode)invokeResponse.Status, bodyContent);
        ////}

        public Task<InvokeResponse> ProcessStreamingActivityAsync(Activity activity, BotCallbackHandler botCallbackHandler, CancellationToken cancellationToken = default)
        {
            return _handler(activity);
        }

        public Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
