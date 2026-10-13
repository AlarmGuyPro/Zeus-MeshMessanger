// SPDX-License-Identifier: GPL-3.0-or-later
//
// Panel faults that only appear in a host, not in a plain browser.
//
//   1. npm run build        (in web/)
//   2. dotnet run --project tests/MeshMessenger.Tests -- --preview 5319
//   3. node tests/preview/regression.mjs [--port 5319]
//
// Each case renders the real panel against the real backend with a host quirk simulated,
// and fails if the panel's error boundary ever appears. The boundary is the signal: it only
// shows when something in the feature threw.
import { chromium } from "playwright";

const args = process.argv.slice(2);
const port = Number((args.indexOf("--port") >= 0 && args[args.indexOf("--port") + 1]) || 5319);
const base = `http://127.0.0.1:${port}`;

/**
 * Host quirks to render under. Each one is a real difference between a plain browser and an
 * embedded webview, and each was added because it broke something.
 */
const quirks = {
  "a plain browser": null,
  // Found on a Zeus bench, 13 October. Some hosts ship a scroll polyfill that returns the
  // element so calls can chain. React keeps whatever an effect returns and calls it later as
  // the cleanup, so an effect written as `() => el?.scrollTo(...)` hands React an element;
  // switching conversations then threw "d is not a function" from inside React's own
  // minified code, and took the whole Zeus workspace down with it.
  "a host whose scrollTo returns the element": () => {
    const real = Element.prototype.scrollTo;
    Element.prototype.scrollTo = function (...a) {
      real.apply(this, a);
      return this;
    };
  },
};

let failures = 0;

for (const [name, quirk] of Object.entries(quirks)) {
  const browser = await chromium.launch();
  try {
    const context = await browser.newContext({ viewport: { width: 1200, height: 800 } });
    if (quirk) await context.addInitScript(quirk);
    const page = await context.newPage();
    await page.goto(base, { waitUntil: "load" });
    await page.waitForFunction(() => window.__previewReady === true, { timeout: 20000 });
    await page.waitForTimeout(4000);

    const broke = async (where) => {
      if ((await page.locator("text=couldn’t draw this panel").count()) === 0) return false;
      const detail = await page
        .locator("text=/^(TypeError|Error|RangeError)/")
        .first()
        .innerText()
        .catch(() => "");
      process.stdout.write(`  FAIL  ${name}: the panel threw at ${where} — ${detail}\n`);
      failures += 1;
      return true;
    };

    // Every tab.
    for (const tab of ["Inbox", "Nodes", "Logs", "Settings"]) {
      await page.getByRole("tab", { name: tab }).click();
      await page.waitForTimeout(900);
      if (await broke(`the ${tab} tab`)) throw new Error("stop");
    }

    // Opening conversations one after another, which is what runs an effect's cleanup.
    await page.getByRole("tab", { name: "Inbox" }).click();
    await page.waitForTimeout(800);
    const rows = page.locator("button").filter({ hasText: /Channel · |Direct · / });
    const n = Math.min(await rows.count(), 5);
    for (let i = 0; i < n; i++) {
      await rows.nth(i).click();
      await page.waitForTimeout(800);
      if (await broke(`conversation ${i + 1} of ${n}`)) throw new Error("stop");
    }
    process.stdout.write(`  PASS  ${name} (${n} conversations, 4 tabs)\n`);
  } catch (err) {
    if (err.message !== "stop") {
      process.stdout.write(`  FAIL  ${name}: ${err.message}\n`);
      failures += 1;
    }
  } finally {
    await browser.close();
  }
}

process.stdout.write(failures === 0 ? "\nAll host quirks render clean.\n" : `\n${failures} failed.\n`);
process.exit(failures === 0 ? 0 : 1);
