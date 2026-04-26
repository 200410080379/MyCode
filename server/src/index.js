// MiniLink Server - 小程序多人联机轻量化框架服务端入口
const { MiniLinkServer } = require('./server');
const { SerializeMode } = require('./serializer');

const PORT = process.env.PORT || 9000;
const HOST = process.env.HOST || '0.0.0.0';

// #1: 序列化模式配置
// 开发环境用 JSON（方便调试），生产环境可设为 SERIALIZER=protobuf
const SERIALIZER = process.env.SERIALIZER || 'json';
const serializeMode = SERIALIZER === 'protobuf' ? SerializeMode.PROTOBUF : SerializeMode.JSON;

const server = new MiniLinkServer({
  port: PORT,
  host: HOST,
  heartbeatInterval: 15000,
  heartbeatTimeout: 30000,
  reconnectTTL: 30000,
  maxRooms: 100,
  maxPlayersPerRoom: 4,
  syncRate: 20,
  serializeMode, // #1: 序列化模式
  wx: {
    appId: process.env.WX_APP_ID || '',
    appSecret: process.env.WX_APP_SECRET || '',
  },
});

// #1: 如果配置了 Protobuf，初始化时加载协议文件
if (serializeMode === SerializeMode.PROTOBUF) {
  server.serializer.initProtobuf(require('path').join(__dirname, '../../shared/proto/protocol.proto'))
    .then(() => {
      console.log('[MiniLink] Protobuf 协议初始化完成');
    })
    .catch(err => {
      console.warn(`[MiniLink] Protobuf 初始化失败，使用 JSON: ${err.message}`);
    });
}

server.start();

process.on('SIGINT', () => {
  console.log('[MiniLink] 正在关闭服务器...');
  server.stop();
  process.exit(0);
});

process.on('uncaughtException', (err) => {
  console.error('[MiniLink] 未捕获异常:', err);
});

process.on('unhandledRejection', (reason) => {
  console.error('[MiniLink] 未处理的Promise拒绝:', reason);
});
