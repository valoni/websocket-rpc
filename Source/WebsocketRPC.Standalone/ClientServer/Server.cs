﻿#region License
// Copyright © 2017 Darko Jurić
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace WebSocketRPC
{
    /// <summary>
    /// Websocket server.
    /// </summary>
    public static class Server
    {
        /// <summary>
        /// Creates and starts a new instance of the http / websocket server.
        /// </summary>
        /// <param name="port">The http/https URI listening port.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="onConnect">Action executed when connection is created.</param>
        /// <param name="useHttps">True to add 'https://' prefix insteaad of 'http://'.</param>
        /// <returns>Server task.</returns>
        public static async Task ListenAsync(int port, CancellationToken token, Action<Connection, WebSocketContext> onConnect, bool useHttps = false)
        {
            var s = useHttps ? "s" : String.Empty;
            await ListenAsync($"http{s}://+:{port}/", token, onConnect);
        }

        /// <summary>
        /// Creates and starts a new instance of the http / websocket server.
        /// <para>All HTTP requests will have the 'BadRequest' response.</para>
        /// </summary>
        /// <param name="port">The http/https URI listening port.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="onConnect">Action executed when connection is created.</param>
        /// <param name="onHttpRequestAsync">Action executed on HTTP request.</param>
        /// <param name="useHttps">True to add 'https://' prefix insteaad of 'http://'.</param>
        /// <returns>Server task.</returns>
        public static async Task ListenAsync(int port, CancellationToken token, Action<Connection, WebSocketContext> onConnect, Func<HttpListenerRequest, HttpListenerResponse, Task> onHttpRequestAsync, bool useHttps = false)
        {
            var s = useHttps ? "s" : String.Empty;
            await ListenAsync($"http{s}://+:{port}/", token, onConnect, onHttpRequestAsync);
        }



        /// <summary>
        /// Creates and starts a new instance of the http / websocket server.
        /// </summary>
        /// <param name="httpListenerPrefix">The http/https URI listening prefix.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="onConnect">Action executed when connection is created.</param>
        /// <returns>Server task.</returns>
        public static async Task ListenAsync(string httpListenerPrefix, CancellationToken token, Action<Connection, WebSocketContext> onConnect)
        {
            await ListenAsync(httpListenerPrefix, token, onConnect, (rq, rp) => 
            {
                rp.StatusCode = (int)HttpStatusCode.BadRequest;
                return Task.FromResult(true);
            });
        }

        /// <summary>
        /// Creates and starts a new instance of the http / websocket server.
        /// <para>All HTTP requests will have the 'BadRequest' response.</para>
        /// </summary>
        /// <param name="httpListenerPrefix">The http/https URI listening prefix.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="onConnect">Action executed when connection is created.</param>
        /// <param name="onHttpRequestAsync">Action executed on HTTP request.</param>
        /// <returns>Server task.</returns>
        public static async Task ListenAsync(string httpListenerPrefix, CancellationToken token, Action<Connection, WebSocketContext> onConnect, Func<HttpListenerRequest, HttpListenerResponse, Task> onHttpRequestAsync)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(httpListenerPrefix);

            try { listener.Start(); }
            catch (Exception ex) when ((ex as HttpListenerException)?.ErrorCode == 5)
            {
                throw new UnauthorizedAccessException($"The HTTP server can not be started, as the namespace reservation does not exist.\n" +
                                                      $"Please run (elevated): 'netsh http add urlacl url={httpListenerPrefix} user=\"Everyone\"'.", ex);
            }

			//helpful: https://stackoverflow.com/questions/11167183/multi-threaded-httplistener-with-await-async-and-tasks
			//         https://github.com/NancyFx/Nancy/blob/815b6fdf42a5a8c61e875501e305382f46cec619/src/Nancy.Hosting.Self/HostConfiguration.cs
            using (var r = token.Register(() => closeListener(listener)))
            {
                bool shouldStop = false;
                while (!shouldStop)
                {
                    try
                    {
                        var ctx = await listener.GetContextAsync();

                        if (ctx.Request.IsWebSocketRequest)
                            Task.Run(() => listenAsync(ctx, token, onConnect)).Wait(0);
                        else
                            Task.Factory.StartNew(() => listenHttpAsync(ctx, onHttpRequestAsync), TaskCreationOptions.LongRunning).Wait(0);
                    }
                    catch (Exception)
                    {
                        if (!token.IsCancellationRequested)
                            throw;
                    }
                    finally
                    {
                        if (token.IsCancellationRequested)
                            shouldStop = true;
                    }
                }
            }

            Debug.WriteLine("Server stopped.");
        }

        static void closeListener(HttpListener listener)
        {
            var wsCloseTasks = new Task[connections.Count];

            for (int i = 0; i < connections.Count; i++)
                wsCloseTasks[i] = connections[i].CloseAsync();

            Task.WaitAll(wsCloseTasks.Where(t => t != null).ToArray());
            listener.Stop();
            connections.Clear();
        }

        static async Task listenHttpAsync(HttpListenerContext ctx, Func<HttpListenerRequest, HttpListenerResponse, Task> onHttpRequest)
        {
            await onHttpRequest(ctx.Request, ctx.Response);
            ctx.Response.Close();
        }

        static List<Connection> connections = new List<Connection>();
        static async Task listenAsync(HttpListenerContext ctx, CancellationToken token, Action<Connection, WebSocketContext> onConnect)
        {
            if (!ctx.Request.IsWebSocketRequest)
                return;

            WebSocketContext wsCtx = null;
            WebSocket webSocket = null;
            try
            {
                wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                webSocket = wsCtx.WebSocket;
            }
            catch (Exception)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
                return;
            }

            var connection = new Connection(webSocket, CookieUtils.GetCookies(wsCtx.CookieCollection));
            try
            {
                lock (connections) connections.Add(connection);
                onConnect(connection, wsCtx);
                await connection.ListenReceiveAsync(token);
            }
            catch (Exception ex)
            {
                 connection.InvokeError(ex);
            }
            finally
            {
                webSocket?.Dispose();
                lock (connections) connections.Remove(connection);
            }
        }
    }
}