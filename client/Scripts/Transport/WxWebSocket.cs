using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 微信小程序WebSocket传输层
    /// 通过DllImport调用微信小游戏WebSocket API
    /// 注意：此组件需要添加到场景中的 GameObject 上，名称必须为 "WxWebSocketBridge"
    /// </summary>
    public class WxWebSocketTransport : MonoBehaviour, ITransport
    {
        #region Singleton Pattern
        
        /// <summary>单例实例（用于 JSLIB 回调）</summary>
        public static WxWebSocketTransport singleton { get; private set; }
        
        private void Awake()
        {
            if (singleton != null && singleton != this)
            {
                Destroy(gameObject);
                return;
            }
            singleton = this;
            DontDestroyOnLoad(gameObject);
            gameObject.name = "WxWebSocketBridge"; // 确保名称正确，用于 SendMessage
        }
        
        #endregion

        #region Events

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<byte[]> OnDataReceived;

        #endregion

        #region Fields

        private bool isConnectedInternal;
        private int socketTaskId = -1;

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

        [DllImport("__Internal")]
        private static extern int wx_connect_socket(string url);

        [DllImport("__Internal")]
        private static extern void wx_close_socket(int socketTaskId);

        [DllImport("__Internal")]
        private static extern void wx_send_socket_message(int socketTaskId, byte[] data, int length);

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
            Debug.Log($"[WxWebSocket] 模拟发送: {data.Length} bytes");
#endif
        }

        #endregion

        #region Callbacks (由 JavaScript SendMessage 调用)

        /// <summary>WebSocket连接成功回调（由 JSLIB SendMessage 调用）</summary>
        private void OnSocketOpen(string taskId)
        {
            Debug.Log($"[WxWebSocket] 连接成功 taskId={taskId}");
            isConnectedInternal = true;
            socketTaskId = int.Parse(taskId);
            OnConnected?.Invoke();
        }

        /// <summary>WebSocket断开回调</summary>
        private void OnSocketClose(string reason)
        {
            Debug.Log($"[WxWebSocket] 断开: {reason}");
            isConnectedInternal = false;
            socketTaskId = -1;
            OnDisconnected?.Invoke();
        }

        /// <summary>WebSocket消息回调</summary>
        private void OnSocketMessage(string message)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(message);
            OnDataReceived?.Invoke(data);
        }

        /// <summary>WebSocket错误回调</summary>
        private void OnSocketError(string errMsg)
        {
            Debug.LogError($"[WxWebSocket] 错误: {errMsg}");
            isConnectedInternal = false;
            OnDisconnected?.Invoke();
        }

        #endregion
    }
}
