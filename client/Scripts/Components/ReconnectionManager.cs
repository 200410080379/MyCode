using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 断线重连管理器（客户端）
    /// 
    /// 核心功能：
    /// 1. 检测连接断开
    /// 2. 自动尝试重连（指数退避）
    /// 3. 重连成功后恢复状态
    /// 4. 接收断线期间丢失的帧数据
    /// </summary>
    public class ReconnectionManager : MonoBehaviour
    {
        #region Singleton
        
        public static ReconnectionManager singleton { get; private set; }
        
        /// <summary>
        /// #29: 获取或创建单例实例
        /// 如果场景中没有 ReconnectionManager，自动创建
        /// </summary>
        public static ReconnectionManager GetOrCreate()
        {
            if (singleton != null) return singleton;
            
            // 尝试在场景中查找
            singleton = FindObjectOfType<ReconnectionManager>();
            if (singleton != null) return singleton;
            
            // 自动创建
            var go = new GameObject("ReconnectionManager");
            DontDestroyOnLoad(go);
            singleton = go.AddComponent<ReconnectionManager>();
            Debug.Log("[Reconnect] 自动创建 ReconnectionManager 单例");
            return singleton;
        }
        
        private void Awake()
        {
            if (singleton != null && singleton != this)
            {
                Destroy(gameObject);
                return;
            }
            singleton = this;
            DontDestroyOnLoad(gameObject);
        }
        
        #endregion
        
        #region Config
        
        [Header("重连配置")]
        [Tooltip("初始重连延迟(秒)")]
        public float initialRetryDelay = 1f;
        
        [Tooltip("最大重连延迟(秒)")]
        public float maxRetryDelay = 30f;
        
        [Tooltip("延迟倍率（指数退避）")]
        public float retryMultiplier = 2f;
        
        [Tooltip("最大重连次数")]
        public int maxRetryCount = 10;
        
        [Tooltip("总超时时间(秒)")]
        public float totalTimeout = 30f;
        
        [Tooltip("心跳超时(秒)")]
        public float heartbeatTimeout = 5f;
        
        #endregion
        
        #region State
        
        /// <summary>重连状态</summary>
        public ReconnectState state { get; private set; } = ReconnectState.Idle;
        
        /// <summary>当前重连次数</summary>
        public int retryCount { get; private set; }
        
        /// <summary>上次收到心跳的时间</summary>
        public float lastHeartbeatTime { get; private set; }
        
        // 重连相关
        private string lastServerUrl;
        private string lastPlayerId;
        private string lastRoomId;
        private long lastFrame;
        private float currentRetryDelay;
        private float reconnectStartTime;
        private bool isReconnecting;
        
        // 心跳检测
        private float heartbeatTimer;
        private bool heartbeatEnabled;
        
        #endregion
        
        #region Events
        
        /// <summary>断线事件</summary>
        public event Action onDisconnected;
        
        /// <summary>开始重连事件</summary>
        public event Action<int> onReconnectAttempt;
        
        /// <summary>重连成功事件</summary>
        public event Action<ReconnectResult> onReconnectSuccess;
        
        /// <summary>重连失败事件</summary>
        public event Action<string> onReconnectFailed;
        
        /// <summary>状态恢复事件</summary>
        public event Action<ReconnectResult> onStateRestored;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Update()
        {
            // 心跳检测
            if (heartbeatEnabled && NetworkClient.isConnected)
            {
                heartbeatTimer += Time.deltaTime;
                if (heartbeatTimer >= heartbeatTimeout)
                {
                    // 心跳超时，判定断线
                    OnConnectionLost();
                }
            }
            
            // 重连超时检测
            if (isReconnecting && state == ReconnectState.Reconnecting)
            {
                if (Time.time - reconnectStartTime > totalTimeout)
                {
                    OnReconnectFailed("总超时");
                }
            }
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// 启用心跳检测
        /// </summary>
        public void EnableHeartbeat()
        {
            heartbeatEnabled = true;
            lastHeartbeatTime = Time.time;
            heartbeatTimer = 0f;
        }
        
        /// <summary>
        /// 禁用心跳检测
        /// </summary>
        public void DisableHeartbeat()
        {
            heartbeatEnabled = false;
        }
        
        /// <summary>
        /// 收到心跳响应
        /// </summary>
        public void OnHeartbeatReceived()
        {
            lastHeartbeatTime = Time.time;
            heartbeatTimer = 0f;
        }
        
        /// <summary>
        /// 保存当前连接信息（用于重连）
        /// </summary>
        public void SaveConnectionState(string serverUrl, string playerId, string roomId, long frame)
        {
            lastServerUrl = string.IsNullOrEmpty(serverUrl) ? lastServerUrl : serverUrl;
            lastPlayerId = string.IsNullOrEmpty(playerId) ? lastPlayerId : playerId;
            lastRoomId = roomId;
            lastFrame = frame;
        }
        
        /// <summary>
        /// 连接丢失时调用
        /// </summary>
        public void OnConnectionLost()
        {
            if (state == ReconnectState.Reconnecting) return;

            if (string.IsNullOrEmpty(lastServerUrl) && NetworkClient.connection != null)
            {
                lastServerUrl = NetworkClient.connection.serverUrl;
            }

            if (string.IsNullOrEmpty(lastPlayerId))
            {
                lastPlayerId = NetworkClient.playerId;
            }
            
            state = ReconnectState.Disconnected;
            isReconnecting = true;
            retryCount = 0;
            currentRetryDelay = initialRetryDelay;
            reconnectStartTime = Time.time;
            
            Debug.LogWarning("[Reconnect] 连接丢失，开始重连");
            onDisconnected?.Invoke();
            
            // 开始重连流程
            StartCoroutine(ReconnectLoop());
        }
        
        /// <summary>
        /// 处理重连结果
        /// </summary>
        public void OnReconnectResult(Dictionary<string, object> msg)
        {
            var result = msg["reconnect_result_msg"] as Dictionary<string, object>;
            if (result == null) return;
            
            bool success = result.TryGetValue("success", out var s) && Convert.ToBoolean(s);
            
            if (success)
            {
                state = ReconnectState.Restoring;
                
                var reconnectResult = new ReconnectResult
                {
                    roomId = result.TryGetValue("room_id", out var rid) ? rid?.ToString() : "",
                    playerId = result.TryGetValue("player_id", out var pid) ? pid?.ToString() : "",
                    currentFrame = result.TryGetValue("current_frame", out var cf) ? Convert.ToInt64(cf) : 0,
                    disconnectedFrame = result.TryGetValue("disconnected_frame", out var df) ? Convert.ToInt64(df) : 0,
                    roomState = result.TryGetValue("room_state", out var rs) ? rs as Dictionary<string, object> : null,
                };
                
                Debug.Log($"[Reconnect] 重连成功! missed frames: {reconnectResult.currentFrame - reconnectResult.disconnectedFrame}");
                
                // 恢复帧同步
                RestoreFrameSync(reconnectResult);
                
                state = ReconnectState.Connected;
                isReconnecting = false;
                
                onReconnectSuccess?.Invoke(reconnectResult);
                onStateRestored?.Invoke(reconnectResult);
            }
            else
            {
                string reason = result.TryGetValue("reason", out var r) ? r?.ToString() : "unknown";
                OnReconnectFailed(reason);
            }
        }
        
        /// <summary>
        /// 取消重连
        /// </summary>
        public void CancelReconnect()
        {
            StopAllCoroutines();
            state = ReconnectState.Idle;
            isReconnecting = false;
            Debug.Log("[Reconnect] 重连已取消");
        }
        
        #endregion
        
        #region Internal Methods
        
        /// <summary>
        /// 重连循环（指数退避）
        /// </summary>
        private IEnumerator ReconnectLoop()
        {
            while (retryCount < maxRetryCount && isReconnecting)
            {
                retryCount++;
                state = ReconnectState.Reconnecting;
                
                Debug.Log($"[Reconnect] 第 {retryCount} 次重连，延迟 {currentRetryDelay:F1}s");
                onReconnectAttempt?.Invoke(retryCount);
                
                // 等待延迟
                yield return new WaitForSecondsRealtime(currentRetryDelay);
                
                if (!isReconnecting) yield break;
                
                bool connected = false;
                yield return StartCoroutine(TryReconnect(result => connected = result));
                
                if (connected)
                {
                    // 连接成功，发送重连请求
                    SendReconnectRequest();
                    yield break;
                }
                
                // 指数退避
                currentRetryDelay = Mathf.Min(currentRetryDelay * retryMultiplier, maxRetryDelay);
            }
            
            // 超过最大次数
            if (isReconnecting)
            {
                OnReconnectFailed("超过最大重连次数");
            }
        }
        
        /// <summary>
        /// 尝试重连
        /// </summary>
        private IEnumerator TryReconnect(Action<bool> onCompleted)
        {
            if (string.IsNullOrEmpty(lastServerUrl))
            {
                onCompleted?.Invoke(false);
                yield break;
            }
            
            // 重新连接服务器
            NetworkClient.Reconnect(lastServerUrl);
            
            // 等待连接结果
            float waitTime = 0f;
            float maxWait = Mathf.Max(1f, currentRetryDelay * 0.8f); // 最多等待80%的延迟时间
            
            while (waitTime < maxWait)
            {
                if (NetworkClient.isConnected)
                {
                    onCompleted?.Invoke(true);
                    yield break;
                }

                waitTime += Time.unscaledDeltaTime;
                yield return null;
            }
            
            onCompleted?.Invoke(NetworkClient.isConnected);
        }
        
        /// <summary>
        /// 发送重连请求
        /// </summary>
        private void SendReconnectRequest()
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 82, // MSG_TYPE_RECONNECT_REQUEST
                ["seq"] = NetworkClient.connection?.nextSeq() ?? 0,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["reconnect_req"] = new Dictionary<string, object>
                {
                    ["player_id"] = lastPlayerId ?? "",
                    ["room_id"] = lastRoomId ?? "",
                    ["last_frame"] = lastFrame,
                },
            };
            
            NetworkClient.SendJson(msg);
        }
        
        /// <summary>
        /// 恢复帧同步
        /// #9: 使用新的 StartReconnectCatchup 接口，正确补发丢失帧
        /// </summary>
        private void RestoreFrameSync(ReconnectResult result)
        {
            // 通知帧同步管理器进入重连补帧模式
            if (FrameSyncManager.singleton != null)
            {
                FrameSyncManager.singleton.StartReconnectCatchup(
                    result.disconnectedFrame,
                    result.currentFrame
                );
            }
            
            // 通知房间管理器
            if (NetworkRoomManager.singleton != null)
            {
                NetworkRoomManager.singleton.OnReconnected(result);
            }
            
            // 重启心跳
            EnableHeartbeat();
        }

        public void OnReconnectState(Dictionary<string, object> msg)
        {
            var stateMsg = msg["player_reconnect_msg"] as Dictionary<string, object>;
            if (stateMsg == null) return;

            string reconnectState = stateMsg.TryGetValue("state", out var value)
                ? value?.ToString()
                : "unknown";

            Debug.Log($"[Reconnect] 当前服务端重连状态: {reconnectState}");
        }
        
        /// <summary>
        /// 重连失败
        /// </summary>
        private void OnReconnectFailed(string reason)
        {
            state = ReconnectState.Failed;
            isReconnecting = false;
            
            Debug.LogError($"[Reconnect] 重连失败: {reason}");
            onReconnectFailed?.Invoke(reason);
        }
        
        #endregion
    }
    
    /// <summary>
    /// 重连状态枚举
    /// </summary>
    public enum ReconnectState
    {
        Idle,           // 空闲
        Connected,      // 已连接
        Disconnected,   // 已断开
        Reconnecting,   // 重连中
        Restoring,      // 恢复状态中
        Failed,         // 重连失败
    }
    
    /// <summary>
    /// 重连结果
    /// </summary>
    public class ReconnectResult
    {
        public string roomId;
        public string playerId;
        public long currentFrame;
        public long disconnectedFrame;
        public Dictionary<string, object> roomState;
    }
}
