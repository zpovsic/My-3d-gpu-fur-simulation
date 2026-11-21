const http = require("http");
const fs = require("fs");
const os = require("os");
const path = require("path");

const PORT = Number(process.env.PORT) || 3000;
const HOST = "0.0.0.0";
const DIST_DIR = path.join(__dirname, "dist");
const ROOT_DIR = fs.existsSync(DIST_DIR) ? DIST_DIR : __dirname;
const MOCHI_APP_DATA = path.join(
  process.env.LOCALAPPDATA || path.join(os.homedir(), ".local", "share"),
  "DesktopMochi",
);
const APPEARANCE_PATH = path.join(MOCHI_APP_DATA, "appearance.json");

function sanitizeAppearance(raw) {
  if (!raw || typeof raw !== "object") return null;
  const out = {};
  const hexRe = /^#[0-9a-fA-F]{6}$/;
  const numBounds = {
    furLength: [0.05, 0.85],
    furDensity: [0.1, 1.0],
    clumping: [0.0, 1.0],
    curliness: [0.0, 1.0],
    roughness: [0.05, 0.95],
    specular: [0.0, 1.0],
    lightAngle: [-180, 180],
    lightIntensity: [0.1, 3.0],
    rimIntensity: [0.0, 3.0],
    windStrength: [0.0, 3.0],
  };
  for (const key of ["baseColor", "tipColor", "keyColor"]) {
    if (typeof raw[key] === "string" && hexRe.test(raw[key].trim())) {
      out[key] = raw[key].trim();
    }
  }
  for (const [key, [min, max]] of Object.entries(numBounds)) {
    const val = Number(raw[key]);
    if (Number.isFinite(val)) {
      out[key] = Math.max(min, Math.min(max, val));
    }
  }
  if (typeof raw.gpuFurEnabled === "boolean") {
    out.gpuFurEnabled = raw.gpuFurEnabled;
  }
  if (typeof raw.name === "string" && raw.name.trim().length > 0 && raw.name.length <= 48) {
    out.name = raw.name.trim();
  }
  if (
    typeof raw.environment === "string" &&
    ["studio", "meadow", "highrise"].includes(raw.environment)
  ) {
    out.environment = raw.environment;
  }
  return out;
}

const MIME_TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "application/javascript; charset=utf-8",
  ".mjs": "application/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".gif": "image/gif",
  ".webp": "image/webp",
  ".ico": "image/x-icon",
  ".woff": "font/woff",
  ".woff2": "font/woff2",
  ".ttf": "font/ttf",
  ".wasm": "application/wasm",
  ".glb": "model/gltf-binary",
  ".gltf": "model/gltf+json",
  ".txt": "text/plain; charset=utf-8",
};

const server = http.createServer((req, res) => {
  try {
    const parsedUrl = new URL(req.url || "/", `http://${req.headers.host || "localhost"}`);
    let pathname = decodeURIComponent(parsedUrl.pathname);

    if (pathname === "/healthz" || pathname === "/_ah/health") {
      res.writeHead(200, { "Content-Type": "text/plain; charset=utf-8" });
      res.end("OK");
      return;
    }

    if (pathname === "/api/mochi-appearance") {
      if (req.method === "GET") {
        let body = "{}";
        if (fs.existsSync(APPEARANCE_PATH)) {
          body = fs.readFileSync(APPEARANCE_PATH, "utf8");
        }
        res.writeHead(200, {
          "Content-Type": "application/json; charset=utf-8",
          "Cache-Control": "no-store",
        });
        res.end(body);
        return;
      }
      if (req.method === "POST") {
        let rawBody = "";
        req.on("data", (chunk) => {
          rawBody += chunk;
          if (rawBody.length > 16384) {
            req.destroy();
          }
        });
        req.on("end", () => {
          try {
            const sanitized = sanitizeAppearance(JSON.parse(rawBody));
            if (!sanitized) {
              res.writeHead(400, { "Content-Type": "application/json; charset=utf-8" });
              res.end(JSON.stringify({ ok: false, error: "Invalid payload" }));
              return;
            }
            fs.mkdirSync(MOCHI_APP_DATA, { recursive: true });
            const tmpPath = `${APPEARANCE_PATH}.tmp`;
            fs.writeFileSync(tmpPath, JSON.stringify(sanitized, null, 2), "utf8");
            fs.renameSync(tmpPath, APPEARANCE_PATH);
            res.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
            res.end(JSON.stringify({ ok: true }));
          } catch (err) {
            res.writeHead(400, { "Content-Type": "application/json; charset=utf-8" });
            res.end(JSON.stringify({ ok: false, error: String(err) }));
          }
        });
        return;
      }
      res.writeHead(405, { "Content-Type": "text/plain; charset=utf-8" });
      res.end("Method Not Allowed");
      return;
    }

    if (pathname === "/") {
      pathname = "/index.html";
    }

    // Prevent directory traversal
    const safePath = path.normalize(pathname).replace(/^(\.\.[/\\])+/, "");
    let filePath = path.join(ROOT_DIR, safePath);

    // Ensure the resolved path stays inside ROOT_DIR
    if (!filePath.startsWith(ROOT_DIR)) {
      res.writeHead(403, { "Content-Type": "text/plain; charset=utf-8" });
      res.end("Forbidden");
      return;
    }

    if (fs.existsSync(filePath) && fs.statSync(filePath).isDirectory()) {
      filePath = path.join(filePath, "index.html");
    }

    if (!fs.existsSync(filePath)) {
      // Fallback to index.html for SPA navigation
      const fallbackPath = path.join(ROOT_DIR, "index.html");
      if (fs.existsSync(fallbackPath)) {
        filePath = fallbackPath;
      } else {
        res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
        res.end("Not Found");
        return;
      }
    }

    const ext = path.extname(filePath).toLowerCase();
    const contentType = MIME_TYPES[ext] || "application/octet-stream";

    res.writeHead(200, {
      "Content-Type": contentType,
      "Cache-Control": ext === ".html" ? "no-cache" : "public, max-age=31536000, immutable",
    });

    const stream = fs.createReadStream(filePath);
    stream.on("error", (err) => {
      console.error("Stream error:", err);
      if (!res.headersSent) {
        res.writeHead(500, { "Content-Type": "text/plain; charset=utf-8" });
      }
      res.end("Internal Server Error");
    });
    stream.pipe(res);
  } catch (err) {
    console.error("Request error:", err);
    if (!res.headersSent) {
      res.writeHead(500, { "Content-Type": "text/plain; charset=utf-8" });
    }
    res.end("Internal Server Error");
  }
});

server.listen(PORT, HOST, () => {
  console.log(`Server listening on http://${HOST}:${PORT} (serving ${ROOT_DIR})`);
});
