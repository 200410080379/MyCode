/**
 * MiniLink Server - 核心服务器类
 * 参考 Mirror NetworkServer 设计，适配 WebSocket + Node.js 环境
 */
const { WebSocketServer } = require('ws');
const { v4: uuidv4 } = require('uuid');
const { Connection } = require('./connection');
const { RoomManager } = require('./room-manager');
const { SyncManager } = require('./sync-manager');
const { FrameSyncManager } = require('./frame-sync');
const { ReconnectionManager } = require('./reconnection-manager');
const { MessageHandler } = require('./message-handler');
const { WechatAdapter } = require('./wechat-adapter');

class MiniLinkServer {
  constructor(options = {}) {
    this.port = options.port || 9000;
    this.host = options.host || '0.0.0.0';
    this.heartbeatInterval = options.heartbeatInterval || 15000;
    this.heartbeatTimeout = options.heartbeatTimeout || 30000;
    this.reconnectTTL = options.reconnectTTL || 30000;
    this.maxRooms = options.maxRooms || 100;
    this.maxPlayersPerRoom = options.maxPlayersPerRoom || 4;
    this.syncRate = options.syncRate || 20;
    this.wxConfig = options.wx || {};

    // 核心组件
    this.wss = null;
    this.connections = new Map(); // connId -> Connection
    this.roomManager = null;
    this.syncManager = null;
    this.frameSyncManager = null;
    this.reconnectionManager = null;
    this.messageHandler = null;
    this.wechatAdapter = null;

    // 网络对象注册表（参考 Mirror NetworkServer.spawned）
    this.spawned = new Map(); // netId -> { connId, prefabHash, state }

    // 序列号计数器
    this._seq = 0;

    // 心跳定时器
    this._heartbeatTimer = null;

    // 同步定时器
    this._syncTimer = null;

    // 运行状态
    this.active = false;

    // 事件回调
    this._onConnect = options.onConnect || null;
    this._onDisconnect = options.onDisconnect || null;
  }

  /**
   * 启动服务器
   */
  start() {
    if (this.active) {
      console.warn('[MiniLink] 服务器已在运行');
      return;
    }

    // 初始化组件
    this.wechatAdapter = new WechatAdapter(this.wxConfig);
    this.roomManager = new RoomManager(this);
    this.syncManager = new SyncManager(this);
    this.frameSyncManager = new FrameSyncManager(this);
    this.reconnectionManager = new ReconnectionManager(this);
    this.messageHandler = new MessageHandler(this);

    // 创建 WebSocket 服务器
    this.wss = new WebSocketServer({
      host: this.host,
      port: this.port,
      maxPayload: 64 * 1024, // 64KB 最大消息
      perMessageDeflate: false, // 小消息关闭压缩以降低延迟
    });

    this.wss.on('listening', () => {
      console.log(`[MiniLink] 服务器启动成功 ws://${this.host}:${this.port}`);
    });

    this.wss.on('connection', (ws, req) => {
      this._onNewConnection(ws, req);
    });

    this.wss.on('error', (err) => {
      console.error('[MiniLink] WebSocket 服务器错误:', err);
    });

    // 启动心跳检测
    this._heartbeatTimer = setInterval(() => {
      this._checkHeartbeats();
    }, this.heartbeatInterval);

    // 启动同步循环
    const syncInterval = Math.floor(1000 / this.syncRate);
    this._syncTimer = setInterval(() => {
      this.syncManager.tick();
    }, syncInterval);

    this.active = true;
  }

  /**
   * 停止服务器
   */
  stop() {
    if (!this.active) return;

    this.active = false;

    // 清除定时器
    if (this._heartbeatTimer) {
      clearInterval(this._heartbeatTimer);
      this._heartbeatTimer = null;
    }
    if (this._syncTimer) {
      clearInterval(this._syncTimer);
      this._syncTimer = null;
    }

    // 关闭所有连接
    for (const [connId, conn] of this.connections) {
      conn.close('server_shutdown');
    }
    this.connections.clear();

    // 关闭 WebSocket 服务器
    if (this.wss) {
      this.wss.close();
      this.wss = null;
    }

    console.log('[MiniLink] 服务器已停止');
  }

  /**
   * 新连接接入
   */
  _onNewConnection(ws, req) {
    const connId = uuidv4();
    const clientAddr = req.headers['x-forwarded-for'] || req.socket.remoteAddress;

    const conn = new Connection(connId, ws, this, {
      clientAddr,
      heartbeatTimeout: this.heartbeatTimeout,
    });

    this.connections.set(connId, conn);

    // 绑定消息处理
    ws.on('message', (data) => {
      this.messageHandler.handle(conn, data);
    });

    ws.on('close', (code, reason) => {
      this._onConnectionClose(connId, code, reason.toString());
    });

    ws.on('error', (err) => {
      console.error(`[MiniLink] 连接错误 ${connId}:`, err.message);
    });

    console.log(`[MiniLink] 新连接 ${connId} from ${clientAddr}`);
  }

  /**
   * 连接关闭
   */
  _onConnectionClose(connId, code, reason) {
    const conn = this.connections.get(connId);
    if (!conn) return;

    // 通知房间管理器（保留重连窗口）
    this.roomManager.onPlayerDisconnect(connId);

    // 标记断线时间（等待重连）
    conn.disconnectedAt = Date.now();

    // 延迟移除（给重连机会）
    setTimeout(() => {
      const c = this.connections.get(connId);
      if (c && c.disconnectedAt) {
        // 未重连，彻底移除
        this.connections.delete(connId);
        this.roomManager.onPlayerRemove(connId);
        console.log(`[MiniLink] 连接彻底移除 ${connId}`);
      }
    }, this.reconnectTTL);

    console.log(`[MiniLink] 连接断开 ${connId} code=${code} reason=${reason}`);
  }

  /**
   * 心跳检测
   */
  _checkHeartbeats() {
    const now = Date.now();
    for (const [connId, conn] of this.connections) {
      if (conn.disconnectedAt) continue; // 已断线等待重连
      if (now - conn.lastHeartbeat > this.heartbeatTimeout) {
        console.warn(`[MiniLink] 心跳超时，断开 ${connId}`);
        conn.close('heartbeat_timeout');
      }
    }
  }

  /**
   * 向指定连接发送消息
   */
  sendTo(connId, message) {
    const conn = this.connections.get(connId);
    if (!conn || conn.disconnectedAt) return false;
    return conn.send(message);
  }

  /**
   * 向房间内所有玩家广播
   */
  broadcastToRoom(roomId, message, excludeConnId = null) {
    const room = this.roomManager.getRoom(roomId);
    if (!room) return;

    for (const player of room.players.values()) {
      if (player.connId === excludeConnId) continue;
      this.sendTo(player.connId, message);
    }
  }

  /**
   * 广播到所有连接
   */
  broadcast(message, excludeConnId = null) {
    for (const [connId, conn] of this.connections) {
      if (connId === excludeConnId) continue;
      if (conn.disconnectedAt) continue;
      conn.send(message);
    }
  }

  /**
   * 生成下一个序列号
   */
  nextSeq() {
    return ++this._seq;
  }

  /**
   * 获取服务器统计信息
   */
  getStats() {
    return {
      active: this.active,
      connections: this.connections.size,
      rooms: this.roomManager ? this.roomManager.getRoomCount() : 0,
      spawnedObjects: this.spawned.size,
      frameSync: this.frameSyncManager ? this.frameSyncManager.getStats() : null,
      reconnection: this.reconnectionManager ? this.reconnectionManager.getStats() : null,
      uptime: process.uptime(),
    };
  }
}

module.exports = { MiniLinkServer };
