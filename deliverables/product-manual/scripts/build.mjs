import { build } from "esbuild";
import { mkdir, copyFile, cp } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const dist = path.join(root, "dist");
await mkdir(dist, { recursive: true });
await build({
  entryPoints: [path.join(root, "src/App.jsx")],
  bundle: true,
  format: "iife",
  target: "es2022",
  outfile: path.join(dist, "manual.js"),
  jsx: "automatic",
  external: ["./assets/*"],
  minify: false,
  define: { "process.env.NODE_ENV": '"production"' },
  logLevel: "info",
});
await copyFile(path.join(root, "index.html"), path.join(dist, "index.html"));
await cp(path.join(root, "public/assets"), path.join(dist, "assets"), {
  recursive: true,
});
console.log(`Built React manual at ${dist}`);
