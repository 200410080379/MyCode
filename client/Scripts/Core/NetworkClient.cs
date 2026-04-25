using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 网络客户端核心
    /// 管理连接、消息收发、对象池、同步
    /// </summary>
    public static class NetworkClient
    {
        #region Properties

        /// <summary>当前连接</summary>
        public static NetworkConnection connection { get; internal set; }

        /// <summary>是否已连接</summary>
        public static bool isConnected => connection != null && connection.isConnected;

        /// <summary>是否已认证</summary>
        public static bool isAuthenticated => connection != null && connection.isAuthenticated;

        /// <summary>本地玩家对象</summary>
        public static NetworkIdentity localPlayer { get; internal set; }

        /// <summary>连接ID</summary>
        public static string connectionId => connection?.connectionId;

        #endregion

        #region Internal Fields

        /// <summary>网络对象池 netId -> NetworkIdentity</summary>
        internal static readonly Dictionary<uint, NetworkIdentity> spawnedObjects = new Dictionary<uint, NetworkIdentity>();

        /// <summary>消息处理器</summary>
        private static readonly Dictionary<string, Action<NetworkReader>> messageHandlers = new Dictionary<string, Action<NetworkReader>>();

        /// <summary>RPC方法缓存 methodHash -> delegate</summary>
        private static readonly Dictionary<string, Delegate> rpcHandlers = new Dictionary<string, Delegate>();

        /// <summary>预制体注册表 prefabHash -> prefab</summary>
        private static readonly Dictionary<int, NetworkIdentity> prefabs = new Dictionary<int, NetworkIdentity>();

        /// <summary>传输层</summary>
        private static ITransport transport;

        #endregion

        #region Connection

        /// <summary>
        /// 连接服务器
        /// </summary>
        public static void Connect(string serverUrl)
        {
            if (isConnected)
            {
                Debug.LogWarning("[MiniLink] 已经连接");
                return;
            }

            // 创建传输层（根据平台自动选择）
            transport = CreateTransport();
            transport.OnConnected += OnConnected;
            transport.OnDisconnected += OnDisconnected;
            transport.OnDataReceived += OnDataReceived;

            connection = new NetworkConnection();
            connection.serverUrl = serverUrl;

            Debug.Log($"[MiniLink] 连接中: {serverUrl}");
            transport.Connect(serverUrl);
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public static void Disconnect()
        {
            if (!isConnected) return;

            Debug.Log("[MiniLink] 断开连接");
            transport?.Disconnect();
            Cleanup();
        }

        #endregion

        #region Transport Factory

        /// <summary>
        /// 根据平台创建传输层
        /// </summary>
        private static ITransport CreateTransport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL/微信小程序环境
            return new WxWebSocketTransport();
#else
            // 原生环境
            return new WebSocketTransport();
#endif
        }

        #endregion

        #region Transport Callbacks

        private static void OnConnected()
        {
            Debug.Log("[MiniLink] 连接成功");
            connection.isConnected = true;

            // 发送握手
            SendHandshake();
        }

        private static void OnDisconnected()
        {
            Debug.Log("[MiniLink] 连接断开");
            connection.isConnected = false;
            connection.isAuthenticated = false;

            // 通知所有对象断开
            foreach (var identity in spawnedObjects.Values)
            {
                identity.NotifyDestroy();
            }
        }

        private static void OnDataReceived(byte[] data)
        {
            try
            {
                // 解析JSON消息
                string json = System.Text.Encoding.UTF8.GetString(data);
                var msg = MiniJson.Deserialize(json) as Dictionary<string, object>;
                if (msg == null) return;

                ProcessMessage(msg);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MiniLink] 消息解析失败: {ex.Message}");
            }
        }

        #endregion

        #region Message Processing

        private static void ProcessMessage(Dictionary<string, object> msg)
        {
            int msgType = Convert.ToInt32(msg["type"]);

            switch (msgType)
            {
                case 2: // HANDSHAKE_RESP
                    ProcessHandshakeResp(msg);
                    break;

                case 3: // HEARTBEAT
                    ProcessHeartbeat(msg);
                    break;

                case 17: // ROOM_STATE_NOTIFY
                    ProcessRoomState(msg);
                    break;

                case 20: // SYNC_VAR_MSG
                    ProcessSyncVar(msg);
                    break;

                case 22: // SNAPSHOT_MSG
                    ProcessSnapshot(msg);
                    break;

                case 30: // COMMAND (客户端收到的是RPC响应)
                    ProcessCommand(msg);
                    break;

                case 31: // CLIENT_RPC
                    ProcessClientRpc(msg);
                    break;

                case 32: // TARGET_RPC
                    ProcessTargetRpc(msg);
                    break;

                case 40: // SPAWN
                    ProcessSpawn(msg);
                    break;

                case 41: // DESPAWN
                    ProcessDespawn(msg);
                    break;

                case 99: // ERROR_MSG
                    ProcessError(msg);
                    break;
            }
        }

        private static void ProcessHandshakeResp(Dictionary<string, object> msg)
        {
            var resp = msg["handshake_resp"] as Dictionary<string, object>;
            if (resp == null) return;

            bool success = Convert.ToBoolean(resp["success"]);
            if (success)
            {
                connection.isAuthenticated = true;
                connection.sessionId = resp["session_id"] as string;
                connection.reconnectToken = resp["reconnect_token"] as string;
                Debug.Log($"[MiniLink] 认证成功, sessionId={connection.sessionId}");
            }
            else
            {
                Debug.LogError("[MiniLink] 认证失败");
            }
        }

        private static void ProcessHeartbeat(Dictionary<string, object> msg)
        {
            var hb = msg["heartbeat"] as Dictionary<string, object>;
            if (hb == null) return;

            long clientTime = Convert.ToInt64(hb["client_time"]);
            int pingMs = Convert.ToInt32(hb["ping_ms"]);
            connection.UpdatePing(pingMs);
        }

        private static void ProcessSyncVar(Dictionary<string, object> msg)
        {
            var syncMsg = msg["sync_var_msg"] as Dictionary<string, object>;
            if (syncMsg == null) return;

            uint netId = Convert.ToUInt32(syncMsg["net_id"]);
            var identity = GetSpawnedObject(netId);
            if (identity == null)
            {
                Debug.LogWarning($"[MiniLink] 收到未知对象SyncVar: netId={netId}");
                return;
            }

            string component = syncMsg["component"] as string;
            int dirtyMask = Convert.ToInt32(syncMsg["dirty_mask"]);
            string payloadBase64 = syncMsg["payload"] as string;
            byte[] payload = Convert.FromBase64String(payloadBase64);

            using (var reader = new NetworkReader(payload))
            {
                identity.DeserializeSyncVars(reader, false);
            }

            identity.ClearDirtyMask((ulong)dirtyMask);
        }

        private static void ProcessSnapshot(Dictionary<string, object> msg)
        {
            var snapMsg = msg["snapshot_msg"] as Dictionary<string, object>;
            if (snapMsg == null) return;

            uint netId = Convert.ToUInt32(snapMsg["net_id"]);
            var identity = GetSpawnedObject(netId);
            if (identity == null) return;

            long remoteTick = Convert.ToInt64(snapMsg["remote_tick"]);
            float remoteTime = Convert.ToSingle(snapMsg["remote_time"]);
            string payloadBase64 = snapMsg["state_data"] as string;
            byte[] payload = Convert.FromBase64String(payloadBase64);

            // 交给 SnapshotInterpolation 处理
            SnapshotInterpolation.AddSnapshot(netId, remoteTick, remoteTime, payload);
        }

        private static void ProcessClientRpc(Dictionary<string, object> msg)
        {
            var rpcMsg = msg["client_rpc_msg"] as Dictionary<string, object>;
            if (rpcMsg == null) return;

            uint netId = Convert.ToUInt32(rpcMsg["net_id"]);
            string methodHash = rpcMsg["method_hash"] as string;
            bool includeOwner = Convert.ToBoolean(rpcMsg["include_owner"]);
            string argsBase64 = rpcMsg["args"] as string;
            byte[] args = Convert.FromBase64String(argsBase64);

            var identity = GetSpawnedObject(netId);
            if (identity == null) return;

            // 检查是否排除拥有者
            if (!includeOwner && identity.isOwned) return;

            // 调用RPC方法
            InvokeRpc(identity, methodHash, args);
        }

        private static void ProcessTargetRpc(Dictionary<string, object> msg)
        {
            var rpcMsg = msg["target_rpc_msg"] as Dictionary<string, object>;
            if (rpcMsg == null) return;

            uint netId = Convert.ToUInt32(rpcMsg["net_id"]);
            string methodHash = rpcMsg["method_hash"] as string;
            string argsBase64 = rpcMsg["args"] as string;
            byte[] args = Convert.FromBase64String(argsBase64);

            var identity = GetSpawnedObject(netId);
            if (identity == null) return;

            InvokeRpc(identity, methodHash, args);
        }

        private static void ProcessSpawn(Dictionary<string, object> msg)
        {
            var spawnMsg = msg["spawn_msg"] as Dictionary<string, object>;
            if (spawnMsg == null) return;

            uint netId = Convert.ToUInt32(spawnMsg["net_id"]);
            int prefabHash = Convert.ToInt32(spawnMsg["prefab_hash"]);
            string ownerConnId = spawnMsg["owner_conn_id"] as string;
            string stateBase64 = spawnMsg["initial_state"] as string;
            byte[] initialState = string.IsNullOrEmpty(stateBase64) ? null : Convert.FromBase64String(stateBase64);

            SpawnObject(netId, prefabHash, ownerConnId, initialState);
        }

        private static void ProcessDespawn(Dictionary<string, object> msg)
        {
            var despawnMsg = msg["despawn_msg"] as Dictionary<string, object>;
            if (despawnMsg == null) return;

            uint netId = Convert.ToUInt32(despawnMsg["net_id"]);
            DestroyObject(netId);
        }

        private static void ProcessError(Dictionary<string, object> msg)
        {
            var errMsg = msg["error_msg"] as Dictionary<string, object>;
            if (errMsg == null) return;

            string code = errMsg["code"] as string;
            string message = errMsg["message"] as string;
            Debug.LogError($"[MiniLink] 服务器错误: {code} - {message}");
        }

        private static void ProcessRoomState(Dictionary<string, object> msg)
        {
            var roomMsg = msg["room_state_notify"] as Dictionary<string, object>;
            if (roomMsg == null) return;
            
            // 检查 singleton 是否存在，避免 NullReferenceException
            if (NetworkRoomManager.singleton != null)
            {
                NetworkRoomManager.OnRoomStateUpdate(roomMsg);
            }
            else
            {
                Debug.LogWarning("[MiniLink] NetworkRoomManager.singleton 未初始化，无法处理房间状态");
            }
        }

        private static void ProcessCommand(Dictionary<string, object> msg)
        {
            // 客户端收到的Command通常是服务端的响应
            // 这里简化处理，实际可扩展为双向RPC
        }

        #endregion

        #region Object Management

        /// <summary>
        /// 注册预制体
        /// </summary>
        public static void RegisterPrefab(NetworkIdentity prefab)
        {
            if (prefab == null) return;
            prefabs[prefab.PrefabHash] = prefab;
        }

        /// <summary>
        /// 生成网络对象
        /// </summary>
        internal static void SpawnObject(uint netId, int prefabHash, string ownerConnId, byte[] initialState)
        {
            if (spawnedObjects.ContainsKey(netId))
            {
                Debug.LogWarning($"[MiniLink] 对象已存在: netId={netId}");
                return;
            }

            if (!prefabs.TryGetValue(prefabHash, out var prefab))
            {
                Debug.LogError($"[MiniLink] 未注册的预制体: hash={prefabHash}");
                return;
            }

            // 实例化
            var go = UnityEngine.Object.Instantiate(prefab.gameObject);
            var identity = go.GetComponent<NetworkIdentity>();

            identity.netId = netId;
            identity.ownerConnId = ownerConnId;
            identity.isClient = true;
            identity.isServer = false;
            identity.isLocalPlayer = (ownerConnId == connectionId);

            if (identity.isLocalPlayer)
            {
                localPlayer = identity;
            }

            // 反序列化初始状态
            if (initialState != null && initialState.Length > 0)
            {
                using (var reader = new NetworkReader(initialState))
                {
                    identity.DeserializeSyncVars(reader, true);
                }
            }

            // 注册到对象池
            spawnedObjects[netId] = identity;

            // 通知生命周期
            identity.NotifySpawned();

            Debug.Log($"[MiniLink] 生成对象: netId={netId}, prefab={prefabHash}");
        }

        /// <summary>
        /// 销毁网络对象
        /// </summary>
        internal static void DestroyObject(uint netId)
        {
            if (!spawnedObjects.TryGetValue(netId, out var identity))
            {
                return;
            }

            identity.NotifyDestroy();
            spawnedObjects.Remove(netId);

            if (localPlayer == identity)
            {
                localPlayer = null;
            }

            UnityEngine.Object.Destroy(identity.gameObject);
        }

        /// <summary>
        /// 获取生成的对象
        /// </summary>
        public static NetworkIdentity GetSpawnedObject(uint netId)
        {
            spawnedObjects.TryGetValue(netId, out var identity);
            return identity;
        }

        /// <summary>
        /// 注销对象（对象销毁时调用）
        /// </summary>
        internal static void Unspawn(uint netId)
        {
            spawnedObjects.Remove(netId);
        }

        #endregion

        #region Send Methods

        /// <summary>
        /// 发送握手消息
        /// </summary>
        private static void SendHandshake()
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 1,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["handshake_req"] = new Dictionary<string, object>
                {
                    ["client_id"] = SystemInfo.deviceUniqueIdentifier,
                    ["version"] = "1.0.0",
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送心跳
        /// </summary>
        public static void SendHeartbeat()
        {
            if (!isConnected) return;

            var msg = new Dictionary<string, object>
            {
                ["type"] = 3,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["heartbeat"] = new Dictionary<string, object>
                {
                    ["client_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["ping_ms"] = connection.pingMs,
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送Command
        /// </summary>
        public static void SendCommand(CommandMessage cmd)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 30,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["command_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = cmd.netId,
                    ["method_hash"] = cmd.methodHash,
                    ["channel"] = cmd.channel,
                    ["requires_authority"] = cmd.requiresAuthority,
                    ["args"] = Convert.ToBase64String(cmd.args),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送ClientRpc（简化版，实际服务端调用）
        /// </summary>
        public static void SendClientRpc(ClientRpcMessage rpc)
        {
            // 客户端一般不主动发送ClientRpc，这里为测试用
            var msg = new Dictionary<string, object>
            {
                ["type"] = 31,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["client_rpc_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = rpc.netId,
                    ["method_hash"] = rpc.methodHash,
                    ["channel"] = rpc.channel,
                    ["include_owner"] = rpc.includeOwner,
                    ["args"] = Convert.ToBase64String(rpc.args),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送TargetRpc
        /// </summary>
        public static void SendTargetRpc(TargetRpcMessage rpc)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 32,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["target_rpc_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = rpc.netId,
                    ["method_hash"] = rpc.methodHash,
                    ["target_conn_id"] = rpc.targetConnId,
                    ["channel"] = rpc.channel,
                    ["args"] = Convert.ToBase64String(rpc.args),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送SyncVar更新
        /// </summary>
        public static void SendSyncVar(uint netId, string component, int dirtyMask, byte[] payload)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 20,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["sync_var_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = netId,
                    ["component"] = component,
                    ["dirty_mask"] = dirtyMask,
                    ["payload"] = Convert.ToBase64String(payload),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送输入帧
        /// </summary>
        public static void SendInputFrame(uint netId, int frame, byte[] inputData)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 23,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["input_frame_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = netId,
                    ["frame"] = frame,
                    ["input_data"] = Convert.ToBase64String(inputData),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 重连服务器
        /// </summary>
        public static void Reconnect(string serverUrl)
        {
            // 先断开现有连接
            if (isConnected)
            {
                Disconnect();
            }

            // 连接新地址
            Connect(serverUrl);
        }

        /// <summary>
        /// 发送时间同步请求（用于延迟补偿）
        /// </summary>
        public static void SendTimeSync()
        {
            if (!isConnected) return;

            var msg = new Dictionary<string, object>
            {
                ["type"] = 90,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["time_sync_req"] = new Dictionary<string, object>
                {
                    ["client_send_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            };

            SendJson(msg);
        }

        public static void SendJson(Dictionary<string, object> msg)
        {
            string json = MiniJson.Serialize(msg);
            byte[] data = System.Text.Encoding.UTF8.GetBytes(json);
            transport?.Send(data);
        }

        #endregion

        #region RPC Invocation

        /// <summary>
        /// 注册RPC处理器
        /// </summary>
        public static void RegisterRpcHandler(string methodHash, Delegate handler)
        {
            rpcHandlers[methodHash] = handler;
        }

        private static void InvokeRpc(NetworkIdentity identity, string methodHash, byte[] args)
        {
            // 这里简化实现，实际应用中应使用代码生成或反射
            // Mirror的做法是在编译时生成RPC代码
            Debug.Log($"[MiniLink] 收到RPC: {methodHash}, netId={identity.netId}");

            if (rpcHandlers.TryGetValue(methodHash, out var handler))
            {
                // 解析参数并调用
                // 实际实现需要根据方法签名解析
            }
        }

        #endregion

        #region Cleanup

        private static void Cleanup()
        {
            spawnedObjects.Clear();
            localPlayer = null;

            if (transport != null)
            {
                transport.OnConnected -= OnConnected;
                transport.OnDisconnected -= OnDisconnected;
                transport.OnDataReceived -= OnDataReceived;
                transport = null;
            }

            connection = null;
        }

        #endregion
    }

    /// <summary>
    /// 网络连接状态
    /// </summary>
    public class NetworkConnection
    {
        public string connectionId { get; set; }
        public string serverUrl { get; set; }
        public string sessionId { get; set; }
        public string reconnectToken { get; set; }
        public bool isConnected { get; set; }
        public bool isAuthenticated { get; set; }
        public int pingMs { get; private set; }

        private int seqCounter = 0;

        public int nextSeq()
        {
            return ++seqCounter;
        }

        public void UpdatePing(int ping)
        {
            pingMs = ping;
        }
    }

    /// <summary>
    /// 传输层接口
    /// </summary>
    public interface ITransport
    {
        event Action OnConnected;
        event Action OnDisconnected;
        event Action<byte[]> OnDataReceived;

        void Connect(string url);
        void Disconnect();
        void Send(byte[] data);
    }
}
