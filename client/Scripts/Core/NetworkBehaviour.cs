using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 网络行为基类
    /// 参考 Mirror NetworkBehaviour 设计，提供 SyncVar/RPC/生命周期回调
    /// 所有需要网络同步的脚本都应继承此类
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        #region Properties

        /// <summary>所属的NetworkIdentity</summary>
        public NetworkIdentity netIdentity { get; internal set; }

        /// <summary>网络唯一ID</summary>
        public uint netId => netIdentity != null ? netIdentity.netId : 0;

        /// <summary>是否是客户端</summary>
        public bool isClient => netIdentity != null && netIdentity.isClient;

        /// <summary>是否是服务端</summary>
        public bool isServer => netIdentity != null && netIdentity.isServer;

        /// <summary>是否是本地玩家</summary>
        public bool isLocalPlayer => netIdentity != null && netIdentity.isLocalPlayer;

        /// <summary>是否拥有此对象</summary>
        public bool isOwned => netIdentity != null && netIdentity.isOwned;

        /// <summary>拥有者连接ID</summary>
        public string ownerConnId => netIdentity != null ? netIdentity.ownerConnId : null;

        /// <summary>同步间隔（秒）</summary>
        public float syncInterval
        {
            get => _syncInterval;
            set => _syncInterval = Mathf.Max(0, value);
        }

        [SerializeField] private float _syncInterval = 0.05f;

        /// <summary>组件脏标记（用于SyncVar增量同步）</summary>
        internal ulong componentDirtyMask { get; set; }

        /// <summary>上次同步时间</summary>
        internal float lastSyncTime { get; set; }

        /// <summary>SyncVar数量（子类实现）</summary>
        internal virtual int syncVarCount => 0;

        #endregion

        #region Unity Lifecycle

        protected virtual void Awake()
        {
            netIdentity = GetComponent<NetworkIdentity>();
        }

        protected virtual void Update()
        {
            // 定期检查同步（服务端）
            if (isServer && isDirty && Time.time - lastSyncTime >= syncInterval)
            {
                lastSyncTime = Time.time;
            }
        }

        #endregion

        #region SyncVar Serialization

        /// <summary>
        /// 序列化脏SyncVar（子类重写）
        /// 参考 Mirror 的脏标记增量序列化
        /// </summary>
        /// <param name="writer">写入器</param>
        /// <param name="initialState">是否全量序列化（首次生成时）</param>
        internal virtual void SerializeSyncVars(NetworkWriter writer, bool initialState)
        {
            // 子类实现示例：
            // if (initialState || IsDirty(0))
            //     writer.WriteString(mySyncVar1);
            // if (initialState || IsDirty(1))
            //     writer.WriteFloat(mySyncVar2);
        }

        /// <summary>
        /// 反序列化SyncVar（子类重写）
        /// </summary>
        internal virtual void DeserializeSyncVars(NetworkReader reader, bool initialState)
        {
            // 子类实现示例：
            // if (initialState || IsDirty(0))
            // {
            //     string oldVal = mySyncVar1;
            //     mySyncVar1 = reader.ReadString();
            //     if (!initialState && mySyncVar1 != oldVal)
            //         OnMySyncVar1Changed(oldVal, mySyncVar1);
            // }
        }

        /// <summary>
        /// 标记脏位
        /// </summary>
        protected void SetDirtyBit(int bitIndex)
        {
            componentDirtyMask |= (1UL << bitIndex);
            if (netIdentity != null)
            {
                netIdentity.SetDirtyBit(bitIndex);
            }
        }

        /// <summary>
        /// 检查脏位
        /// </summary>
        protected bool IsDirty(int bitIndex)
        {
            return (componentDirtyMask & (1UL << bitIndex)) != 0;
        }

        /// <summary>
        /// 清除脏标记
        /// </summary>
        protected void ClearDirtyBits()
        {
            componentDirtyMask = 0;
        }

        /// <summary>
        /// 组件是否有脏数据
        /// </summary>
        public bool isDirty => componentDirtyMask != 0;

        #endregion

        #region RPC Methods

        /// <summary>
        /// 发送Command（客户端→服务端）
        /// 标记 [Command] 的方法通过此机制发送
        /// </summary>
        protected void SendCommand(string methodHash, int channel, bool requiresAuthority, params object[] args)
        {
            if (!isClient)
            {
                Debug.LogWarning($"[MiniLink] Command只能从客户端发送: {methodHash}");
                return;
            }

            if (requiresAuthority && !isOwned)
            {
                Debug.LogWarning($"[MiniLink] 没有权限发送此Command: {methodHash}");
                return;
            }

            var msg = new CommandMessage
            {
                netId = netId,
                methodHash = methodHash,
                channel = channel,
                requiresAuthority = requiresAuthority,
            };

            // 序列化参数
            using (var writer = NetworkWriter.Pool.Get())
            {
                foreach (var arg in args)
                {
                    WriteObject(writer, arg);
                }
                msg.args = writer.ToArray();
            }

            NetworkClient.SendCommand(msg);
        }

        /// <summary>
        /// 发送ClientRpc（服务端→所有客户端）
        /// 标记 [ClientRpc] 的方法通过此机制发送
        /// </summary>
        protected void SendClientRpc(string methodHash, int channel, bool includeOwner, params object[] args)
        {
            if (!isServer)
            {
                Debug.LogWarning($"[MiniLink] ClientRpc只能从服务端发送: {methodHash}");
                return;
            }

            byte[] argBytes = null;
            using (var writer = NetworkWriter.Pool.Get())
            {
                foreach (var arg in args)
                {
                    WriteObject(writer, arg);
                }
                argBytes = writer.ToArray();
            }

            var msg = new ClientRpcMessage
            {
                netId = netId,
                methodHash = methodHash,
                channel = channel,
                includeOwner = includeOwner,
                args = argBytes,
            };

            // 服务端通过 NetworkServer 广播
            // 这里简化为通过 NetworkClient 转发（单机模式）
            NetworkClient.SendClientRpc(msg);
        }

        /// <summary>
        /// 发送TargetRpc（服务端→指定客户端）
        /// </summary>
        protected void SendTargetRpc(string methodHash, string targetConnId, int channel, params object[] args)
        {
            if (!isServer) return;

            byte[] argBytes = null;
            using (var writer = NetworkWriter.Pool.Get())
            {
                foreach (var arg in args)
                {
                    WriteObject(writer, arg);
                }
                argBytes = writer.ToArray();
            }

            var msg = new TargetRpcMessage
            {
                netId = netId,
                methodHash = methodHash,
                targetConnId = targetConnId,
                channel = channel,
                args = argBytes,
            };

            NetworkClient.SendTargetRpc(msg);
        }

        #endregion

        #region Lifecycle Callbacks (子类重写)

        /// <summary>对象在客户端生成时调用</summary>
        public virtual void OnStartClient() { }

        /// <summary>对象在客户端销毁前调用</summary>
        public virtual void OnStopClient() { }

        /// <summary>成为本地玩家时调用</summary>
        public virtual void OnStartLocalPlayer() { }

        /// <summary>获得拥有权时调用</summary>
        public virtual void OnStartAuthority() { }

        /// <summary>失去拥有权时调用</summary>
        public virtual void OnStopAuthority() { }

        /// <summary>SyncVar变更时调用（通用版本）</summary>
        public virtual void OnSyncVarChanged(string varName, object oldValue, object newValue) { }

        #endregion

        #region Utility

        /// <summary>
        /// 通用对象写入（简化版）
        /// </summary>
        protected void WriteObject(NetworkWriter writer, object obj)
        {
            if (obj == null)
            {
                writer.WriteByte(0);
                return;
            }

            var type = obj.GetType();
            if (type == typeof(int)) { writer.WriteByte(1); writer.WriteInt((int)obj); }
            else if (type == typeof(float)) { writer.WriteByte(2); writer.WriteFloat((float)obj); }
            else if (type == typeof(bool)) { writer.WriteByte(3); writer.WriteBool((bool)obj); }
            else if (type == typeof(string)) { writer.WriteByte(4); writer.WriteString((string)obj); }
            else if (type == typeof(Vector3)) { writer.WriteByte(5); writer.WriteVector3((Vector3)obj); }
            else if (type == typeof(Vector2)) { writer.WriteByte(6); writer.WriteVector2((Vector2)obj); }
            else if (type == typeof(Quaternion)) { writer.WriteByte(7); writer.WriteQuaternion((Quaternion)obj); }
            else if (type == typeof(byte[])) { writer.WriteByte(8); writer.WriteBytes((byte[])obj); }
            else
            {
                Debug.LogWarning($"[MiniLink] 不支持的类型: {type.Name}");
            }
        }

        #endregion
    }
}
