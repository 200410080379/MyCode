using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 微信小程序WebSocket传输层
    /// 通过DllImport调用微信小游戏WebSocket API
    /// </summary>
    public class WxWebSocketTransport : ITransport
    {
        #region Events

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<byte[]> OnDataReceived;

        #endregion

        #region Fields

        private bool isConnectedInternal;
        private int socketTaskId = -1;

        // 微信WebSocket任务状态
        private const int WX_SOCKET_STATUS_OPEN = 1;
        private const int WX_SOCKET_STATUS_CLOSING = 2;
        private const int WX_SOCKET_STATUS_CLOSED = 3;

        #endregion

        #region ITransport Implementation

        public bool isConnected => isConnectedInternal;

        public void Connect(string url)
        {
            if (isConnectedInternal)
            {
                Debug.LogWarning("[WxWebSocket] 已连接");
                return;
            }

            Debug.Log($"[WxWebSocket] 连接: {url}");

            // 微信小游戏环境下调用 wx.connectSocket
            // 通过JSLIB桥接
            WxConnectSocket(url);
        }

        public void Disconnect()
        {
            if (!isConnectedInternal) return;

            Debug.Log("[WxWebSocket] 断开连接");
            WxCloseSocket(socketTaskId);
            isConnectedInternal = false;
            socketTaskId = -1;
            OnDisconnected?.Invoke();
        }

        public void Send(byte[] data)
        {
            if (!isConnectedInternal || socketTaskId < 0)
            {
                Debug.LogWarning("[WxWebSocket] 未连接");
                return;
            }

            WxSendSocketMessage(socketTaskId, data);
        }

        #endregion

        #region 微信API桥接 (DllImport)

        // 微信小游戏 WebSocket API 通过 Unity WebGL JSLIB 暥接
        // 实际运行时会调用 JavaScript 层的 wx API

        [DllImport("__Internal")]
        private static extern int wx_connect_socket(string url);

        [DllImport("__Internal")]
        private static extern void wx_close_socket(int socketTaskId);

        [DllImport("__Internal")]
        private static extern void wx_send_socket_message(int socketTaskId, byte[] data, int length);

        // 微信API包装方法
        private void WxConnectSocket(string url)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            socketTaskId = wx_connect_socket(url);
#else
            // 编辑器模拟
            Debug.LogWarning("[WxWebSocket] 编辑器模式，模拟连接");
            socketTaskId = 1;
            isConnectedInternal = true;
            OnConnected?.Invoke();
#endif
        }

        private void WxCloseSocket(int taskId)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            wx_close_socket(taskId);
#endif
        }

        private void WxSendSocketMessage(int taskId, byte[] data)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            wx_send_socket_message(taskId, data, data.Length);
#else
            // 编辑器模拟：打印日志
            Debug.Log($"[WxWebSocket] 模拟发送: {data.Length} bytes");
#endif
        }

        #endregion

        #region Callbacks (由JavaScript调用)

        // WebSocket连接成功回调（由JSLIB调用）
        private void OnSocketOpen(string taskId)
        {
            Debug.Log($"[WxWebSocket] 连接成功 taskId={taskId}");
            isConnectedInternal = true;
            socketTaskId = int.Parse(taskId);
            OnConnected?.Invoke();
        }

        // WebSocket断开回调
        private void OnSocketClose(string reason)
        {
            Debug.Log($"[WxWebSocket] 断开: {reason}");
            isConnectedInternal = false;
            socketTaskId = -1;
            OnDisconnected?.Invoke();
        }

        // WebSocket消息回调
        private void OnSocketMessage(string message)
        {
            // 微信小程序接收的是字符串消息
            byte[] data = System.Text.Encoding.UTF8.GetBytes(message);
            OnDataReceived?.Invoke(data);
        }

        // WebSocket错误回调
        private void OnSocketError(string errMsg)
        {
            Debug.LogError($"[WxWebSocket] 错误: {errMsg}");
            isConnectedInternal = false;
            OnDisconnected?.Invoke();
        }

        #endregion
    }
}

// ==================== JSLIB (Unity WebGL桥接) ====================
/*
需要在 Unity 项目中添加 JSLIB 文件 (Plugins/WebGL/WxWebSocket.jslib):

mergeInto(LibraryManager.library, {
    wx_connect_socket: function(url) {
        var urlStr = Pointer_stringify(url);
        var socketTask = wx.connectSocket({
            url: urlStr,
            success: function(res) {
                console.log('[WxWebSocket] connect success');
            },
            fail: function(err) {
                console.error('[WxWebSocket] connect fail:', err);
            }
        });

        var taskId = socketTask.socketTaskId || 1;

        socketTask.onOpen(function(res) {
            _WxWebSocketTransport_OnSocketOpen(taskId.toString());
        });

        socketTask.onClose(function(res) {
            _WxWebSocketTransport_OnSocketClose(res.reason || 'closed');
        });

        socketTask.onMessage(function(res) {
            _WxWebSocketTransport_OnSocketMessage(res.data);
        });

        socketTask.onError(function(err) {
            _WxWebSocketTransport_OnSocketError(err.errMsg || 'error');
        });

        return taskId;
    },

    wx_close_socket: function(taskId) {
        wx.closeSocket();
    },

    wx_send_socket_message: function(taskId, dataPtr, length) {
        var data = new Uint8Array(Module.HEAPU8.buffer, dataPtr, length);
        var str = String.fromCharCode.apply(null, data);
        wx.sendSocketMessage({
            data: str,
            success: function() {
                console.log('[WxWebSocket] send success');
            },
            fail: function(err) {
                console.error('[WxWebSocket] send fail:', err);
            }
        });
    }
});
*/