/**
 * MiniLink Connection - 连接管理
 * 参考 Mirror NetworkConnection 设计
 */
const { v4: uuidv4 } = require('uuid');

class Connection {
  constructor(connId, ws, server, options = {}) {
    this.connId = connId;
    this.ws = ws;
    this.server = server;

    // 连接信息
    this.clientAddr = options.clientAddr || '';
    this.heartbeatTimeout = options.heartbeatTimeout || 30000;

    // 认证状态
    this.isAuthenticated = false;
    this.playerId = null;
    this.nickname = '';
    this.avatarUrl = '';

    // 心跳
    this.lastHeartbeat = Date.now();
    this.pingMs = 0;

    // 断线重连
    this.disconnectedAt = null;
    this.sessionId = uuidv4();

    // 所属房间
    this.roomId = null;

    // 拥有的网络对象
    this.ownedNetIds = new Set();

    // 消息批处理缓冲（参考 Mirror Batcher）
    this._batchBuffer = [];
    this._batchTimer = null;
    this._batchInterval = 10; // 10ms 批处理间隔
  }

  /**
   * 发送消息
   */
  send(data) {
    if (!this.ws || this.ws.readyState !== 1) return false;
    try {
      const payload = typeof data === 'string' ? data : this.server.serializer.serialize(data);
      this.ws.send(payload);
      return true;
    } catch (err) {
      console.error(`[Connection] 发送失败 ${this.connId}:`, err.message);
      return false;
    }
  }

  /**
   * 批量发送（参考 Mirror Batcher 机制）
   * 将短时间内的多条消息合并发送，减少 WebSocket 帧开销
   */
  sendBatched(data) {
    this._batchBuffer.push(data);

    if (!this._batchTimer) {
      this._batchTimer = setTimeout(() => {
        this._flushBatch();
      }, this._batchInterval);
    }
  }

  /**
   * 刷新批处理缓冲
   */
  _flushBatch() {
    if (this._batchBuffer.length === 0) return;

    if (this.ws && this.ws.readyState === 1) {
      const batch = this._batchBuffer.map(d => typeof d === 'string' ? d : JSON.stringify(d));
      const payload = '[' + batch.join(',') + ']';
      try {
        this.ws.send(payload);
      } catch (err) {
        console.error(`[Connection] 批量发送失败 ${this.connId}:`, err.message);
      }
    }

    this._batchBuffer = [];
    this._batchTimer = null;
  }

  /**
   * 更新心跳
   */
  updateHeartbeat(pingMs = 0) {
    this.lastHeartbeat = Date.now();
    this.pingMs = pingMs;
  }

  /**
   * 关闭连接
   */
  close(reason = '') {
    if (this._batchTimer) {
      clearTimeout(this._batchTimer);
      this._batchTimer = null;
    }
    this._batchBuffer = [];

    try {
      if (this.ws && this.ws.readyState === 1) {
        this.ws.close(1000, reason);
      }
    } catch (err) {
      // 忽略关闭错误
    }
  }

  /**
   * 重连恢复
   */
  reconnect(ws) {
    this.ws = ws;
    this.disconnectedAt = null;
    this.lastHeartbeat = Date.now();

    // 重新绑定消息处理
    ws.on('message', (data) => {
      this.server.messageHandler.handle(this, data);
    });

    ws.on('close', (code, reason) => {
      this.server._onConnectionClose(this.connId, code, reason.toString());
    });

    ws.on('error', (err) => {
      console.error(`[Connection] 重连后错误 ${this.connId}:`, err.message);
    });

    console.log(`[Connection] 重连成功 ${this.connId}`);
  }

  /**
   * 连接是否活跃
   */
  get isActive() {
    return this.ws && this.ws.readyState === 1 && !this.disconnectedAt;
  }

  /**
   * 获取连接信息摘要
   */
  toPlayerInfo() {
    return {
      player_id: this.playerId || this.connId,
      nickname: this.nickname,
      avatar_url: this.avatarUrl,
      is_ready: false,
      is_host: false,
      slot_index: -1,
    };
  }
}

module.exports = { Connection };
