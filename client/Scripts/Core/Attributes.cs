using System;

namespace MiniLink
{
    /// <summary>
    /// 标记同步变量，服务端变更自动同步到客户端
    /// 参考 Mirror [SyncVar] 设计，支持脏标记增量同步
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class SyncVarAttribute : Attribute
    {
        /// <summary>
        /// 变量变更时的回调方法名
        /// </summary>
        public string hook { get; set; }

        /// <summary>
        /// 脏位索引（自动分配，0-63，最多64个SyncVar）
        /// </summary>
        public int dirtyBitIndex { get; set; } = -1;

        public SyncVarAttribute() { }

        public SyncVarAttribute(string hook)
        {
            this.hook = hook;
        }
    }

    /// <summary>
    /// 客户端→服务端远程调用
    /// 参考 Mirror [Command] 设计
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CommandAttribute : Attribute
    {
        /// <summary>
        /// 通道：0=可靠, 1=不可靠
        /// </summary>
        public int channel { get; set; } = 0;

        /// <summary>
        /// 是否需要拥有者权限（默认需要）
        /// 设为false允许非拥有者调用
        /// </summary>
        public bool requiresAuthority { get; set; } = true;

        public CommandAttribute() { }
    }

    /// <summary>
    /// 服务端→所有客户端远程调用
    /// 参考 Mirror [ClientRpc] 设计
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class ClientRpcAttribute : Attribute
    {
        /// <summary>
        /// 通道：0=可靠, 1=不可靠
        /// </summary>
        public int channel { get; set; } = 0;

        /// <summary>
        /// 是否包含拥有者（默认包含）
        /// 设为false排除调用者的拥有者客户端
        /// </summary>
        public bool includeOwner { get; set; } = true;

        public ClientRpcAttribute() { }
    }

    /// <summary>
    /// 服务端→指定客户端远程调用
    /// 参考 Mirror [TargetRpc] 设计
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class TargetRpcAttribute : Attribute
    {
        /// <summary>
        /// 通道：0=可靠, 1=不可靠
        /// </summary>
        public int channel { get; set; } = 0;

        public TargetRpcAttribute() { }
    }

    /// <summary>
    /// 标记方法仅在服务端执行
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class ServerAttribute : Attribute
    {
        public bool error { get; set; } = true;

        public ServerAttribute() { }
    }

    /// <summary>
    /// 标记方法仅在客户端执行
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class ClientAttribute : Attribute
    {
        public bool error { get; set; } = true;

        public ClientAttribute() { }
    }

    /// <summary>
    /// 同步方向
    /// </summary>
    public enum SyncDirection
    {
        /// <summary>服务端→客户端（默认）</summary>
        ServerToClient = 0,
        /// <summary>客户端→服务端（需权限）</summary>
        ClientToServer = 1,
    }

    /// <summary>
    /// 同步模式
    /// </summary>
    public enum SyncMode
    {
        /// <summary>所有观察者同步</summary>
        Observers = 0,
        /// <summary>仅拥有者同步</summary>
        Owner = 1,
    }

    /// <summary>
    /// 同步方法类型
    /// </summary>
    public enum SyncMethod
    {
        /// <summary>可靠通道</summary>
        Reliable = 0,
        /// <summary>混合（重要数据可靠，其余不可靠）</summary>
        Hybrid = 1,
    }
}
