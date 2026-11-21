import { defineConfig } from "vite-plus";
import react from "@vitejs/plugin-react";
import fs from "fs";
import os from "os";
import { resolve, join } from "path";
import { lazyPlugins } from "vite-plus";

const mochiAppDataDir = join(
  process.env.LOCALAPPDATA || join(os.homedir(), ".local", "share"),
  "DesktopMochi",
);
const mochiAppearancePath = join(mochiAppDataDir, "appearance.json");

function mochiAppearanceSyncPlugin() {
  return {
    name: "mochi-appearance-sync",
    configureServer(server: { middlewares: { use: (path: string, fn: Function) => void } }) {
      server.middlewares.use("/api/mochi-appearance", (req: any, res: any) => {
        if (req.method === "GET") {
          const body = fs.existsSync(mochiAppearancePath)
            ? fs.readFileSync(mochiAppearancePath, "utf8")
            : "{}";
          res.statusCode = 200;
          res.setHeader("Content-Type", "application/json; charset=utf-8");
          res.setHeader("Cache-Control", "no-store");
          res.end(body);
          return;
        }
        if (req.method === "POST") {
          let raw = "";
          req.on("data", (chunk: Buffer) => {
            raw += chunk.toString();
            if (raw.length > 16384) req.destroy();
          });
          req.on("end", () => {
            try {
              const parsed = JSON.parse(raw);
              fs.mkdirSync(mochiAppDataDir, { recursive: true });
              const tmp = `${mochiAppearancePath}.tmp`;
              fs.writeFileSync(tmp, JSON.stringify(parsed, null, 2), "utf8");
              fs.renameSync(tmp, mochiAppearancePath);
              res.statusCode = 200;
              res.setHeader("Content-Type", "application/json; charset=utf-8");
              res.end(JSON.stringify({ ok: true }));
            } catch (err) {
              res.statusCode = 400;
              res.setHeader("Content-Type", "application/json; charset=utf-8");
              res.end(JSON.stringify({ ok: false, error: String(err) }));
            }
          });
          return;
        }
        res.statusCode = 405;
        res.end("Method Not Allowed");
      });
    },
  };
}

export default defineConfig({
  staged: {
    "*": "vp check --fix",
  },
  fmt: {},
  lint: {
    jsPlugins: [{ name: "vite-plus", specifier: "vite-plus/oxlint-plugin" }],
    rules: { "vite-plus/prefer-vite-plus-imports": "error" },
    options: { typeAware: true, typeCheck: true },
  },
  plugins: lazyPlugins(() => [react(), mochiAppearanceSyncPlugin()]),
  server: {
    port: 3000,
    host: "0.0.0.0",
  },
  build: {
    rollupOptions: {
      input: {
        main: resolve(__dirname, "index.html"),
        grass: resolve(__dirname, "grass.html"),
      },
    },
  },
});
