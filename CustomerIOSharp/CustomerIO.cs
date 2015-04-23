﻿using System;
using System.Net;
using System.Threading.Tasks;
using RestSharp.Portable;
using RestSharp.Portable.Authenticators;
using Method = System.Net.Http.HttpMethod;
using System.Text;
using System.Linq;
using Newtonsoft.Json;
using System.Threading;

namespace CustomerIOSharp
{
    #region AsyncLock class
    
    // see https://github.com/FubarDevelopment/restsharp.portable/issues/25
    public sealed class AsyncLock
    {
        private readonly SemaphoreSlim m_semaphore = new SemaphoreSlim(1, 1);
        private readonly Task<IDisposable> m_releaser;

        public AsyncLock()
        {
            m_releaser = Task.FromResult((IDisposable)new Releaser(this));
        }

        public Task<IDisposable> LockAsync()
        {
            var wait = m_semaphore.WaitAsync();
            return wait.IsCompleted ?
                m_releaser :
                wait.ContinueWith((_, state) => (IDisposable)state,
                    m_releaser.Result, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private sealed class Releaser : IDisposable
        {
            private readonly AsyncLock m_toRelease;
            internal Releaser(AsyncLock toRelease) { m_toRelease = toRelease; }
            public void Dispose() { m_toRelease.m_semaphore.Release(); }
        }
    }

    #endregion

    public class CustomerIo
    {
        private readonly string _siteId;
        private readonly string _apiKey;
        private readonly ICustomerFactory _customerFactory;

        private const string Endpoint = "https://track.customer.io/api/v1/";

        private const string MethodCustomer = "customers/{customer_id}";
        private const string MethodTrack = "customers/{customer_id}/events";

        private readonly RestClient _client;
        private readonly AsyncLock _lock = new AsyncLock ();

        public CustomerIo(string siteId, string apiKey, ICustomerFactory customerFactory)
        {
            _siteId = siteId;
            _apiKey = apiKey;
            _customerFactory = customerFactory;

            _client = new RestClient(Endpoint)
                {
                    Authenticator = new FixedHttpBasicAuthenticator(_siteId, _apiKey)
                };
        }

        private async Task CallMethodAsync(string method, string customerId, Method httpMethod, object data)
        {
            // do not transmit events if we do not have a customer id
            if (customerId == null) return;

            var request = new RestRequest(method)
            {
                Method = httpMethod,
                //RequestFormat = DataFormat.Json,
                Serializer = new JsonSerializer()
            };
            request.AddUrlSegment(@"customer_id", customerId);
            request.AddBody(data);
            
            IRestResponse response;
            using (await _lock.LockAsync ().ConfigureAwait (false))
            {
                response = await _client.Execute(request).ConfigureAwait (false);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new CustomerIoApiException(response.StatusCode);
            }
        }

        public Task IdentifyAsync()
        {
            var id = _customerFactory.GetCustomerId();
            return CallMethodAsync(MethodCustomer, id, Method.Put, _customerFactory.GetCustomerDetails());
        }

        public Task DeleteCustomerAsync()
        {
            var id = _customerFactory.GetCustomerId();
            return CallMethodAsync(MethodCustomer, id, Method.Delete, null);
        }

        /// <summary>
        /// Track a custom event.
        /// </summary>
        /// <param name="eventName">The name of the event you want to track</param>
        /// <param name="data">Any related information you’d like to attach to this event. These attributes can be used in your triggers to control who should receive the triggered email. You can set any number of data key and values.</param>
        /// <param name="timestamp">Allows you to back-date the event, pass null to use current time</param>
        /// <returns>Nothing if successful, throws if failed</returns>
        /// <exception cref="CustomerIoApiException">If any code besides 200 OK is returned from the server.</exception>
        public Task TrackEventAsync(string eventName, object data = null, DateTime? timestamp = null)
        {
            var id = _customerFactory.GetCustomerId();

            var wrappedData = new TrackedEvent
                {
                    Name = eventName,
                    Data = data,
                    Timestamp = timestamp
                };

            return CallMethodAsync(MethodTrack, id, Method.Post, wrappedData);
        }
    }

    //[SerializeAs(NameStyle = NameStyle.CamelCase)]
    public class TrackedEvent
    {
        public string Name { get; set; }
        public object Data { get; set; }
        public DateTime? Timestamp { get; set; }
    }

    // NOTE
    // Default HttpBasicAuthenticator crashes in Authenticate method with NullReferenceException
    // because it does not check parameter's Name for null (see parameters.Any (...) call)
    // This class fixes the issue
    class FixedHttpBasicAuthenticator : IAuthenticator
    {
        private readonly string _authHeader;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpBasicAuthenticator" /> class.
        /// </summary>
        /// <param name="username">User name</param>
        /// <param name="password">The users password</param>
        public FixedHttpBasicAuthenticator(string username, string password)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Format("{0}:{1}", username, password)));
            _authHeader = string.Format("Basic {0}", token);
        }

        /// <summary>
        /// Modifies the request to ensure that the authentication requirements are met.
        /// </summary>
        /// <param name="client">Client executing this request</param>
        /// <param name="request">Request to authenticate</param>
        public void Authenticate(IRestClient client, IRestRequest request)
        {
            // only add the Authorization parameter if it hasn't been added by a previous Execute
            if (request.Parameters.Any(p => p.Name != null && p.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
                return;
            request.AddParameter("Authorization", _authHeader, ParameterType.HttpHeader);
        }
    }

}
