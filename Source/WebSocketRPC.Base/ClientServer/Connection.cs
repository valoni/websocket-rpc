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
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebSocketRPC
{
    /// <summary>
    /// Represents the websocket connection.
    /// </summary>
    public class Connection
    {
        internal static int MaxMessageSize { get; set; } = 64 * 1024; //x KiB
        static string messageToBig = "The message exceeds the maximum allowed message size: {0} bytes.";

        WebSocket socket;
        TaskQueue sendTaskQueue;

        /// <summary>
        /// Creates new connection.
        /// </summary>
        /// <param name="socket">Web-socket.</param>
        /// <param name="cookies">Cookies.</param>
        internal protected Connection(WebSocket socket, IReadOnlyDictionary<string, string> cookies)
        {
            this.socket = socket;
            this.sendTaskQueue = new TaskQueue();
            this.Cookies = cookies;
        }

        /// <summary>
        /// Gets the cookie collection.
        /// </summary>
        public IReadOnlyDictionary<string, string> Cookies { get; private set; }

        /// <summary>
        /// Message receive event. Args: message, is text message.
        /// </summary>
        public event Action<ArraySegment<byte>, bool> OnReceive;
        /// <summary>
        /// Open event.
        /// </summary>
        public event Action OnOpen;
        /// <summary>
        /// Close event.
        /// </summary>
        public event Action OnClose;
        /// <summary>
        /// Error event Args: exception.
        /// </summary>
        public event Action<Exception> OnError;

        /// <summary>
        /// Sends the specified data.
        /// </summary>
        /// <param name="data">Text data to send.</param>
        /// <param name="e">String encoding.</param>
        /// <returns>True if the operation was successful, false otherwise.</returns>
        public async Task<bool> SendAsync(string data, Encoding e)
        {
            if (socket.State != WebSocketState.Open)
                return false;

            var bData = e.GetBytes(data);
            var segment = new ArraySegment<byte>(bData, 0, bData.Length);
            if (segment.Count >= MaxMessageSize)
            {
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, String.Format(messageToBig, MaxMessageSize));
                return false;
            }

            Debug.WriteLine("Sending: " + data);
            //sendAsync(segment, WebSocketMessageType.Text).Wait();
            await sendTaskQueue.Enqueue(() => sendAsync(segment, WebSocketMessageType.Text));
            return true;
        }

        async Task sendAsync(ArraySegment<byte> data, WebSocketMessageType msgType)
        {
            if (socket.State != WebSocketState.Open)
                return;

            try
            {
                await socket.SendAsync(data, msgType, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (socket.State != WebSocketState.Open)
                    await CloseAsync(WebSocketCloseStatus.InternalServerError, ex.Message);
            }
        }

        /// <summary>
        /// Closes the connection.
        /// </summary>
        /// <param name="closeStatus">Close reason.</param>
        /// <param name="statusDescription">Status description.</param>
        /// <returns>Task.</returns>
        public async Task CloseAsync(WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure, string statusDescription = "")
        {
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(closeStatus, statusDescription, CancellationToken.None);
            }
            catch
            { } //do not propagate the exception
            finally
            {
                OnClose?.Invoke();
                clearEvents();
            }
        }

        /// <summary>
        /// Listens for the receive messages for the specified connection.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        internal async Task ListenReceiveAsync(CancellationToken token)
        {
            using (var registration = token.Register(() => CloseAsync().Wait()))
            {
                try
                {
                    OnOpen?.Invoke();
                    byte[] receiveBuffer = new byte[MaxMessageSize];

                    while (socket.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult receiveResult = null;
                        var count = 0;
                        do
                        {
                            var segment = new ArraySegment<byte>(receiveBuffer, count, MaxMessageSize - count);
                            receiveResult = await socket.ReceiveAsync(segment, CancellationToken.None);
                            count += receiveResult.Count;

                            if (count >= MaxMessageSize)
                            {
                                await CloseAsync(WebSocketCloseStatus.MessageTooBig, String.Format(messageToBig, MaxMessageSize));
                                return;
                            }
                        }
                        while (receiveResult?.EndOfMessage == false);


                        if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            await CloseAsync();
                        }
                        else
                        {
                            var segment = new ArraySegment<byte>(receiveBuffer, 0, count);
                            OnReceive?.Invoke(segment, receiveResult.MessageType == WebSocketMessageType.Text);
                            Debug.WriteLine("Received: " + segment.ToString(RPCSettings.Encoding));
                        }

                        if (token.IsCancellationRequested)
                            break;
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex);
                    await CloseAsync(WebSocketCloseStatus.InternalServerError, ex.Message);
                    //socket will be aborted -> no need to close manually
                }
            }
        }

        /// <summary>
        /// Invokes the error event.
        /// </summary>
        /// <param name="ex">Exception.</param>
        internal void InvokeError(Exception ex)
        {
            OnError?.Invoke(ex);
        }

        private void clearEvents()
        {
            OnClose = null;
            OnError = null;
            OnReceive = null;
        }
    }
}
