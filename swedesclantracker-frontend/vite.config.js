import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'

// https://vitejs.dev/config/
export default defineConfig(({ mode }) => {
  const envDir = fileURLToPath(new URL(".", import.meta.url))
  const env = loadEnv(mode, envDir, "")
  const apiProxyTarget = env.VITE_API_PROXY_TARGET || "http://127.0.0.1:5166"

  return {
    plugins: [react()],
    server: {
      proxy: {
        "/api": {
          target: apiProxyTarget,
          changeOrigin: true,
        },
      },
    },
  }
})
