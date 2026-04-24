/**
 * MiniLink SyncManager - 同步管理
 * 参考 Mirror 的 SyncVar/NetworkBehaviour 同步机制
 * 实现状态同步为主、帧同步为辅的混合同步策略
 */
class SyncManager {
  constructor(server) {
    this.server = server;

    // 同步频率
    this.syncRate = server.syncRate || 20; // Hz
    this.syncInterval = 1000 / this.syncRate;

    // 脏标记注册表（参考 Mirror 的 SyncVar dirty mask）
    // netId -> { dirtyMask, syncVars, lastSyncTime }
    this.syncRegistry = new Map();

    // 快照缓冲区（参考 Mirror SnapshotInterpolation）
    // netId -> Snapshot[]
    this.snapshotBuffers = new Map();

    // 帧同步输入缓冲
    // roomId -> { frame, inputs: Map<netId, inputData> }
    this.frameBuffers = new Map();

    // tick 计数器
    this._tick = 0;
  }

  /**
   * 注册同步对象
   */
  registerSyncable(netId, componentInfo) {
    this.syncRegistry.set(netId, {
      dirtyMask: 0,
      syncVars: componentInfo.syncVars || {},
      syncMode: componentInfo.syncMode || 'STATE',
      syncInterval: componentInfo.syncInterval || (1 / this.syncRate),
      lastSyncTime: 0,
      componentHash: componentInfo.componentHash || '',
    });

    this.snapshotBuffers.set(netId, []);
  }

  /**
   * 注销同步对象
   */
  unregisterSyncable(netId) {
    this.syncRegistry.delete(netId);
    this.snapshotBuffers.delete(netId);
  }

  /**
   * 标记脏位（参考 Mirror SyncVar dirty mask）
   */
  markDirty(netId, bitIndex) {
    const entry = this.syncRegistry.get(netId);
    if (!entry) return;
    entry.dirtyMask |= (1 << bitIndex);
  }

  /**
   * 清除脏位
   */
  clearDirty(netId, mask = 0xFFFFFFFF) {
    const entry = this.syncRegistry.get(netId);
    if (!entry) return;
    entry.dirtyMask &= ~mask;
  }

  /**
   * 处理 SyncVar 更新（客户端→服务端，ClientToServer方向）
   */
  handleSyncVarUpdate(connId, msg) {
    const entry = this.syncRegistry.get(msg.net_id);
    if (!entry) return;

    // 服务端收到客户端的SyncVar更新，校验权限
    const obj = this.server.spawned.get(msg.net_id);
    if (!obj || obj.ownerConnId !== connId) return;

    // 应用脏标记
    entry.dirtyMask = msg.dirty_mask;

    // 广播给房间内其他玩家
    const conn = this.server.connections.get(connId);
    if (conn && conn.roomId) {
      this.server.broadcastToRoom(conn.roomId, {
        type: 20, // MSG_TYPE_SYNC_VAR
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        sync_var_msg: msg,
      }, connId);
    }
  }

  /**
   * 接收快照（客户端上报状态）
   */
  receiveSnapshot(connId, msg) {
    const { net_id, remote_tick, remote_time, state_data } = msg;

    const buffer = this.snapshotBuffers.get(net_id);
    if (!buffer) return;

    // 存入快照缓冲
    buffer.push({
      tick: remote_tick,
      time: remote_time,
      stateData: state_data,
      receivedAt: Date.now(),
    });

    // 保留最近60个快照（3秒 @ 20Hz）
    while (buffer.length > 60) {
      buffer.shift();
    }

    // 转发到房间内其他客户端
    const conn = this.server.connections.get(connId);
    if (conn && conn.roomId) {
      this.server.broadcastToRoom(conn.roomId, {
        type: 22, // MSG_TYPE_SNAPSHOT
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        snapshot_msg: msg,
      }, connId);
    }
  }

  /**
   * 接收帧同步输入
   */
  receiveInputFrame(connId, msg) {
    const conn = this.server.connections.get(connId);
    if (!conn || !conn.roomId) return;

    let frameBuffer = this.frameBuffers.get(conn.roomId);
    if (!frameBuffer) {
      frameBuffer = { frame: 0, inputs: new Map(), confirmed: new Map() };
      this.frameBuffers.set(conn.roomId, frameBuffer);
    }

    // 存储该玩家此帧的输入
    frameBuffer.inputs.set(msg.net_id, {
      connId,
      frame: msg.frame,
      inputData: msg.input_data,
      receivedAt: Date.now(),
    });
    // 标记该玩家已提交
    frameBuffer.confirmed.set(connId, true);

    // 检查是否所有玩家都已提交输入
    const room = this.server.roomManager.getRoom(conn.roomId);
    if (room) {
      const allConfirmed = this._allPlayersConfirmed(room, frameBuffer);
      if (allConfirmed) {
        // 广播帧确认给所有玩家
        this.server.broadcastToRoom(conn.roomId, {
          type: 23, // MSG_TYPE_INPUT_FRAME
          seq: this.server.nextSeq(),
          timestamp: Date.now(),
          input_frame_msg: {
            frame: frameBuffer.frame,
            inputs: this._serializeFrameInputs(frameBuffer),
          },
        });

        // 推进帧号
        frameBuffer.frame++;
        frameBuffer.inputs.clear();
        frameBuffer.confirmed.clear();
      }
    }
  }

  /**
   * 同步 tick（服务端主动推送）
   * 定时检查脏标记，将变更推送给客户端
   */
  tick() {
    this._tick++;
    const now = Date.now();

    for (const [netId, entry] of this.syncRegistry) {
      // 检查是否有脏数据需要同步
      if (entry.dirtyMask === 0) continue;

      // 检查同步间隔
      if (now - entry.lastSyncTime < entry.syncInterval * 1000) continue;

      // 找到该对象所在房间并广播
      const obj = this.server.spawned.get(netId);
      if (!obj) continue;

      const conn = this.server.connections.get(obj.ownerConnId);
      if (!conn || !conn.roomId) continue;

      // 构造 SyncVar 同步消息
      this.server.broadcastToRoom(conn.roomId, {
        type: 20, // MSG_TYPE_SYNC_VAR
        seq: this.server.nextSeq(),
        timestamp: now,
        sync_var_msg: {
          net_id: netId,
          component: entry.componentHash,
          dirty_mask: entry.dirtyMask,
          payload: this._serializeDirtyVars(entry),
        },
      });

      // 清除脏标记
      entry.dirtyMask = 0;
      entry.lastSyncTime = now;
    }

    // 帧同步超时检查（防止某玩家卡住导致整帧阻塞）
    this._checkFrameTimeouts();
  }

  // ==================== 内部方法 ====================

  _allPlayersConfirmed(room, frameBuffer) {
    for (const [connId, player] of room.players) {
      if (player.disconnected) continue;
      if (!frameBuffer.confirmed.get(connId)) return false;
    }
    return true;
  }

  _serializeFrameInputs(frameBuffer) {
    const result = [];
    for (const [netId, input] of frameBuffer.inputs) {
      result.push({
        net_id: netId,
        frame: input.frame,
        input_data: input.inputData,
      });
    }
    return result;
  }

  _serializeDirtyVars(entry) {
    // 简化实现：将脏标记对应的变量值序列化为JSON buffer
    // 实际项目中应使用 Protobuf 编码
    const dirtyVars = {};
    for (const [name, info] of Object.entries(entry.syncVars)) {
      if (entry.dirtyMask & (1 << info.bitIndex)) {
        dirtyVars[name] = info.currentValue;
      }
    }
    return Buffer.from(JSON.stringify(dirtyVars));
  }

  _checkFrameTimeouts() {
    const now = Date.now();
    const FRAME_TIMEOUT = 200; // 200ms 帧超时

    for (const [roomId, frameBuffer] of this.frameBuffers) {
      if (frameBuffer.inputs.size === 0) continue;

      // 检查最早提交的输入是否超时
      let earliestTime = Infinity;
      for (const input of frameBuffer.inputs.values()) {
        if (input.receivedAt < earliestTime) {
          earliestTime = input.receivedAt;
        }
      }

      if (now - earliestTime > FRAME_TIMEOUT) {
        // 超时，用上一帧的输入填充未提交的玩家
        const room = this.server.roomManager.getRoom(roomId);
        if (!room) continue;

        for (const [connId, player] of room.players) {
          if (player.disconnected) continue;
          if (!frameBuffer.confirmed.get(connId)) {
            // 用空输入填充
            frameBuffer.confirmed.set(connId, true);
          }
        }
      }
    }
  }

  /**
   * 获取同步统计
   */
  getStats() {
    return {
      registeredObjects: this.syncRegistry.size,
      tick: this._tick,
      frameBuffers: this.frameBuffers.size,
    };
  }
}

module.exports = { SyncManager };
