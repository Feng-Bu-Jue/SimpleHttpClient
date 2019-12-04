﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleHttpClient
{
    internal sealed class HttpConnectionPoolManager : IDisposable
    {
        private readonly ConcurrentDictionary<HttpConnectionKey, HttpConnectionPool> _pools;

        internal readonly HttpConnectionSettings settings;

        private bool disposed;
        private object SyncObj => _pools;

        public HttpConnectionPoolManager(HttpConnectionSettings settings)
        {
            _pools = new ConcurrentDictionary<HttpConnectionKey, HttpConnectionPool>();
            this.settings = settings;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = (HttpConnectionKey)request;
            HttpConnectionPool pool;
            while (!_pools.TryGetValue(key, out pool))
            {
                _pools.TryAdd(key, new HttpConnectionPool(key.Kind, key.Host, key.SslHostName, key.Port, this));
            }
            return pool.SendAsync(request, cancellationToken);
        }

        private void Dispose(bool disposing)
        {
            if (!disposed && disposing)
            {
                foreach (var pool in _pools)
                {
                    pool.Value.Dispose();
                }
                disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
