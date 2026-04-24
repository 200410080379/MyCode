using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 网络对象标识组件
    /// 参考 Mirror NetworkIdentity 设计，精简为小程序场景所需
    /// 挂载到所有需要网络同步的 GameObject 上
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MiniLink/NetworkIdentity")]
    public class NetworkIdentity : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Prefab Settings")]
        [Tooltip("预制体哈希值，用于服务端生成对象时匹配")]
        [SerializeField] private int prefabHash = 0;

        [Header("Sync Settings")]
        [Tooltip("同步方向")]
        [SerializeField] private SyncDirection syncDirection = SyncDirection.ServerToClient;

        [Tooltip("同步模式：观察者/拥有者")]
        [SerializeField] private SyncMode syncMode = SyncMode.Observers;

        [Tooltip("同步间隔(秒)")]
        [SerializeField] private float syncInterval = 0.05f; // 20Hz

        #endregion

        #region Public Properties

        /// <summary>网络唯一ID，由服务端分配</summary>
        public uint netId { get; internal set; }

        /// <summary>是否已分配netId</summary>
        public bool isSpawned => netId != 0;

        /// <summary>预制体哈希</summary>
        public int PrefabHash => prefabHash;

        /// <summary>该对象的拥有者连接ID</summary>
        public string ownerConnId { get; internal set; }

        /// <summary>是否是本地玩家对象</summary>
        public bool isLocalPlayer { get; internal set; }

        /// <summary>是否是客户端上的对象（已连接且已生成）</summary>
        public bool isClient { get; internal set; }

        /// <summary>是否是服务端上的对象</summary>
        public bool isServer { get; internal set; }

        /// <summary>本地是否拥有此对象</summary>
        public bool isOwned => isLocalPlayer || 
            (NetworkClient.connection?.connectionId == ownerConnId);

        /// <summary>同步方向</summary>
        public SyncDirection SyncDirection => syncDirection;

        /// <summary>同步模式</summary>
        public SyncMode SyncMode => syncMode;

        /// <summary>同步间隔</summary>
        public float SyncInterval => syncInterval;

        /// <summary>上次同步时间</summary>
        public float lastSyncTime { get; internal set; }

        /// <summary>脏标记位掩码（参考Mirror dirty mask机制）</summary>
        public ulong dirtyMask { get; internal set; }

        /// <summary>是否有脏数据</summary>
        public bool isDirty => dirtyMask != 0;

        #endregion

        #region Internal Fields

        /// <summary>缓存的NetworkBehaviour组件列表</summary>
        internal NetworkBehaviour[] networkBehaviours;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // 缓存所有NetworkBehaviour子组件
            RefreshNetworkBehaviours();
        }

        private void OnDestroy()
        {
            // 从管理器注销
            if (isSpawned)
            {
                NetworkClient.Unspawn(netId);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 刷新缓存的NetworkBehaviour组件
        /// </summary>
        public void RefreshNetworkBehaviours()
        {
            networkBehaviours = GetComponents<NetworkBehaviour>();
        }

        /// <summary>
        /// 标记指定脏位（参考Mirror SyncVar脏标记）
        /// </summary>
        public void SetDirtyBit(int bitIndex)
        {
            dirtyMask |= (1UL << bitIndex);
        }

        /// <summary>
        /// 清除脏标记
        /// </summary>
        public void ClearDirtyMask(ulong mask = ulong.MaxValue)
        {
            dirtyMask &= ~mask;
        }

        /// <summary>
        /// 检查指定脏位
        /// </summary>
        public bool IsDirtyBit(int bitIndex)
        {
            return (dirtyMask & (1UL << bitIndex)) != 0;
        }

        /// <summary>
        /// 序列化所有脏SyncVar（参考Mirror NetworkIdentitySerialization）
        /// </summary>
        public void SerializeSyncVars(NetworkWriter writer, bool initialState = false)
        {
            if (networkBehaviours == null) return;

            foreach (var nb in networkBehaviours)
            {
                if (nb == null) continue;
                nb.SerializeSyncVars(writer, initialState);
            }
        }

        /// <summary>
        /// 反序列化SyncVar
        /// </summary>
        public void DeserializeSyncVars(NetworkReader reader, bool initialState = false)
        {
            if (networkBehaviours == null) return;

            foreach (var nb in networkBehaviours)
            {
                if (nb == null) continue;
                nb.DeserializeSyncVars(reader, initialState);
            }
        }

        /// <summary>
        /// 通知所有NetworkBehaviour对象已生成
        /// </summary>
        public void NotifySpawned()
        {
            if (networkBehaviours == null) return;

            foreach (var nb in networkBehaviours)
            {
                if (nb == null) continue;

                if (isClient) nb.OnStartClient();
                if (isLocalPlayer) nb.OnStartLocalPlayer();
                if (isOwned) nb.OnStartAuthority();
            }
        }

        /// <summary>
        /// 通知所有NetworkBehaviour对象即将销毁
        /// </summary>
        public void NotifyDestroy()
        {
            if (networkBehaviours == null) return;

            foreach (var nb in networkBehaviours)
            {
                if (nb == null) continue;

                if (isOwned) nb.OnStopAuthority();
                if (isClient) nb.OnStopClient();
            }
        }

        #endregion

        #region Editor

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (prefabHash == 0)
            {
                // 自动计算预制体哈希
                prefabHash = gameObject.name.GetHashCode();
            }
        }
#endif

        #endregion
    }
}
