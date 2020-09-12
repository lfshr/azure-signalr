﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR.Common;
using Microsoft.Azure.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.SignalR.Management
{
    internal class ServiceManager : IServiceManager
    {
        private readonly ServiceManagerOptions _serviceManagerOptions;
        private readonly ServiceEndpointProvider _endpointProvider;
        private readonly IServerNameProvider _serverNameProvider;
        private readonly ServiceEndpoint _endpoint;
        private readonly string _productInfo;
        private readonly RestClient _restClient;
        private readonly RestApiProvider _restApiProvider;

        internal ServiceManager(ServiceManagerOptions serviceManagerOptions, string productInfo)
        {
            _serviceManagerOptions = serviceManagerOptions;
            _endpoint = new ServiceEndpoint(_serviceManagerOptions.ConnectionString, EndpointType.Secondary);
            _serverNameProvider = new DefaultServerNameProvider();

            var serviceOptions = Options.Create(new ServiceOptions
            {
                ApplicationName = _serviceManagerOptions.ApplicationName,
                Proxy = serviceManagerOptions.Proxy
            }).Value;

            _endpointProvider = new ServiceEndpointProvider(_serverNameProvider, _endpoint, serviceOptions);

            _productInfo = productInfo;
            _restClient = new RestClient();
            _restApiProvider = new RestApiProvider(_serviceManagerOptions.ConnectionString);
        }

        public async Task<IServiceHubContext> CreateHubContextAsync(string hubName, ILoggerFactory loggerFactory = null, CancellationToken cancellationToken = default)
        {
            loggerFactory ??= NullLoggerFactory.Instance;
            switch (_serviceManagerOptions.ServiceTransportType)
            {
                case ServiceTransportType.Persistent:
                    {
                        var success = false;
                        var serviceProvider = BuildPersistentServiceProvider(hubName, loggerFactory);
                        try
                        {
                            var serviceConnectionManager = serviceProvider.GetRequiredService<IServiceConnectionManager<Hub>>();
                            var weakConnectionContainer = serviceProvider.GetRequiredService<IServiceConnectionContainer>();

                            serviceConnectionManager.SetServiceConnection(weakConnectionContainer);
                            _ = serviceConnectionManager.StartAsync();

                            // wait until service connection established
                            await weakConnectionContainer.ConnectionInitializedTask.OrTimeout(cancellationToken);

                            var webSocketsHubLifetimeManager = (WebSocketsHubLifetimeManager<Hub>)serviceProvider.GetRequiredService<HubLifetimeManager<Hub>>();

                            var hubContext = serviceProvider.GetRequiredService<IHubContext<Hub>>();
                            var serviceHubContext = new ServiceHubContext(hubContext, webSocketsHubLifetimeManager, serviceProvider);
                            success = true;
                            return serviceHubContext;
                        }
                        finally
                        {
                            if (!success)
                            {
                                serviceProvider?.Dispose();
                            }
                        }
                    }
                case ServiceTransportType.Transient:
                    {
                        var serviceProvider = BuildTransientServiceProvider(hubName, loggerFactory);
                        var hubContext = serviceProvider.GetRequiredService<IHubContext<Hub>>();
                        var restHubLifetimeManager = (RestHubLifetimeManager) serviceProvider.GetRequiredService<HubLifetimeManager<Hub>>();
                        return new ServiceHubContext(hubContext, restHubLifetimeManager, serviceProvider);
                    }
                default:
                    throw new ArgumentException("Not supported service transport type.");
            }
        }

        public async Task<IServiceHubContext<T>> CreateHubContextAsync<T>(string hubName, ILoggerFactory loggerFactory = null, CancellationToken cancellationToken = default) 
            where T : class
        {
            loggerFactory ??= NullLoggerFactory.Instance;
            switch (_serviceManagerOptions.ServiceTransportType)
            {
                case ServiceTransportType.Persistent:
                    {
                        var success = false;
                        var serviceProvider = BuildPersistentServiceProvider(hubName, loggerFactory);
                        try
                        {
                            var serviceConnectionManager = serviceProvider.GetRequiredService<IServiceConnectionManager<Hub>>();
                            var weakConnectionContainer = serviceProvider.GetRequiredService<IServiceConnectionContainer>();

                            serviceConnectionManager.SetServiceConnection(weakConnectionContainer);
                            _ = serviceConnectionManager.StartAsync();

                            // wait until service connection established
                            await weakConnectionContainer.ConnectionInitializedTask.OrTimeout(cancellationToken);

                            var webSocketsHubLifetimeManager = (WebSocketsHubLifetimeManager<Hub>)serviceProvider.GetRequiredService<HubLifetimeManager<Hub>>();

                            var hubContext = serviceProvider.GetRequiredService<IHubContext<Hub<T>, T>>();
                            var serviceHubContext = new ServiceHubContext<T>(hubContext, webSocketsHubLifetimeManager, serviceProvider);
                            success = true;
                            return serviceHubContext;
                        }
                        finally
                        {
                            if (!success)
                            {
                                serviceProvider?.Dispose();
                            }
                        }
                    }
                case ServiceTransportType.Transient:
                    {
                        var serviceProvider = BuildTransientServiceProvider(hubName, loggerFactory);
                        var hubContext = serviceProvider.GetRequiredService<IHubContext<Hub<T>, T>>();
                        var restHubLifetimeManager = (RestHubLifetimeManager)serviceProvider.GetRequiredService<HubLifetimeManager<Hub>>();
                        return new ServiceHubContext<T>(hubContext, restHubLifetimeManager, serviceProvider);
                    }
                default:
                    throw new ArgumentException("Not supported service transport type.");
            }
        }

        public ServiceProvider BuildPersistentServiceProvider(string hubName, ILoggerFactory loggerFactory)
        {
            var serviceCollection = new ServiceCollection();

            var connectionFactory = new ManagementConnectionFactory(_productInfo, new ConnectionFactory(_serverNameProvider, loggerFactory));
            var serviceProtocol = new ServiceProtocol();
            var clientConnectionManager = new ClientConnectionManager();
            var clientConnectionFactory = new ClientConnectionFactory();
            ConnectionDelegate connectionDelegate = connectionContext => Task.CompletedTask;
            var serviceConnectionFactory = new ServiceConnectionFactory(
                serviceProtocol,
                clientConnectionManager,
                connectionFactory,
                loggerFactory,
                connectionDelegate,
                clientConnectionFactory,
                new DefaultServerNameProvider()
                );
            var weakConnectionContainer = new WeakServiceConnectionContainer(
                serviceConnectionFactory,
                _serviceManagerOptions.ConnectionCount,
                new HubServiceEndpoint(hubName, _endpointProvider, _endpoint),
                loggerFactory?.CreateLogger(nameof(WeakServiceConnectionContainer)) ?? NullLogger.Instance);

            serviceCollection.AddSignalRCore();
            serviceCollection.AddSingleton<IConfigureOptions<HubOptions>, ManagementHubOptionsSetup>();

            if (loggerFactory != null)
            {
                serviceCollection.AddSingleton(typeof(ILoggerFactory), loggerFactory);
            }

            serviceCollection
                .AddLogging()
                .AddSingleton(typeof(IConnectionFactory), sp => connectionFactory)
                .AddSingleton(typeof(HubLifetimeManager<>), typeof(WebSocketsHubLifetimeManager<>))
                .AddSingleton(typeof(IServiceConnectionManager<>), typeof(ServiceConnectionManager<>))
                .AddSingleton(typeof(IServiceConnectionContainer), sp => weakConnectionContainer);

            return serviceCollection.BuildServiceProvider();
        }

        public ServiceProvider BuildTransientServiceProvider(string hubName, ILoggerFactory loggerFactory)
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSignalRCore();

            // remove default hub lifetime manager
            var serviceDescriptor = serviceCollection.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(HubLifetimeManager<>));
            serviceCollection.Remove(serviceDescriptor);

            // add rest hub lifetime manager
            // TODO: Tests will fail, must update RestHubLifetimeManager to support Hub<T> or open generic
            var restHubLifetimeManager = new RestHubLifetimeManager(_serviceManagerOptions, hubName, _productInfo);
            serviceCollection.AddSingleton(typeof(HubLifetimeManager<Hub>), sp => restHubLifetimeManager);

            return serviceCollection.BuildServiceProvider();
        }

        public string GenerateClientAccessToken(string hubName, string userId = null, IList<Claim> claims = null, TimeSpan? lifeTime = null)
        {
            var claimsWithUserId = new List<Claim>();
            if (userId != null)
            {
                claimsWithUserId.Add(new Claim(ClaimTypes.NameIdentifier, userId));
            };
            if (claims != null)
            {
                claimsWithUserId.AddRange(claims);
            }
            return _endpointProvider.GenerateClientAccessTokenAsync(hubName, claimsWithUserId, lifeTime).Result;
        }

        public string GetClientEndpoint(string hubName) => _endpointProvider.GetClientEndpoint(hubName, null, null);

        public async Task<bool> IsServiceHealthy(CancellationToken cancellationToken)
        {
            var isHealthy = false;
            var api = await _restApiProvider.GetServiceHealthEndpointAsync();
            await _restClient.SendAsync(api, HttpMethod.Get, _productInfo, handleExpectedResponse: response =>
                {
                    switch (response.StatusCode)
                    {
                        case HttpStatusCode.OK:
                            isHealthy = true;
                            return true;
                        case HttpStatusCode.BadGateway:
                            isHealthy = false;
                            return true;
                        case HttpStatusCode.ServiceUnavailable:
                            isHealthy = false;
                            return true;
                        default:
                            return false;
                    }
                },
                cancellationToken: cancellationToken);
            return isHealthy;
        }
    }
}
