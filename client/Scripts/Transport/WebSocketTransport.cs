using System;
using System.Text;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// WebSocket传输层 - 原生平台实现
    /// 使用 System.Net.WebSockets.ClientWebSocket
    /// </summary>
    public class WebSocketTransport : ITransport
    {
        #region Events

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<byte[]> OnDataReceived;

        #endregion

        #region Fields

        private ClientWebSocket socket;
        private CancellationTokenSource cts;
        private bool isConnecting;
        private bool isDisconnecting;

        private const int ReceiveBufferSize = 8192;
        private const int SendBufferSize = 8192;

        #endregion

        #region Properties

        public bool isConnected => socket != null && socket.State == WebSocketState.Open;

        #endregion

        #region ITransport Implementation

        public async void Connect(string url)
        {
            if (isConnecting || isConnected)
            {
                Debug.LogWarning("[WebSocket] 已经连接或正在连接");
                return;
            }

            isConnecting = true;

            try
            {
                socket = new ClientWebSocket();
                cts = new CancellationTokenSource();

                var connectUri = new Uri(url);

                // 配置选项（可选）
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                socket.Options.ReceiveBufferSize = ReceiveBufferSize;

                await socket.ConnectAsync(connectUri, cts.Token);

                if (socket.State == WebSocketState.Open)
                {
                    Debug.Log("[WebSocket] 连接成功");
                    isConnecting = false;
                    OnConnected?.Invoke();

                    // 开始接收消息
                    _ = ReceiveLoop();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocket] 连接失败: {ex.Message}");
                isConnecting = false;
                OnDisconnected?.Invoke();
            }
        }

        public async void Disconnect()
        {
            if (isDisconnecting || socket == null) return;

            isDisconnecting = true;

            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Client disconnecting",
                        CancellationToken.None
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WebSocket] 断开连接异常: {ex.Message}");
            }
            finally
            {
                Cleanup();
                isDisconnecting = false;
                OnDisconnected?.Invoke();
            }
        }

        public async void Send(byte[] data)
        {
            if (!isConnected)
            {
                Debug.LogWarning("[WebSocket] 未连接，无法发送");
                return;
            }

            try
            {
                var segment = new ArraySegment<byte>(data);
                await socket.SendAsync(
                    segment,
                    WebSocketMessageType.Text,
                    true,
                    cts.Token
                );
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocket] 发送失败: {ex.Message}");
                HandleDisconnection();
            }
        }

        #endregion

        #region Receive Loop

        private async Task ReceiveLoop()
        {
            var buffer = new byte[ReceiveBufferSize];
            var receiveBuffer = new ArraySegment<byte>(buffer);

            try
            {
                while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(receiveBuffer, cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.Log("[WebSocket] 服务器关闭连接");
                        HandleDisconnection();
                        break;
                    }

                    if (result.Count > 0)
                    {
                        // 复制数据到新数组
                        var data = new byte[result.Count];
                        Array.Copy(buffer, 0, data, 0, result.Count);
                        OnDataReceived?.Invoke(data);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocket] 接收异常: {ex.Message}");
                HandleDisconnection();
            }
        }

        #endregion

        #region Reconnection

        /// <summary>
        /// 重连逻辑
        /// </summary>
        public async void Reconnect(int maxRetries = 3, int retryDelayMs = 1000)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                Debug.Log($"[WebSocket] 尝试重连 ({i + 1}/{maxRetries})");

                Cleanup();
                await Task.Delay(retryDelayMs);

                if (!string.IsNullOrEmpty(NetworkClient.connection?.serverUrl))
                {
                    Connect(NetworkClient.connection.serverUrl);

                    // 等待连接结果
                    await Task.Delay(2000);
                    if (isConnected)
                    {
                        Debug.Log("[WebSocket] 重连成功");

                        // 发送重连令牌
                        if (!string.IsNullOrEmpty(NetworkClient.connection?.reconnectToken))
                        {
                            SendReconnectToken(NetworkClient.connection.reconnectToken);
                        }
                        return;
                    }
                }
            }

            Debug.LogError("[WebSocket] 重连失败");
        }

        private void SendReconnectToken(string token)
        {
            var msg = new System.Collections.Generic.Dictionary<string, object>
            {
                ["type"] = 4, // MSG_TYPE_RECONNECT
                ["reconnect_token"] = token,
            };
            string json = MiniJson.Serialize(msg);
            Send(Encoding.UTF8.GetBytes(json));
        }

        #endregion

        #region Cleanup

        private void HandleDisconnection()
        {
            Cleanup();
            OnDisconnected?.Invoke();
        }

        private void Cleanup()
        {
            try
            {
                cts?.Cancel();
            }
            catch { }

            try
            {
                socket?.Dispose();
            }
            catch { }

            socket = null;
            cts = null;
        }

        #endregion
    }
}
