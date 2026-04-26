/**
 * MiniLink RoomManager - 房间管理
 * 参考 Mirror NetworkRoomManager 设计，适配小程序2-4人轻度联机场景
 */
const { v4: uuidv4 } = require('uuid');

class RoomManager {
  constructor(server) {
    this.server = server;
    this.rooms = new Map(); // roomId -> Room
    this.maxRooms = server.maxRooms;
    this.maxPlayersPerRoom = server.maxPlayersPerRoom;

    // 玩家→房间映射（快速查找）
    this.playerRoomMap = new Map(); // connId -> roomId

    // 匹配队列
    this._matchQueue = [];
    this._matchTimer = null;
  }

  /**
   * 创建房间
   */
  createRoom(connId, opts = {}) {
    if (this.playerRoomMap.has(connId)) {
      return { error: 'ALREADY_IN_ROOM', message: '已在房间中' };
    }

    if (this.rooms.size >= this.maxRooms) {
      return { error: 'ROOM_LIMIT', message: '服务器房间数已达上限' };
    }

    const roomId = this._generateRoomId();
    const maxPlayers = Math.min(opts.max_players || 4, this.maxPlayersPerRoom);

    const room = {
      id: roomId,
      name: opts.room_name || `房间${roomId}`,
      password: opts.password || '',
      hostId: connId,
      state: 'WAITING',
      maxPlayers,
      players: new Map(), // connId -> PlayerInfo
      settings: opts.settings || {},
      createdAt: Date.now(),
      gameStartedAt: null,
    };

    this.rooms.set(roomId, room);
    this._addPlayerToRoom(room, connId, true);

    return { room_id: roomId, room: this._roomToNotify(room) };
  }

  /**
   * 加入房间
   */
  joinRoom(connId, roomId, password = '') {
    if (this.playerRoomMap.has(connId)) {
      return { error: 'ALREADY_IN_ROOM', message: '已在房间中' };
    }

    const room = this.rooms.get(roomId);
    if (!room) {
      return { error: 'ROOM_NOT_FOUND', message: '房间不存在' };
    }

    if (room.password && room.password !== password) {
      return { error: 'PASSWORD_WRONG', message: '房间密码错误' };
    }

    if (room.players.size >= room.maxPlayers) {
      return { error: 'ROOM_FULL', message: '房间已满' };
    }

    if (room.state === 'PLAYING') {
      return { error: 'GAME_STARTED', message: '游戏已开始' };
    }

    this._addPlayerToRoom(room, connId, false);

    return { room_id: roomId, room: this._roomToNotify(room) };
  }

  /**
   * 离开房间
   * #27: 调整调用顺序 — 先通知帧同步管理器，再从房间移除玩家
   */
  leaveRoom(connId) {
    const roomId = this.playerRoomMap.get(connId);
    if (!roomId) {
      return { error: 'NOT_IN_ROOM', message: '不在房间中' };
    }

    const room = this.rooms.get(roomId);
    if (!room) {
      this.playerRoomMap.delete(connId);
      return { error: 'ROOM_NOT_FOUND', message: '房间不存在' };
    }

    // #27: 先通知帧同步管理器（此时玩家还在 room.players 中，状态一致）
    if (room.state === 'PLAYING' && this.server.frameSyncManager) {
      this.server.frameSyncManager.playerLeave(roomId, connId);
    }

    // 再从房间移除玩家
    this._removePlayerFromRoom(room, connId);

    // 房间空了则解散
    if (room.players.size === 0) {
      this.rooms.delete(roomId);
      if (this.server.frameSyncManager) {
        this.server.frameSyncManager.removeRoom(roomId);
      }
      return { room_id: roomId, dismissed: true };
    }

    // 如果房主离开，转移房主
    if (room.hostId === connId) {
      const newHost = room.players.keys().next().value;
      room.hostId = newHost;
      const newHostPlayer = room.players.get(newHost);
      if (newHostPlayer) newHostPlayer.is_host = true;
    }

    return { room_id: roomId, room: this._roomToNotify(room) };
  }

  /**
   * 设置准备状态
   */
  setReady(connId, ready) {
    const roomId = this.playerRoomMap.get(connId);
    if (!roomId) return { error: 'NOT_IN_ROOM' };

    const room = this.rooms.get(roomId);
    const player = room.players.get(connId);
    if (!player) return { error: 'NOT_IN_ROOM' };

    player.is_ready = ready;

    const allReady = this._checkAllReady(room);
    if (room.state !== 'PLAYING') {
      room.state = allReady ? 'READY' : 'WAITING';
    }

    return { room_id: roomId, room: this._roomToNotify(room), all_ready: allReady };
  }

  /**
   * 开始游戏（仅房主）
   */
  startGame(connId) {
    const roomId = this.playerRoomMap.get(connId);
    if (!roomId) return { error: 'NOT_IN_ROOM' };

    const room = this.rooms.get(roomId);
    if (room.hostId !== connId) {
      return { error: 'NOT_HOST', message: '仅房主可以开始游戏' };
    }

    if (!this._checkAllReady(room)) {
      return { error: 'NOT_READY', message: '还有玩家未准备' };
    }

    if (room.players.size < 2) {
      return { error: 'NOT_ENOUGH', message: '至少需要2名玩家' };
    }

    room.state = 'PLAYING';
    room.gameStartedAt = Date.now();

    return { room_id: roomId, room: this._roomToNotify(room) };
  }

  /**
   * 匹配（快速加入）
   */
  match(connId, opts = {}) {
    if (this.playerRoomMap.has(connId)) {
      return { error: 'ALREADY_IN_ROOM' };
    }

    const maxPlayers = opts.max_players || 4;

    for (const [roomId, room] of this.rooms) {
      if (room.state !== 'WAITING') continue;
      if (room.password) continue;
      if (room.players.size >= room.maxPlayers) continue;
      if (room.maxPlayers !== maxPlayers) continue;

      this._addPlayerToRoom(room, connId, false);
      return { room_id: roomId, room: this._roomToNotify(room) };
    }

    return this.createRoom(connId, {
      room_name: '匹配房间',
      max_players: maxPlayers,
    });
  }

  /**
   * 获取房间列表
   */
  getRoomList(page = 1, pageSize = 20) {
    const allRooms = [];
    for (const [roomId, room] of this.rooms) {
      if (room.state === 'WAITING' && !room.password) {
        allRooms.push(this._roomToNotify(room));
      }
    }

    const total = allRooms.length;
    const start = (page - 1) * pageSize;
    const list = allRooms.slice(start, start + pageSize);

    return { rooms: list, total };
  }

  /**
   * 玩家断线处理
   */
  onPlayerDisconnect(connId) {
    const roomId = this.playerRoomMap.get(connId);
    if (!roomId) return;

    const room = this.rooms.get(roomId);
    if (!room) return;

    const player = room.players.get(connId);
    if (player) {
      player.disconnected = true;
      player.disconnectedAt = Date.now();
    }
  }

  /**
   * 玩家彻底移除（重连超时）
   */
  onPlayerRemove(connId) {
    this.leaveRoom(connId);
  }

  getRoom(roomId) {
    return this.rooms.get(roomId) || null;
  }

  getPlayerRoom(connId) {
    const roomId = this.playerRoomMap.get(connId);
    return roomId ? this.rooms.get(roomId) : null;
  }

  getRoomCount() {
    return this.rooms.size;
  }

  // ==================== 内部方法 ====================

  _addPlayerToRoom(room, connId, isHost) {
    const conn = this.server.connections.get(connId);
    const playerInfo = {
      player_id: conn ? (conn.playerId || connId) : connId,
      conn_id: connId,
      nickname: conn ? conn.nickname : '玩家',
      avatar_url: conn ? conn.avatarUrl : '',
      is_ready: false,
      is_host: isHost,
      slot_index: room.players.size,
      disconnected: false,
      disconnectedAt: null,
    };

    room.players.set(connId, playerInfo);
    this.playerRoomMap.set(connId, room.id);

    if (conn) conn.roomId = room.id;
  }

  _removePlayerFromRoom(room, connId) {
    room.players.delete(connId);
    this.playerRoomMap.delete(connId);

    const conn = this.server.connections.get(connId);
    if (conn) conn.roomId = null;
  }

  _checkAllReady(room) {
    for (const [connId, player] of room.players) {
      if (!player.is_ready && !player.disconnected) return false;
    }
    return room.players.size >= 2;
  }

  _generateRoomId() {
    let id;
    do {
      id = String(Math.floor(100000 + Math.random() * 900000));
    } while (this.rooms.has(id));
    return id;
  }

  /**
   * #19: 缓存房间通知数据，避免每次广播重复构建
   * 当房间状态未变化时，直接返回缓存
   */
  _roomToNotify(room) {
    // 如果有缓存且版本号一致，返回缓存
    if (room._notifyCache && room._notifyVersion === room._version) {
      return room._notifyCache;
    }

    const players = [];
    for (const [connId, p] of room.players) {
      players.push({
        player_id: p.player_id,
        nickname: p.nickname,
        avatar_url: p.avatar_url,
        is_ready: p.is_ready,
        is_host: p.is_host,
        slot_index: p.slot_index,
      });
    }

    const result = {
      room_id: room.id,
      room_name: room.name,
      state: room.state,
      max_players: room.maxPlayers,
      host_id: room.hostId,
      players,
      settings: room.settings,
    };

    // 缓存结果
    room._notifyCache = result;
    room._notifyVersion = room._version || 0;

    return result;
  }

  /**
   * 使房间通知缓存失效
   * 在任何修改房间状态的操作后调用
   */
  _invalidateRoomCache(room) {
    room._version = (room._version || 0) + 1;
  }
}

module.exports = { RoomManager };
