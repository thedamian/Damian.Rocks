// pm2 start ecosystem.config.cjs   (run `npm run build` first)
module.exports = {
  apps: [
    {
      name: 'damian-rocks',
      script: 'dist/server.js',
      cwd: __dirname,
      instances: 1, // must stay 1: rate limits and connected sockets live in memory
      exec_mode: 'fork',
      max_memory_restart: '300M',
      env: {
        NODE_ENV: 'production',
        PORT: 3000,
        HOST: '127.0.0.1', // only Caddy talks to Node; use 0.0.0.0 if exposed directly
        TRUST_PROXY: 'loopback', // set to 'none' if there is no reverse proxy
        // RL_IP_BURST: 15, RL_IP_PER_SEC: 4, BAN_SECONDS: 300, ...see README
      },
    },
  ],
};
