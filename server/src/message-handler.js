/**
 * MiniLink MessageHandler - 消息路由与处理
 * 参考 Mirror 的 NetworkMessage 消息分发体系
 */
class MessageHandler {
  constructor(server) {
    this.server = server;

    // #2/#4: Command 处理器注册表
    // key: methodHash (uint), value: async function(conn, cmd) => void
    this._commandHandlers = new Map();

    // 消息类型→处理函数映射表
    this.handlers = {
      1:  this._onHandshakeReq.bind(this),
      3:  this._onHeartbeat.bind(this),
      10: this._onRoomCreate.bind(this),
      11: this._onRoomJoin.bind(this),
      12: this._onRoomLeave.bind(this),
      14: this._onRoomList.bind(this),
      15: this._onRoomReady.bind(this),
      16: this._onRoomStart.bind(this),
      18: this._onRoomMatch.bind(this),
      20: this._onSyncVar.bind(this),
      21: this._onSyncList.bind(this),   // #23: 注册 SyncList 处理器
      22: this._onSnapshot.bind(this),
      23: this._onInputFrame.bind(this),
      30: this._onCommand.bind(this),
      50: this._onWxLogin.bind(this),
      51: this._onWxShare.bind(this),
      // 帧同步消息
      70: this._onFramePause.bind(this),
      72: this._onFrameInput.bind(this),
      // 断线重连消息
      80: this._onReconnectState.bind(this),
      82: this._onReconnectRequest.bind(this),
      // 延迟补偿（时间同步）
      90: this._onTimeSync.bind(this),
    };
  }

  // ==================== #2/#4: Command 注册 API ====================

  /**
   * 注册 Command 处理器
   * @param {number} methodHash - 方法哈希值（对应客户端 [Command] 标记的方法）
   * @param {function} handler - 处理函数 async (conn, cmd) => void
   */
  registerCommandHandler(methodHash, handler) {
    if (typeof handler !== 'function') {
      throw new TypeError('[MessageHandler] Command handler 必须是函数');
    }
    this._commandHandlers.set(methodHash, handler);
    console.log(`[MessageHandler] 注册 Command handler: hash=${methodHash}`);
  }

  /**
   * 注销 Command 处理器
   */
  unregisterCommandHandler(methodHash) {
    this._commandHandlers.delete(methodHash);
  }

  /**
   * 清空所有 Command 处理器
   */
  clearCommandHandlers() {
    this._commandHandlers.clear();
  }

  // ==================== 消息分发 ====================

  /**
   * 处理收到的消息
   */
  handle(conn, rawData) {
    let msg;
    try {
      msg = this.server.serializer.deserialize(rawData);
    } catch (err) {
      console.warn(`[MessageHandler] 无效消息 from ${conn.connId}`);
      return;
    }
    if (!msg) return;

    // 处理批量消息
    if (Array.isArray(msg)) {
      for (const m of msg) {
        this._dispatch(conn, m);
      }
    } else {
      this._dispatch(conn, msg);
    }
  }

  /**
   * 分发单条消息
   */
  _dispatch(conn, msg) {
    const msgType = msg.type;
    if (!msgType) return;

    const handler = this.handlers[msgType];
    if (handler) {
      handler(conn, msg);
    } else {
      console.warn(`[MessageHandler] 未知消息类型: ${msgType}`);
    }
  }

  // ==================== 连接/认证 ====================

  async _onHandshakeReq(conn, msg) {
    const req = msg.handshake_req;
    if (!req) return;

    conn.isAuthenticated = true;
    conn.playerId = req.client_id || conn.connId;

    conn.send({
      type: 2, // MSG_TYPE_HANDSHAKE_RESP
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      handshake_resp: {
        success: true,
        conn_id: conn.connId,
        player_id: conn.playerId,
        session_id: conn.sessionId,
        reconnect_token: conn.sessionId,
        server_time: new Date().toISOString(),
        reconnect_ttl: this.server.reconnectTTL / 1000,
      },
    });
  }

  _onHeartbeat(conn, msg) {
    const hb = msg.heartbeat;
    const clientTime = hb ? hb.client_time : Date.now();
    const pingMs = hb ? hb.ping_ms : 0;

    conn.updateHeartbeat(pingMs);

    // 回复心跳
    conn.send({
      type: 3,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      heartbeat: {
        client_time: clientTime,
        ping_ms: Date.now() - clientTime,
      },
    });
  }

  // ==================== 房间管理 ====================

  _onRoomCreate(conn, msg) {
    const req = msg.room_create_req;
    const result = this.server.roomManager.createRoom(conn.connId, req);

    if (result.error) {
      conn.send(this._makeError(result.error, result.message));
      return;
    }

    // 通知创建者
    conn.send({
      type: 17, // MSG_TYPE_ROOM_STATE
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });
  }

  _onRoomJoin(conn, msg) {
    const req = msg.room_join_req;
    const result = this.server.roomManager.joinRoom(
      conn.connId, req.room_id, req.password
    );

    if (result.error) {
      conn.send(this._makeError(result.error, result.message));
      return;
    }

    // 通知房间内所有人
    this.server.broadcastToRoom(result.room_id, {
      type: 17,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });
  }

  _onRoomLeave(conn, msg) {
    const result = this.server.roomManager.leaveRoom(conn.connId);

    if (result.error) {
      conn.send(this._makeError(result.error, result.message));
      return;
    }

    // 通知房间内其他人
    if (!result.dismissed && result.room) {
      this.server.broadcastToRoom(result.room_id, {
        type: 17,
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        room_state_notify: result.room,
      });
    }
  }

  _onRoomList(conn, msg) {
    const req = msg.room_list_req || {};
    const result = this.server.roomManager.getRoomList(
      req.page || 1,
      req.page_size || 20
    );

    conn.send({
      type: 14,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_list_resp: result,
    });
  }

  _onRoomReady(conn, msg) {
    const req = msg.room_ready_req;
    const result = this.server.roomManager.setReady(
      conn.connId, req.ready
    );

    if (result.error) {
      conn.send(this._makeError(result.error));
      return;
    }

    // 广播房间状态更新
    this.server.broadcastToRoom(result.room_id, {
      type: 17,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });
  }

  _onRoomStart(conn, msg) {
    const result = this.server.roomManager.startGame(conn.connId);

    if (result.error) {
      conn.send(this._makeError(result.error, result.message));
      return;
    }

    // 广播游戏开始
    this.server.broadcastToRoom(result.room_id, {
      type: 17,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });

    // 通知客户端生成游戏对象
    const room = this.server.roomManager.getRoom(result.room_id);
    if (room) {
      this.server.frameSyncManager.initRoom(result.room_id, room.players.size);

      for (const [playerConnId] of room.players) {
        // #13: 使用 nextNetId() 而非 nextSeq()
        const netId = this.server.nextNetId();
        const playerConn = this.server.connections.get(playerConnId);

        this.server.spawned.set(netId, {
          ownerConnId: playerConnId,
          prefabHash: 0,
          state: null,
        });

        if (playerConn) {
          playerConn.playerNetId = netId;
          playerConn.ownedNetIds.add(netId);
        }

        this.server.sendTo(playerConnId, {
          type: 40, // MSG_TYPE_SPAWN
          seq: this.server.nextSeq(),
          timestamp: Date.now(),
          spawn_msg: {
            net_id: netId,
            prefab_hash: 0,
            owner_conn_id: playerConnId,
            initial_state: null,
          },
        });
      }
    }
  }

  _onRoomMatch(conn, msg) {
    const req = msg.room_match_req;
    const result = this.server.roomManager.match(conn.connId, req);

    if (result.error) {
      conn.send(this._makeError(result.error));
      return;
    }

    // 通知匹配结果
    this.server.broadcastToRoom(result.room_id, {
      type: 17,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });
  }

  // ==================== 同步 ====================

  _onSyncVar(conn, msg) {
    this.server.syncManager.handleSyncVarUpdate(
      conn.connId, msg.sync_var_msg
    );
  }

  /**
   * #23: SyncList 消息处理
   */
  _onSyncList(conn, msg) {
    const listMsg = msg.sync_list_msg;
    if (!listMsg) return;

    const { net_id, operation, items } = listMsg;
    if (!net_id) return;

    // 广播 SyncList 变更给房间内其他玩家
    const roomId = this.server.roomManager.playerRoomMap.get(conn.connId);
    if (!roomId) return;

    this.server.broadcastToRoom(roomId, {
      type: 21,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      sync_list_msg: {
        net_id,
        operation,
        items,
        source_conn_id: conn.connId,
      },
    }, conn.connId); // 排除发送者
  }

  _onSnapshot(conn, msg) {
    this.server.syncManager.receiveSnapshot(
      conn.connId, msg.snapshot_msg
    );
  }

  _onInputFrame(conn, msg) {
    this.server.syncManager.receiveInputFrame(
      conn.connId, msg.input_frame_msg
    );
  }

  // ==================== RPC / Command ====================

  /**
   * #2/#4: Command 处理器 - 完整实现
   * 
   * Command 是客户端调用、服务端执行的方法。
   * 服务端收到 Command 后：
   * 1. 验证权限（客户端只能发送自己拥有权限的对象的 Command）
   * 2. 查找注册的 Command 处理器
   * 3. 执行处理器（处理器内部可调用 ClientRpc 广播结果）
   * 4. 同时通过 EventEmitter 通知上层
   */
  _onCommand(conn, msg) {
    const cmd = msg.command_msg;
    if (!cmd) return;

    // 验证权限
    if (cmd.requires_authority) {
      const netId = cmd.net_id;
      if (!netId) {
        conn.send(this._makeError('AUTH_FAILED', 'Command 缺少 net_id'));
        return;
      }
      const spawnedObj = this.server.spawned.get(netId);
      if (!spawnedObj || spawnedObj.ownerConnId !== conn.connId) {
        conn.send(this._makeError('AUTH_FAILED', '无权执行此 Command'));
        return;
      }
    }

    const methodHash = cmd.method_hash;

    // 查找注册的处理器
    const handler = this._commandHandlers.get(methodHash);
    if (handler) {
      try {
        // 执行注册的处理器
        const result = handler(conn, cmd);
        // 支持 async handler
        if (result && typeof result.catch === 'function') {
          result.catch(err => {
            console.error(`[MessageHandler] Command handler 异常: hash=${methodHash}`, err);
          });
        }
      } catch (err) {
        console.error(`[MessageHandler] Command handler 执行失败: hash=${methodHash}`, err);
      }
    }

    // #4: 通过 EventEmitter 通知上层（即使有注册 handler 也会触发）
    this.server.emit('command', { conn, cmd });
  }

  // ==================== 微信 ====================

  async _onWxLogin(conn, msg) {
    const req = msg.wx_login_req;
    if (!req || !req.code) {
      conn.send(this._makeError('AUTH_FAILED', '缺少code'));
      return;
    }

    try {
      const wxResult = await this.server.wechatAdapter.login(req.code);
      conn.isAuthenticated = true;
      conn.playerId = wxResult.openid;
      conn.nickname = req.nickname || '玩家';
      conn.avatarUrl = req.avatar_url || '';

      conn.send({
        type: 61, // MSG_TYPE_WX_LOGIN_RESP
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        wx_login_resp: {
          success: true,
          token: wxResult.token,
          openid: wxResult.openid,
          session_key: wxResult.session_key,
        },
      });
    } catch (err) {
      conn.send({
        type: 61,
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        wx_login_resp: {
          success: false,
          token: '',
          openid: '',
          session_key: '',
        },
      });
    }
  }

  _onWxShare(conn, msg) {
    const req = msg.wx_share_req;
    console.log(`[MessageHandler] 分享: room=${req?.room_id} by ${conn.connId}`);
  }

  // ==================== 帧同步 ====================

  _onFramePause(conn, msg) {
    const req = msg.frame_pause_msg;
    if (!req) return;
    const room = conn.roomId ? this.server.roomManager.getRoom(conn.roomId) : null;
    if (!room) return;
    const player = room.players.get(conn.connId);
    if (!player || !player.is_host) return;
    this.server.frameSyncManager.setPaused(conn.roomId, req.paused);
  }

  /**
   * #12: 帧同步输入 - 统一消息格式
   * 客户端发送: { type: 72, frame, input_data, input_hash }
   * 服务端接收后传递给 FrameSyncManager
   */
  _onFrameInput(conn, msg) {
    const connRoomId = conn.roomId;
    if (!connRoomId) return;

    // #12: 统一从消息顶层提取帧输入字段
    // 客户端直接发送 frame/input_data/input_hash 在消息顶层
    const frameInput = {
      frame: msg.frame,
      input_data: msg.input_data,
      input_hash: msg.input_hash,
    };

    this.server.frameSyncManager.receiveInput(conn.connId, connRoomId, frameInput);
  }

  // ==================== 断线重连 ====================

  _onReconnectState(conn, msg) {
    const state = this.server.reconnectionManager.isWaitingReconnect(conn.connId);
    conn.send({
      type: 80,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      player_reconnect_msg: {
        conn_id: conn.connId,
        state: state ? 'waiting' : 'none',
      },
    });
  }

  _onReconnectRequest(conn, msg) {
    const req = msg.reconnect_req;
    if (!req) return;

    // 通过 playerId 查找旧连接
    const oldState = this.server.reconnectionManager.findByPlayerId(req.player_id);
    if (!oldState) {
      conn.send({
        type: 81,
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        reconnect_result_msg: {
          success: false,
          reason: 'no_saved_state',
        },
      });
      return;
    }

    const result = this.server.reconnectionManager.tryReconnect(
      oldState.connId, conn.connId, req.room_id
    );

    if (!result.success) {
      conn.send({
        type: 81,
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        reconnect_result_msg: {
          success: false,
          reason: result.reason,
        },
      });
    }
    // 成功的情况在 tryReconnect 中已发送
  }

  // ==================== 延迟补偿（时间同步）====================

  _onTimeSync(conn, msg) {
    const req = msg.time_sync_req;
    if (!req) return;

    const serverReceiveTime = Date.now();

    conn.send({
      type: 91, // MSG_TYPE_TIME_SYNC_RESP
      seq: this.server.nextSeq(),
      timestamp: serverReceiveTime,
      time_sync_resp: {
        client_send_time: req.client_send_time,
        server_receive_time: serverReceiveTime,
        server_send_time: Date.now(),
      },
    });
  }

  // ==================== 工具方法 ====================

  _makeError(code, message = '') {
    return {
      type: 99,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      error_msg: { code, message },
    };
  }
}

module.exports = { MessageHandler };
