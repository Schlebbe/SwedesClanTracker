import { readdir, readFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const dist = fileURLToPath(new URL("../dist/assets/", import.meta.url));
const marker = "operator-console-gate3-fixture";
let files;
try {
  files = await readdir(dist);
} catch {
  console.error("Production bundle check requires a completed Vite build.");
  process.exit(1);
}

for (const file of files) {
  const contents = await readFile(join(dist, file), "utf8");
  if (contents.includes(marker) || contents.includes("VITE_API_PROXY_TARGET")) {
    console.error(`Forbidden development-only content found in dist/assets/${file}.`);
    process.exit(1);
  }
}

console.log("Production bundle contains no development fixture marker or private proxy target.");
