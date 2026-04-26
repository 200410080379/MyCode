# MiniLink

> 小程序游戏多人联机轻量化框架

## 项目简介

MiniLink 是一个面向微信/抖音小程序游戏的轻量化多人联机框架，参考 Unity Mirror 网络框架设计思想，针对小程序环境（仅 WebSocket、Node.js 服务端、核心代码 ≤500KB）进行了深度适配。

### 核心特性

- 🔄 **混合同步** — SyncVar 脏标记 + 快照插值（状态同步）+ 确定性帧同步
- 🏠 **房间系统** — 创建/加入/匹配/准备/开始，6位数字房间号
- 🔌 **多端适配** — 微信小程序 + 抖音小程序 + 原生 WebSocket
- 🔗 **断线重连** — 30秒重连窗口，自动补发丢失帧
- ⏱ **延迟补偿** — NTP 风格时钟同步 + 历史状态回滚
- 📦 **轻量级** — 核心代码 < 500KB，Node.js 服务端低成本部署

## 技术架构

```
客户端 (Unity C#)  ←── WebSocket ──→  服务端 (Node.js)
     ↕                                       ↕
  ITransport                          MiniLinkServer
  ├─ WebSocketTransport (原生)        ├─ RoomManager
  ├─ WxWebSocketTransport (微信)      ├─ SyncManager
  └─ TtWebSocketTransport (抖音)      ├─ FrameSyncManager
                                      ├─ ReconnectionManager
                                      ├─ MessageHandler
                                      └─ WechatAdapter
```

### 四大核心模块

| 模块 | 说明 | 参考 Mirror |
|------|------|------------|
| Transport | WebSocket 传输层，支持原生/微信/抖音 | Transport |
| Sync | SyncVar 脏标记 + 快照插值 + 帧同步 | NetworkClient/SyncVar |
| Room | 房间创建、匹配、状态管理 | NetworkRoomManager |
| Adapter | 微信登录、分享、JSLIB 桥接 | - |

## 快速开始

### 服务端

```bash
cd server
npm install
npm start
# WebSocket 服务启动在 ws://0.0.0.0:9000
# HTTP 监控服务启动在 http://0.0.0.0:9001
```

### 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `PORT` | 9000 | WebSocket 端口 |
| `HOST` | 0.0.0.0 | 监听地址 |
| `SERIALIZER` | json | 序列化模式 (json/protobuf) |
| `WX_APP_ID` | - | 微信小程序 AppID |
| `WX_APP_SECRET` | - | 微信小程序 AppSecret |

### 客户端 (Unity)

详见 [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md)

```csharp
// 连接服务器
NetworkManager.GetOrCreate().ConnectToServer("ws://localhost:9000");

// 创建房间
NetworkRoomManager.singleton.CreateRoom("我的房间");

// 定义同步变量
public class PlayerHealth : NetworkBehaviour
{
    [SyncVar]
    public SyncVar<int> health = new SyncVar<int>(100);

    [Command]
    public void TakeDamage(int amount)
    {
        health.Value -= amount;
    }

    [ClientRpc]
    public void RpcOnDeath()
    {
        // 在所有客户端执行
    }
}
```

## 项目结构

```
MiniLink/
├── client/                    # Unity 客户端
│   ├── Scripts/
│   │   ├── Core/             # 核心类 (NetworkClient, NetworkBehaviour, NetworkIdentity)
│   │   ├── Transport/        # 传输层 (WebSocket, 微信, 抖音)
│   │   ├── Components/       # 组件 (FrameSync, SnapshotInterpolation, Reconnection)
│   │   ├── Room/             # 房间 (NetworkRoomManager, NetworkRoomPlayer)
│   │   └── Wechat/           # 微信/抖音登录适配
│   └── Plugins/WebGL/        # JSLIB 桥接文件
├── server/                    # Node.js 服务端
│   └── src/
│       ├── server.js         # 核心服务器类
│       ├── connection.js     # 连接管理
│       ├── message-handler.js# 消息路由
│       ├── room-manager.js   # 房间管理
│       ├── sync-manager.js   # 同步管理
│       ├── frame-sync.js     # 帧同步
│       ├── reconnection-manager.js # 断线重连
│       ├── wechat-adapter.js # 微信适配
│       ├── serializer.js     # 序列化抽象层
│       └── index.js          # 入口
├── shared/
│   └── proto/
│       └── protocol.proto    # Protobuf 协议定义
├── docs/
│   ├── ARCHITECTURE.md       # 架构设计文档
│   └── GETTING-STARTED.md    # 使用指南
└── papers/                    # 论文资料
```

## 与 Unity Mirror 的对比

| 特性 | Mirror | MiniLink |
|------|--------|----------|
| 传输层 | KCP/TCP/WebSocket | WebSocket only |
| 服务端 | Unity Headless | Node.js |
| 序列化 | Weaver (IL注入) | 手动 + 反射 |
| 协议 | 自定义二进制 | JSON/Protobuf 可切换 |
| 同步方式 | SyncVar + RPC | SyncVar + 帧同步混合 |
| 断线重连 | 需插件 | 内建 30s 重连窗口 |
| 包体积 | ~2MB | < 500KB |

## 性能目标

- 延迟 ≤ 150ms（同区域部署）
- 同步精度 ≥ 85%（快照插值 + 帧同步混合）
- 断线重连成功率 ≥ 95%（30s 重连窗口）

## 作者

李康 — 湖南师范大学信息科学与工程学院 · 软件工程 2022 级

## 许可证

MIT License
