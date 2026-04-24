// MiniLink Server - 小程序多人联机轻量化框架服务端入口
const { MiniLinkServer } = require('./server');

const PORT = process.env.PORT || 9000;
const HOST = process.env.HOST || '0.0.0.0';

const server = new MiniLinkServer({
  port: PORT,
  host: HOST,
  heartbeatInterval: 15000,
  heartbeatTimeout: 30000,
  reconnectTTL: 30000,
  maxRooms: 100,
  maxPlayersPerRoom: 4,
  syncRate: 20,
  wx: {
    appId: process.env.WX_APP_ID || '',
    appSecret: process.env.WX_APP_SECRET || '',
  },
});

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
