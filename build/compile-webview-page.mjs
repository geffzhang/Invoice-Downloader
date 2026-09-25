import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourcePath = path.join(root, "templates", "index_app.js");
const outputPath = path.join(root, "templates", "index_app.compiled.js");
const babel = require(path.join(root, "templates", "static", "babel.min.js"));
const source = fs.readFileSync(sourcePath, "utf8");
const compiled = `${babel.transform(source, { presets: ["react"], sourceType: "script", comments: false }).code}\n`;

if (process.argv.includes("--check")) {
    if (!fs.existsSync(outputPath) || fs.readFileSync(outputPath, "utf8") !== compiled) {
        console.error("WebView page is stale. Run: node build/compile-webview-page.mjs");
        process.exitCode = 1;
    }
} else {
    fs.writeFileSync(outputPath, compiled);
}