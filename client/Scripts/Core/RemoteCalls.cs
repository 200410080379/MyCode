using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// #24: 远程调用工具类
    /// 
    /// 统一管理 Command / ClientRpc / TargetRpc 的注册和调用
    /// 提供 methodHash 计算和类型安全的包装
    /// 
    /// 使用方式：
    /// <code>
    /// // 注册 RPC handler
    /// RemoteCalls.RegisterRpc("OnHealthChanged", reader => {
    ///     int newHealth = reader.ReadInt();
    ///     // 处理逻辑
    /// });
    /// 
    /// // 发送 Command
    /// RemoteCalls.SendCommand(netId, "TakeDamage", writer => {
    ///     writer.WriteInt(10);
    /// });
    /// </code>
    /// </summary>
    public static class RemoteCalls
    {
        #region Internal Types

        /// <summary>RPC 回调类型</summary>
        private delegate void RpcCallback(NetworkReader reader);

        #endregion

        #region Static Registry

        /// <summary>RPC 处理器注册表 methodHash -> callback</summary>
        private static readonly Dictionary<string, Action<NetworkReader>> rpcRegistry
            = new Dictionary<string, Action<NetworkReader>>();

        /// <summary>Command 处理器注册表 methodHash -> callback</summary>
        private static readonly Dictionary<string, Action<NetworkReader>> commandRegistry
            = new Dictionary<string, Action<NetworkReader>>();

        #endregion

        #region Registration API

        /// <summary>
        /// 注册 ClientRpc / TargetRpc 处理器
        /// </summary>
        /// <param name="methodHash">方法标识（通常为 "ClassName.MethodName"）</param>
        /// <param name="handler">处理函数，接收 NetworkReader</param>
        public static void RegisterRpc(string methodHash, Action<NetworkReader> handler)
        {
            if (string.IsNullOrEmpty(methodHash))
                throw new ArgumentNullException(nameof(methodHash));

            rpcRegistry[methodHash] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// 注册 Command 处理器（服务端用，客户端一般不注册 Command handler）
        /// </summary>
        public static void RegisterCommand(string methodHash, Action<NetworkReader> handler)
        {
            if (string.IsNullOrEmpty(methodHash))
                throw new ArgumentNullException(nameof(methodHash));

            commandRegistry[methodHash] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// 注销 RPC 处理器
        /// </summary>
        public static void UnregisterRpc(string methodHash)
        {
            rpcRegistry.Remove(methodHash);
        }

        /// <summary>
        /// 注销 Command 处理器
        /// </summary>
        public static void UnregisterCommand(string methodHash)
        {
            commandRegistry.Remove(methodHash);
        }

        /// <summary>
        /// 清空所有注册
        /// </summary>
        public static void ClearAll()
        {
            rpcRegistry.Clear();
            commandRegistry.Clear();
        }

        #endregion

        #region Invocation API

        /// <summary>
        /// 调用已注册的 RPC handler
        /// </summary>
        /// <returns>是否找到并执行了 handler</returns>
        public static bool InvokeRpc(string methodHash, byte[] args)
        {
            if (string.IsNullOrEmpty(methodHash)) return false;

            if (rpcRegistry.TryGetValue(methodHash, out var handler))
            {
                try
                {
                    using (var reader = new NetworkReader(args))
                    {
                        handler(reader);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[RemoteCalls] RPC调用异常: {methodHash}, {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// 调用已注册的 Command handler
        /// </summary>
        public static bool InvokeCommand(string methodHash, byte[] args)
        {
            if (string.IsNullOrEmpty(methodHash)) return false;

            if (commandRegistry.TryGetValue(methodHash, out var handler))
            {
                try
                {
                    using (var reader = new NetworkReader(args))
                    {
                        handler(reader);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[RemoteCalls] Command调用异常: {methodHash}, {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        #endregion

        #region Send Helpers

        /// <summary>
        /// 发送 Command（客户端 → 服务端）
        /// </summary>
        /// <param name="netId">网络对象ID</param>
        /// <param name="methodHash">方法标识</param>
        /// <param name="writeArgs">参数写入回调</param>
        /// <param name="requiresAuthority">是否需要权限</param>
        public static void SendCommand(uint netId, string methodHash, Action<NetworkWriter> writeArgs, bool requiresAuthority = true)
        {
            if (!NetworkClient.isConnected)
            {
                Debug.LogWarning("[RemoteCalls] 未连接，无法发送 Command");
                return;
            }

            byte[] args;
            using (var writer = NetworkWriter.Pool.Get())
            {
                writeArgs?.Invoke(writer);
                args = writer.ToArray();
            }

            var cmd = new CommandMessage
            {
                netId = netId,
                methodHash = methodHash,
                channel = 0,
                requiresAuthority = requiresAuthority,
                args = args,
            };

            NetworkClient.SendCommand(cmd);
        }

        /// <summary>
        /// 发送 ClientRpc（服务端 → 所有客户端）
        /// </summary>
        public static void SendClientRpc(uint netId, string methodHash, Action<NetworkWriter> writeArgs, bool includeOwner = true)
        {
            byte[] args;
            using (var writer = NetworkWriter.Pool.Get())
            {
                writeArgs?.Invoke(writer);
                args = writer.ToArray();
            }

            var rpc = new ClientRpcMessage
            {
                netId = netId,
                methodHash = methodHash,
                channel = 0,
                includeOwner = includeOwner,
                args = args,
            };

            NetworkClient.SendClientRpc(rpc);
        }

        /// <summary>
        /// 发送 TargetRpc（服务端 → 指定客户端）
        /// </summary>
        public static void SendTargetRpc(uint netId, string methodHash, string targetConnId, Action<NetworkWriter> writeArgs)
        {
            byte[] args;
            using (var writer = NetworkWriter.Pool.Get())
            {
                writeArgs?.Invoke(writer);
                args = writer.ToArray();
            }

            var rpc = new TargetRpcMessage
            {
                netId = netId,
                methodHash = methodHash,
                targetConnId = targetConnId,
                channel = 0,
                args = args,
            };

            NetworkClient.SendTargetRpc(rpc);
        }

        #endregion

        #region Utility

        /// <summary>
        /// 计算方法哈希值
        /// 格式：ClassName.MethodName 的稳定哈希
        /// </summary>
        public static string ComputeMethodHash(string className, string methodName)
        {
            return $"{className}.{methodName}";
        }

        /// <summary>
        /// 获取已注册的 RPC 数量
        /// </summary>
        public static int GetRegisteredRpcCount() => rpcRegistry.Count;

        /// <summary>
        /// 获取已注册的 Command 数量
        /// </summary>
        public static int GetRegisteredCommandCount() => commandRegistry.Count;

        #endregion
    }
}
