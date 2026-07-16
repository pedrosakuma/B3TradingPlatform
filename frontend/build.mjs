import { build } from "esbuild";
import { cp, mkdir, rm } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const dist = join(root, "dist");

await rm(dist, { recursive: true, force: true });
await mkdir(join(dist, "js"), { recursive: true });

await Promise.all([
  cp(join(root, "index.html"), join(dist, "index.html")),
  cp(join(root, "design-system.html"), join(dist, "design-system.html")),
  cp(join(root, "env.js.template"), join(dist, "env.js.template")),
  cp(join(root, "nginx.conf.template"), join(dist, "nginx.conf.template")),
  cp(join(root, "20-render-env-js.sh"), join(dist, "20-render-env-js.sh")),
  cp(join(root, "25-render-nginx-conf.sh"), join(dist, "25-render-nginx-conf.sh")),
  cp(join(root, "css"), join(dist, "css"), { recursive: true }),
  cp(join(root, "js", "env.js"), join(dist, "js", "env.js")),
]);

await build({
  entryPoints: [
    join(root, "js", "app.js"),
    join(root, "js", "worker.js"),
    join(root, "js", "mdWorker.js"),
  ],
  bundle: true,
  format: "esm",
  target: ["es2022"],
  outdir: join(dist, "js"),
  sourcemap: false,
  splitting: true,
  legalComments: "linked",
  logLevel: "info",
});
