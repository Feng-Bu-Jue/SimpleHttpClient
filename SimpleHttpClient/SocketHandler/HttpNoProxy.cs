﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace SimpleHttpClient
{
    internal sealed class HttpNoProxy : IWebProxy
    {
        public ICredentials Credentials { get; set; }
        public Uri GetProxy(Uri destination) => null;
        public bool IsBypassed(Uri host) => true;
    }
}
