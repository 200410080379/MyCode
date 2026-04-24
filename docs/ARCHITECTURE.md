# MiniLink - 小程序游戏多人联机轻量化框架

## 框架命名

**MiniLink** — Mini Program + Link，寓意小程序场景下的联机连接。

## 设计理念

参考 Unity Mirror 的核心设计思想（NetworkIdentity、NetworkBehaviour、RPC机制、SyncVar同步），
但完全针对微信小程序场景重新实现：

- Mirror: 基于原生Socket (TCP/KCP) → MiniLink: 基于WebSocket（小程序唯一可用协议）
- Mirror: 重型框架，功能全面 → MiniLink: 轻量化，核心代码≤500KB
- Mirror: 依赖Unity IL2CPP → MiniLink: 适配微信小程序WebGL环境
- Mirror: 服务端C# → MiniLink: 服务端Node.js（低成本部署）

## 架构总览

```
┌─────────────────────────────────────────────────┐
│                 MiniLink 框架                     │
├─────────────────────────────────────────────────┤
│                                                  │
│  ┌──────────┐  ┌──────────┐  ┌──────────────┐  │
│  │ 通信层    │  │ 同步层    │  │ 房间管理层    │  │
│  │ Transport │  │  Sync    │  │  RoomManager  │  │
│  └──────────┘  └──────────┘  └──────────────┘  │
│                                                  │
│  ┌──────────────────────────────────────────┐   │
│  │         业务适配层 (Adapter)              │   │
│  │   微信登录 │ 社交分享 │ 引擎适配          │   │
│  └──────────────────────────────────────────┘   │
│                                                  │
└─────────────────────────────────────────────────┘

客户端 (Unity/Cocos)           服务端 (Node.js)
┌──────────────────┐          ┌──────────────────┐
│ MiniLinkClient    │◄─WS/WS─►│ MiniLinkServer    │
│ ├─ NetworkIdentity│          │ ├─ RoomManager    │
│ ├─ NetworkBehaviour│         │ ├─ SyncManager    │
│ ├─ SyncVar        │          │ ├─ ConnectionMgr  │
│ ├─ Command/Rpc    │          │ └─ WechatAdapter  │
│ └─ WebSocketTransport│       └──────────────────┘
└──────────────────┘
```

## 四大核心模块

### 1. 通信层 (Transport Layer)
- 基于 WebSocket 双向通信
- Protobuf 数据序列化（精简数据体积）
- 心跳检测与断线重连机制
- 适配小程序网络不稳定特性

### 2. 同步层 (Sync Layer)
- 状态同步为主，帧同步为辅的混合同步策略
- SyncVar 脏标记机制（参考Mirror）
- 插值补间与延迟补偿算法
- 快照插值（参考Mirror SnapshotInterpolation）

### 3. 房间管理层 (Room Manager)
- 房间创建、加入、匹配、解散
- 参数配置（人数、密码）
- 对接微信登录与社交分享
- 准备状态管理

### 4. 业务适配层 (Adapter Layer)
- 微信小游戏登录对接
- 社交分享链路
- 引擎差异适配（Unity/Cocos Creator）
- 小程序包体优化

## 目录结构

```
MyCode/
├── server/                    # Node.js 服务端
│   ├── src/
│   │   ├── index.js           # 服务端入口
│   │   ├── server.js          # 主服务器类
│   │   ├── connection.js      # 连接管理
│   │   ├── room-manager.js    # 房间管理
│   │   ├── sync-manager.js    # 同步管理
│   │   ├── message-handler.js # 消息处理
│   │   ├── wechat-adapter.js  # 微信适配
│   │   └── proto/             # Protobuf 协议定义
│   │       └── protocol.proto
│   ├── package.json
│   └── README.md
├── client/                    # Unity C# 客户端
│   ├── Scripts/
│   │   ├── Core/
│   │   │   ├── NetworkIdentity.cs
│   │   │   ├── NetworkBehaviour.cs
│   │   │   ├── NetworkClient.cs
│   │   │   ├── NetworkManager.cs
│   │   │   ├── Attributes.cs
│   │   │   └── RemoteCalls.cs
│   │   ├── Transport/
│   │   │   ├── WebSocketTransport.cs
│   │   │   └── WxWebSocket.cs
│   │   ├── Sync/
│   │   │   ├── SyncVar.cs
│   │   │   ├── SyncList.cs
│   │   │   ├── SnapshotInterpolation.cs
│   │   │   └── LagCompensation.cs
│   │   ├── Room/
│   │   │   ├── NetworkRoomManager.cs
│   │   │   └── NetworkRoomPlayer.cs
│   │   └── Adapter/
│   │       ├── WxMiniProgramAdapter.cs
│   │       └── WechatLogin.cs
│   └── README.md
├── shared/                    # 共享协议定义
│   └── proto/
│       └── protocol.proto
├── docs/                      # 文档
│   ├── ARCHITECTURE.md
│   ├── API.md
│   └── GETTING-STARTED.md
└── demo/                      # Demo游戏
    └── README.md
```

## 与Mirror的对应关系

| Mirror概念           | MiniLink对应                    | 说明                          |
|---------------------|--------------------------------|-------------------------------|
| Transport (抽象)     | WebSocketTransport             | 小程序仅支持WebSocket         |
| Telepathy/KCP       | 原生WebSocket                  | 无需多传输层，统一WS           |
| NetworkIdentity     | NetworkIdentity                | 简化版，精简字段               |
| NetworkBehaviour    | NetworkBehaviour               | 简化版，保留核心同步逻辑       |
| SyncVar             | SyncVar                        | 脏标记+增量同步                |
| SyncList            | SyncList                       | 简化版集合同步                 |
| [Command]           | [Command]                      | 客户端→服务端RPC              |
| [ClientRpc]         | [ClientRpc]                    | 服务端→所有客户端RPC           |
| [TargetRpc]         | [TargetRpc]                    | 服务端→指定客户端RPC           |
| NetworkManager      | NetworkManager                 | 简化版生命周期管理             |
| NetworkRoomManager  | NetworkRoomManager             | 适配小程序房间场景             |
| NetworkConnection   | NetworkConnection              | WebSocket连接封装              |
| Batcher/Unbatcher   | 消息批处理                      | 简化版批处理                   |
| SnapshotInterpolation| SnapshotInterpolation         | 快照插值                       |
| InterestManagement  | (暂不实现)                     | 小游戏2-4人无需兴趣管理        |

## 性能目标

- 联机延迟：≤150ms（弱网环境≤300ms延迟下）
- 同步精度：≥85%
- 框架核心代码体积：≤500KB
- 断线重连成功率：≥95%
- 连续运行2小时无崩溃
- 重连后数据同步一致性：≥98%
