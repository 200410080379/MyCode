/**
 * MiniLink WechatAdapter - 微信小程序适配
 * 处理微信登录、分享等接口
 */
const axios = require('axios');
const crypto = require('crypto');
const { v4: uuidv4 } = require('uuid');

class WechatAdapter {
  constructor(config = {}) {
    this.appId = config.appId || '';
    this.appSecret = config.appSecret || '';

    // session_key 缓存（openid -> sessionKey）
    this._sessionCache = new Map();

    // token 缓存
    this._tokenCache = new Map();
  }

  /**
   * 微信登录
   * 调用微信 code2Session 接口
   */
  async login(code) {
    if (!this.appId || !this.appSecret) {
      // 开发模式：返回模拟数据
      return this._mockLogin(code);
    }

    try {
      const url = 'https://api.weixin.qq.com/sns/jscode2session';
      const resp = await axios.get(url, {
        params: {
          appid: this.appId,
          secret: this.appSecret,
          js_code: code,
          grant_type: 'authorization_code',
        },
        timeout: 5000,
      });

      const data = resp.data;
      if (data.errcode) {
        throw new Error(`微信登录失败: ${data.errcode} ${data.errmsg}`);
      }

      // 缓存 session_key
      this._sessionCache.set(data.openid, data.session_key);

      // 生成自有 token
      const token = this._generateToken(data.openid, data.session_key);

      return {
        openid: data.openid,
        session_key: data.session_key,
        unionid: data.unionid || '',
        token,
      };
    } catch (err) {
      console.error('[WechatAdapter] 登录失败:', err.message);
      throw err;
    }
  }

  /**
   * 解密微信加密数据（如用户信息）
   */
  decryptData(encryptedData, iv, sessionKey) {
    try {
      const key = Buffer.from(sessionKey, 'base64');
      const ivBuf = Buffer.from(iv, 'base64');
      const encryptedBuf = Buffer.from(encryptedData, 'base64');

      const decipher = crypto.createDecipheriv('aes-128-cbc', key, ivBuf);
      decipher.setAutoPadding(true);

      let decoded = decipher.update(encryptedBuf, null, 'utf8');
      decoded += decipher.final('utf8');

      return JSON.parse(decoded);
    } catch (err) {
      console.error('[WechatAdapter] 解密失败:', err.message);
      return null;
    }
  }

  /**
   * 验证 token 有效性
   */
  verifyToken(token) {
    try {
      const parts = token.split('.');
      if (parts.length !== 3) return null;

      const payload = JSON.parse(
        Buffer.from(parts[1], 'base64url').toString()
      );

      // 检查过期
      if (payload.exp && payload.exp < Date.now() / 1000) {
        return null;
      }

      return payload;
    } catch {
      return null;
    }
  }

  /**
   * 获取微信 access_token（用于服务端调用微信接口）
   */
  async getAccessToken() {
    // 检查缓存
    const cached = this._tokenCache.get('access_token');
    if (cached && cached.expiresAt > Date.now()) {
      return cached.token;
    }

    if (!this.appId || !this.appSecret) return '';

    try {
      const url = 'https://api.weixin.qq.com/cgi-bin/token';
      const resp = await axios.get(url, {
        params: {
          grant_type: 'client_credential',
          appid: this.appId,
          secret: this.appSecret,
        },
        timeout: 5000,
      });

      const data = resp.data;
      if (data.errcode) {
        throw new Error(`获取token失败: ${data.errcode}`);
      }

      // 缓存（提前5分钟过期）
      this._tokenCache.set('access_token', {
        token: data.access_token,
        expiresAt: Date.now() + (data.expires_in - 300) * 1000,
      });

      return data.access_token;
    } catch (err) {
      console.error('[WechatAdapter] 获取access_token失败:', err.message);
      return '';
    }
  }

  /**
   * 生成分享链接参数
   */
  generateShareParams(roomId, extra = {}) {
    return {
      title: extra.title || '来一起玩吧！',
      imageUrl: extra.imageUrl || '',
      path: `/pages/index/index?room=${roomId}`,
      room_id: roomId,
    };
  }

  // ==================== 内部方法 ====================

  /**
   * 生成自有 token（简化JWT）
   */
  _generateToken(openid, sessionKey) {
    const header = Buffer.from(JSON.stringify({ alg: 'HS256', typ: 'JWT' }))
      .toString('base64url');
    const payload = Buffer.from(JSON.stringify({
      openid,
      iat: Math.floor(Date.now() / 1000),
      exp: Math.floor(Date.now() / 1000) + 86400, // 24小时
    })).toString('base64url');

    const secret = sessionKey || 'minilink-dev-secret';
    const signature = crypto
      .createHmac('sha256', secret)
      .update(`${header}.${payload}`)
      .digest('base64url');

    return `${header}.${payload}.${signature}`;
  }

  /**
   * 开发模式模拟登录
   */
  // #21: 使用 uuid 替代 Date.now() 避免多客户端快速连接时 openid 冲突
  _mockLogin(code) {
    const mockOpenid = `dev_${uuidv4().split('-')[0]}`;
    const mockSessionKey = crypto.randomBytes(16).toString('base64');

    this._sessionCache.set(mockOpenid, mockSessionKey);

    return {
      openid: mockOpenid,
      session_key: mockSessionKey,
      unionid: '',
      token: this._generateToken(mockOpenid, mockSessionKey),
    };
  }
}

module.exports = { WechatAdapter };
