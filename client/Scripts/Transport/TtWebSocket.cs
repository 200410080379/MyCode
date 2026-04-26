using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 抖音小程序WebSocket传输层
    /// 通过DllImport调用抖音小游戏WebSocket API (tt.connectSocket)
    /// 注意：此组件需要添加到场景中的 GameObject 上，名称必须为 "TtWebSocketBridge"
    /// </summary>
    public class TtWebSocketTransport : MonoBehaviour, ITransport
    {
        #region Singleton Pattern
        
        public static TtWebSocketTransport singleton { get; private set; }
        
        private void Awake()
        {
            if (singleton != null && singleton != this)
            {
                Destroy(gameObject);
                return;
            }
            singleton = this;
            DontDestroyOnLoad(gameObject);
            gameObject.name = "TtWebSocketBridge";
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
                Debug.LogWarning("[TtWebSocket] 已连接");
                return;
            }

            Debug.Log($"[TtWebSocket] 连接: {url}");
            TtConnectSocket(url);
        }

        public void Disconnect()
        {
            if (!isConnectedInternal) return;

            Debug.Log("[TtWebSocket] 断开连接");
            TtCloseSocket(socketTaskId);
            isConnectedInternal = false;
            socketTaskId = -1;
            OnDisconnected?.Invoke();
        }

        public void Send(byte[] data)
        {
            if (!isConnectedInternal || socketTaskId < 0)
            {
                Debug.LogWarning("[TtWebSocket] 未连接");
                return;
            }

            TtSendSocketMessage(socketTaskId, data);
        }

        #endregion

        #region 抖音API桥接 (DllImport)

        [DllImport("__Internal")]
        private static extern int tt_connect_socket(string url);

        [DllImport("__Internal")]
        private static extern void tt_close_socket(int socketTaskId);

        [DllImport("__Internal")]
        private static extern void tt_send_socket_message(int socketTaskId, byte[] data, int length);

        private void TtConnectSocket(string url)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            socketTaskId = tt_connect_socket(url);
#else
            Debug.LogWarning("[TtWebSocket] 编辑器模式，模拟连接");
            socketTaskId = 1;
            isConnectedInternal = true;
            OnConnected?.Invoke();
#endif
        }

        private void TtCloseSocket(int taskId)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            tt_close_socket(taskId);
#endif
        }

        private void TtSendSocketMessage(int taskId, byte[] data)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            tt_send_socket_message(taskId, data, data.Length);
#else
            Debug.Log($"[TtWebSocket] 模拟发送: {data.Length} bytes");
#endif
        }

        #endregion

        #region Callbacks (由 JavaScript SendMessage 调用)

        public void OnSocketOpen(string taskId)
        {
            Debug.Log($"[TtWebSocket] 连接成功 taskId={taskId}");
            isConnectedInternal = true;
            socketTaskId = int.Parse(taskId);
            OnConnected?.Invoke();
        }

        public void OnSocketClose(string reason)
        {
            Debug.Log($"[TtWebSocket] 断开: {reason}");
            isConnectedInternal = false;
            socketTaskId = -1;
            OnDisconnected?.Invoke();
        }

        public void OnSocketMessage(string message)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(message);
            OnDataReceived?.Invoke(data);
        }

        public void OnSocketError(string errMsg)
        {
            Debug.LogError($"[TtWebSocket] 错误: {errMsg}");
            isConnectedInternal = false;
            OnDisconnected?.Invoke();
        }

        #endregion
    }
}
