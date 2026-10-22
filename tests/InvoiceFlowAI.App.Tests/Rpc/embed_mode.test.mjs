import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../../../templates/embed_mode.js", import.meta.url), "utf8");

function runEmbedMode(search, bodyClasses) {
    const classes = new Set(bodyClasses || []);
    const context = {
        window: { location: { search } },
        document: { body: { classList: { add: (value) => classes.add(value) } } },
        // vm contexts only expose ECMAScript intrinsics; browsers also provide this global.
        URLSearchParams,
    };
    context.window.document = context.document;
    vm.createContext(context);
    vm.runInContext(source, context);
    return { window: context.window, classes };
}

test("embed mode maps view names to spa routes", () => {
    const { window } = runEmbedMode("?embed=1&view=processing", []);
    assert.equal(window.__invoiceFlowEmbed.enabled, true);
    assert.equal(window.__invoiceFlowEmbed.initialPath, "/processing");
});

test("embed mode hides the spa chrome", () => {
    const { classes } = runEmbedMode("?embed=1&view=settings", []);
    assert.equal(classes.has("is-embedded"), true);
});

test("non embed mode keeps the spa intact", () => {
    const { window, classes } = runEmbedMode("", []);
    assert.equal(window.__invoiceFlowEmbed.enabled, false);
    assert.equal(window.__invoiceFlowEmbed.initialPath, "/");
    assert.equal(classes.has("is-embedded"), false);
});

test("unknown view falls back to the settings route", () => {
    const { window } = runEmbedMode("?embed=1&view=unknown", []);
    assert.equal(window.__invoiceFlowEmbed.initialPath, "/");
});
