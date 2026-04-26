/**
 * MiniLink 序列化抽象层
 * 
 * #1: 解决 Protobuf 协议定义与实际 JSON 序列化不一致的问题
 * 
 * 设计决策：
 * - Protobuf 协议（shared/proto/protocol.proto）作为消息规范定义
 * - 当前默认使用 JSON 序列化（便于调试、小程序环境兼容）
 * - 可通过配置切换为 Protobuf（生产环境减少带宽）
 * - 切换只需修改 Serializer 配置，无需改动业务代码
 * 
 * 为什么默认 JSON：
 * 1. 小程序环境调试方便（可直接在开发者工具中查看消息内容）
 * 2. 不需要预编译 .proto 文件
 * 3. 帧同步消息数据量小，JSON 开销可接受
 * 4. 开发阶段优先正确性，生产阶段再优化性能
 */

const { v4: uuidv4 } = require('uuid');

// 序列化模式
const SerializeMode = {
  JSON: 'json',
  PROTOBUF: 'protobuf',
};

class Serializer {
  constructor(options = {}) {
    this.mode = options.mode || SerializeMode.JSON;
    this._protobufRoot = null;
    this._protobufLoaded = false;
  }

  /**
   * 初始化 Protobuf（仅在 mode=protobuf 时调用）
   */
  async initProtobuf(protoPath) {
    if (this.mode !== SerializeMode.PROTOBUF) return;

    try {
      const protobuf = require('protobufjs');
      this._protobufRoot = await protobuf.load(protoPath);
      this._protobufLoaded = true;
      console.log('[Serializer] Protobuf 协议加载成功');
    } catch (err) {
      console.warn(`[Serializer] Protobuf 加载失败，回退到 JSON: ${err.message}`);
      this.mode = SerializeMode.JSON;
    }
  }

  /**
   * 序列化消息
   * @param {object} msg - 消息对象
   * @returns {string|Buffer} 序列化后的数据
   */
  serialize(msg) {
    switch (this.mode) {
      case SerializeMode.PROTOBUF:
        return this._serializeProtobuf(msg);
      case SerializeMode.JSON:
      default:
        return JSON.stringify(msg);
    }
  }

  /**
   * 反序列化消息
   * @param {string|Buffer} data - 原始数据
   * @returns {object} 消息对象
   */
  deserialize(data) {
    switch (this.mode) {
      case SerializeMode.PROTOBUF:
        return this._deserializeProtobuf(data);
      case SerializeMode.JSON:
      default:
        return JSON.parse(data);
    }
  }

  // ==================== Protobuf 序列化 ====================

  _serializeProtobuf(msg) {
    if (!this._protobufLoaded) {
      // 回退到 JSON
      return JSON.stringify(msg);
    }

    try {
      const msgType = this._protobufRoot.lookupType('MiniLink.NetMessage');
      const errMsg = msgType.verify(msg);
      if (errMsg) {
        console.warn(`[Serializer] Protobuf 验证失败: ${errMsg}, 回退到 JSON`);
        return JSON.stringify(msg);
      }

      const message = msgType.create(msg);
      return msgType.encode(message).finish();
    } catch (err) {
      console.warn(`[Serializer] Protobuf 编码失败: ${err.message}, 回退到 JSON`);
      return JSON.stringify(msg);
    }
  }

  _deserializeProtobuf(data) {
    if (!this._protobufLoaded) {
      return JSON.parse(typeof data === 'string' ? data : data.toString());
    }

    try {
      const msgType = this._protobufRoot.lookupType('MiniLink.NetMessage');
      const decoded = msgType.decode(data);
      return msgType.toObject(decoded, {
        longs: Number,
        bytes: String,
        defaults: true,
      });
    } catch (err) {
      // 尝试 JSON 回退
      try {
        return JSON.parse(typeof data === 'string' ? data : data.toString());
      } catch {
        console.warn(`[Serializer] 反序列化失败: ${err.message}`);
        return null;
      }
    }
  }

  /**
   * 获取当前模式信息
   */
  getInfo() {
    return {
      mode: this.mode,
      protobufLoaded: this._protobufLoaded,
    };
  }
}

module.exports = { Serializer, SerializeMode };
