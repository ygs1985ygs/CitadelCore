﻿/*
* Copyright © 2017-Present Jesse Nicholson
* This Source Code Form is subject to the terms of the Mozilla Public
* License, v. 2.0. If a copy of the MPL was not distributed with this
* file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

using CitadelCore.Extensions;
using CitadelCore.IO;
using CitadelCore.Logging;
using CitadelCore.Net.Http;
using CitadelCore.Net.Proxy;
using CitadelCore.Websockets.Managed;
using Microsoft.AspNetCore.Http;
using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CitadelCore.Net.Handlers
{
    /// <summary>
    /// The FilterWebsocketHandler handler is designed to proxy Websocket requests and responses,
    /// while providing an opportunity for users to inspect and optionally filter and modifiy
    /// requests and responses at different stages of the transaction.
    /// </summary>
    internal class FilterWebsocketHandler : AbstractFilterResponseHandler
    {
        private static readonly Regex s_httpVerRegex = new Regex("([0-9]+\\.[0-9]+)", RegexOptions.Compiled | RegexOptions.ECMAScript);

        /// <summary>
        /// Constructs a FilterWebsocketHandler instance.
        /// </summary>
        /// <param name="configuration">
        /// The shared, proxy configuration.
        /// </param>
        /// <exception cref="ArgumentException">
        /// If the supplied configuration is null or invalid, this constructor will throw.
        /// </exception>
        public FilterWebsocketHandler(
            ProxyServerConfiguration configuration
            ) : base(configuration)
        {
        }

        /// <summary>
        /// Invoked when this handler is determined to be the best suited to handle the supplied connection.
        /// </summary>
        /// <param name="context">
        /// The HTTP context.
        /// </param>
        /// <returns>
        /// The handling task.
        /// </returns>
        public override async Task Handle(HttpContext context)
        {   
            ClientWebSocket wsServer = null;
            System.Net.WebSockets.WebSocket wsClient = null;
            
            try
            {
                // First we need the URL for this connection, since it's been requested to be
                // upgraded to a websocket.
                var fullUrl = Microsoft.AspNetCore.Http.Extensions.UriHelper.GetEncodedUrl(context.Request);

                // Need to replate the scheme with appropriate websocket scheme.
                if (fullUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    fullUrl = "ws://" + fullUrl.Substring(7);
                }
                else if (fullUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    fullUrl = "wss://" + fullUrl.Substring(8);
                }

                // Next we need to try and parse the URL as a URI, because the websocket client
                // requires this for connecting upstream.

                if (!Uri.TryCreate(fullUrl, UriKind.RelativeOrAbsolute, out Uri wsUri))
                {
                    LoggerProxy.Default.Error("Failed to parse websocket URI.");
                    return;
                }

                // Create the websocket that's going to connect to the remote server.
                wsServer = new ClientWebSocket();
                wsServer.Options.Cookies = new System.Net.CookieContainer();
                wsServer.Options.SetBuffer((int)ushort.MaxValue * 16, (int)ushort.MaxValue * 16);

                foreach (var proto in context.WebSockets.WebSocketRequestedProtocols)
                {
                    wsServer.Options.AddSubProtocol(proto);
                }

                foreach (var hdr in context.Request.Headers)
                {
                    if (!ForbiddenWsHeaders.IsForbidden(hdr.Key))
                    {
                        try
                        {
                            wsServer.Options.SetRequestHeader(hdr.Key, hdr.Value.ToString());
                        }
                        catch (Exception hdrException)
                        {
                            LoggerProxy.Default.Error(hdrException);
                        }
                    }
                }

                foreach (var cookie in context.Request.Cookies)
                {
                    try
                    {
                        wsServer.Options.Cookies.Add(new Uri(fullUrl, UriKind.Absolute), new System.Net.Cookie(cookie.Key, System.Net.WebUtility.UrlEncode(cookie.Value)));
                    }
                    catch (Exception e)
                    {
                        LoggerProxy.Default.Error("Error while attempting to add websocket cookie.");
                        LoggerProxy.Default.Error(e);
                    }
                }

                if (context.Connection.ClientCertificate != null)
                {
                    wsServer.Options.ClientCertificates = new System.Security.Cryptography.X509Certificates.X509CertificateCollection(new[] { context.Connection.ClientCertificate.ToV2Certificate() });
                }

                // Connect the server websocket to the upstream, remote webserver.
                await wsServer.ConnectAsync(wsUri, context.RequestAborted);

                foreach (string key in wsServer.ResponseHeaders)
                {
                    if (!ForbiddenWsHeaders.IsForbidden(key))
                    {
                        try
                        {   
                            var value = wsServer.ResponseHeaders[key];
                            context.Response.Headers[key] = wsServer.ResponseHeaders[key];                            
                        }
                        catch (Exception hdrException)
                        {
                            LoggerProxy.Default.Error(hdrException);
                        }
                    }
                }

                Console.WriteLine();
                
                // Create, via acceptor, the client websocket. This is the local machine's websocket.
                wsClient = await context.WebSockets.AcceptWebSocketAsync(wsServer.SubProtocol ?? null);
                
                // Match the HTTP version of the client on the upstream request. We don't want to
                // transparently pass around headers that are wrong for the client's HTTP version.
                Version upstreamReqVersionMatch = null;

                Match match = s_httpVerRegex.Match(context.Request.Protocol);
                if (match != null && match.Success)
                {
                    upstreamReqVersionMatch = Version.Parse(match.Value);
                }

                var msgNfo = new HttpMessageInfo
                {
                    Url = wsUri,
                    Method = new HttpMethod(context.Request.Method),
                    IsEncrypted = context.Request.IsHttps,
                    Headers = context.Request.Headers.ToNameValueCollection(),
                    HttpVersion = upstreamReqVersionMatch ?? new Version(1, 0),
                    MessageProtocol = MessageProtocol.WebSocket,
                    MessageType = MessageType.Request,
                    RemoteAddress = context.Connection.RemoteIpAddress,
                    RemotePort = (ushort)context.Connection.RemotePort,
                    LocalAddress = context.Connection.LocalIpAddress,
                    LocalPort = (ushort)context.Connection.LocalPort
                };

                _configuration.NewHttpMessageHandler?.Invoke(msgNfo);

                switch (msgNfo.ProxyNextAction)
                {
                    case ProxyNextAction.DropConnection:
                        {
                            await wsClient.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                            return;
                        }
                }

                // Spawn an async task that will poll the remote server for data in a loop, and then
                // write any data it gets to the client websocket.
                var serverTask = Task.Run(async () =>
                {
                    System.Net.WebSockets.WebSocketReceiveResult serverStatus = null;
                    var serverBuffer = new byte[1024 * 4];
                    try
                    {
                        bool looping = true;

                        serverStatus = await wsServer.ReceiveAsync(new ArraySegment<byte>(serverBuffer), context.RequestAborted);

                        while (looping && !serverStatus.CloseStatus.HasValue && !context.RequestAborted.IsCancellationRequested)
                        {
                            await wsClient.SendAsync(new ArraySegment<byte>(serverBuffer, 0, serverStatus.Count), serverStatus.MessageType, serverStatus.EndOfMessage, context.RequestAborted);

                            if (!wsClient.CloseStatus.HasValue)
                            {
                                serverStatus = await wsServer.ReceiveAsync(new ArraySegment<byte>(serverBuffer), context.RequestAborted);
                                continue;
                            }

                            looping = false;
                        }

                        await wsClient.CloseAsync(serverStatus.CloseStatus.Value, serverStatus.CloseStatusDescription, context.RequestAborted);
                    }
                    catch
                    {
                        try
                        {
                            var closeStatus = serverStatus?.CloseStatus ?? System.Net.WebSockets.WebSocketCloseStatus.NormalClosure;
                            var closeMessage = serverStatus?.CloseStatusDescription ?? string.Empty;

                            await wsClient.CloseAsync(closeStatus, closeMessage, context.RequestAborted);
                        }
                        catch { }
                    }
                });

                // Spawn an async task that will poll the local client websocket, in a loop, and then
                // write any data it gets to the remote server websocket.
                var clientTask = Task.Run(async () =>
                {
                    System.Net.WebSockets.WebSocketReceiveResult clientResult = null;
                    var clientBuffer = new byte[1024 * 4];
                    try
                    {
                        bool looping = true;

                        clientResult = await wsClient.ReceiveAsync(new ArraySegment<byte>(clientBuffer), context.RequestAborted);

                        while (looping && !clientResult.CloseStatus.HasValue && !context.RequestAborted.IsCancellationRequested)
                        {
                            await wsServer.SendAsync(new ArraySegment<byte>(clientBuffer, 0, clientResult.Count), clientResult.MessageType, clientResult.EndOfMessage, context.RequestAborted);

                            if (!wsServer.CloseStatus.HasValue)
                            {
                                clientResult = await wsClient.ReceiveAsync(new ArraySegment<byte>(clientBuffer), context.RequestAborted);
                                continue;
                            }

                            looping = false;
                        }

                        await wsServer.CloseAsync(clientResult.CloseStatus.Value, clientResult.CloseStatusDescription, context.RequestAborted);
                    }
                    catch
                    {
                        try
                        {
                            var closeStatus = clientResult?.CloseStatus ?? System.Net.WebSockets.WebSocketCloseStatus.NormalClosure;
                            var closeMessage = clientResult?.CloseStatusDescription ?? string.Empty;

                            await wsServer.CloseAsync(closeStatus, closeMessage, context.RequestAborted);
                        }
                        catch { }
                    }
                });

                // Above, we have created a bridge between the local and remote websocket. Wait for
                // both associated tasks to complete.
                await Task.WhenAll(serverTask, clientTask);
            }
            catch (Exception wshe)
            {
                LoggerProxy.Default.Error(wshe);
            }
            finally
            {
                if (wsClient != null)
                {
                    wsClient.Dispose();
                    wsClient = null;
                }

                if (wsServer != null)
                {
                    wsServer.Dispose();
                    wsServer = null;
                }
            }
        }
    }
}