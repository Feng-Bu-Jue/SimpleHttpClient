﻿using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleHttpClient
{
    public class HttpClient : HttpMessageInvoker
    {
        private CancellationTokenSource _pendingRequestsCts;
        private volatile bool _disposed;

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);
        private static readonly TimeSpan s_infiniteTimeout = System.Threading.Timeout.InfiniteTimeSpan;

        public HttpClient()
            : this(new HttpClientHandler())
        {

        }

        public HttpClient(HttpMessageHandler handler) :
            this(handler, true)
        {
        }

        public HttpClient(HttpMessageHandler handler, bool disposeHandler) : base(handler, disposeHandler)
        {
            _pendingRequestsCts = new CancellationTokenSource();
        }


        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            return SendAsync(request, CancellationToken.None);
        }

        public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CancellationTokenSource cts;

            bool disposeCts;
            var hasTimeout = this.Timeout != s_infiniteTimeout;
            if (hasTimeout || cancellationToken.CanBeCanceled)
            {
                disposeCts = true;
                cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _pendingRequestsCts.Token);
                if (hasTimeout)
                {
                    cts.CancelAfter(Timeout);
                }
            }
            else
            {
                disposeCts = false;
                cts = _pendingRequestsCts;
            }

            return FinishSendAsync(base.SendAsync(request, cts.Token), request, cts, disposeCts);
        }

        private async Task<HttpResponseMessage> FinishSendAsync(Task<HttpResponseMessage> sendTask, HttpRequestMessage request, CancellationTokenSource cts, bool disposeCts)
        {
            HttpResponseMessage response = null;
            try
            {
                response = await sendTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                response?.Dispose();
                throw;
            }

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;

                _pendingRequestsCts.Cancel();
                _pendingRequestsCts.Dispose();
            }

            base.Dispose(disposing);
        }

    }
}
