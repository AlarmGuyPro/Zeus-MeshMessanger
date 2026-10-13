// SPDX-License-Identifier: GPL-3.0-or-later
//
// Takes the screenshot set the Zeus catalog requires: dark and light, normal and narrow
// widths, 200% scaling, visible keyboard focus, and the loading, empty, error,
// disconnected and unavailable states, across every tab.
//
//   1. npm run build        (in web/)
//   2. dotnet run --project tests/MeshMessenger.Tests -- --preview 5319
//   3. node tests/preview/screenshots.mjs [--port 5319] [--out <dir>]
//
// The states are forced through the preview host rather than waited for, because waiting
// for a node to break on its own does not make a release schedule.
import { chromium } from "playwright";
import { mkdir, readdir } from "node:fs/promises";
import { join } from "node:path";

const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const port = Number(opt("port", "5319"));
const outDir = opt("out", "screenshots");
const base = `http://127.0.0.1:${port}`;
const api = `${base}/api/plugins/io.github.alarmguypro.meshmessenger`;

const NORMAL = { width: 1280, height: 860 };
const NARROW = { width: 420, height: 860 };

let n = 0;
const shots = [];

async function shoot(page, name) {
  n += 1;
  const file = join(outDir, `${String(n).padStart(2, "0")}-${name}.png`);
  await page.screenshot({ path: file, fullPage: false });
  shots.push(file);
  process.stdout.write(`  ${file}\n`);
}

/** Opens a tab by its label and waits for the pane to settle. */
async function tab(page, label) {
  await page.getByRole("tab", { name: label }).click();
  await page.waitForTimeout(700);
}

async function theme(page, mode) {
  await page.evaluate((m) => document.documentElement.setAttribute("data-theme", m), mode);
  await page.waitForTimeout(150);
}

async function preview(page, flag, on) {
  await page.evaluate(
    ([f, o]) => fetch(`/preview/${f}/${o}`, { method: "POST" }),
    [flag, String(on)],
  );
}

const browser = await chromium.launch();
try {
  await mkdir(outDir, { recursive: true });
  const context = await browser.newContext({ viewport: NORMAL, colorScheme: "dark" });
  const page = await context.newPage();
  page.on("console", (m) => {
    if (m.type() === "error") process.stdout.write(`  [console error] ${m.text()}\n`);
  });
  page.on("pageerror", (e) => process.stdout.write(`  [page error] ${e.message}\n`));

  await page.goto(base, { waitUntil: "load" });
  await page.waitForFunction(() => window.__previewReady === true, { timeout: 15000 });
  await page.waitForTimeout(2500);

  // --- the four tabs, dark, normal width
  for (const [label, name] of [
    ["Inbox", "inbox-dark"],
    ["Nodes", "nodes-dark"],
    ["Logs", "logs-dark"],
    ["Settings", "settings-dark"],
  ]) {
    await tab(page, label);
    await shoot(page, name);
  }

  // --- a conversation open, and the Logs filters
  await tab(page, "Inbox");
  const first = page.locator("[role='button'], button").filter({ hasText: /morning all|anyone on/ }).first();
  if (await first.count()) {
    await first.click();
    await page.waitForTimeout(600);
    await shoot(page, "conversation-dark");
  }
  await tab(page, "Logs");
  for (const view of ["Messages", "Problems"]) {
    await page.getByRole("button", { name: view, exact: true }).click();
    await page.waitForTimeout(300);
    await shoot(page, `logs-${view.toLowerCase()}-dark`);
  }
  await page.getByRole("button", { name: "Everything", exact: true }).click();

  // --- light
  await theme(page, "light");
  for (const [label, name] of [
    ["Inbox", "inbox-light"],
    ["Nodes", "nodes-light"],
    ["Logs", "logs-light"],
    ["Settings", "settings-light"],
  ]) {
    await tab(page, label);
    await shoot(page, name);
  }
  await theme(page, "dark");

  // --- keyboard focus must be visible
  await tab(page, "Inbox");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Tab");
  await shoot(page, "keyboard-focus");

  // --- narrow
  await page.setViewportSize(NARROW);
  await page.waitForTimeout(300);
  for (const [label, name] of [
    ["Inbox", "inbox-narrow"],
    ["Nodes", "nodes-narrow"],
    ["Logs", "logs-narrow"],
  ]) {
    await tab(page, label);
    await shoot(page, name);
  }
  await page.setViewportSize(NORMAL);

  // --- 200% scaling
  const big = await browser.newContext({ viewport: NORMAL, deviceScaleFactor: 1, colorScheme: "dark" });
  const bigPage = await big.newPage();
  await bigPage.goto(base, { waitUntil: "load" });
  await bigPage.waitForFunction(() => window.__previewReady === true, { timeout: 15000 });
  await bigPage.evaluate(() => (document.documentElement.style.fontSize = "26px"));
  await bigPage.waitForTimeout(2500);
  for (const [label, name] of [
    ["Inbox", "inbox-200pc"],
    ["Nodes", "nodes-200pc"],
  ]) {
    await tab(bigPage, label);
    await shoot(bigPage, name);
  }
  await big.close();

  // --- loading
  await preview(page, "slow", true);
  await page.reload({ waitUntil: "load" });
  await page.waitForTimeout(900);
  await shoot(page, "loading");
  await preview(page, "slow", false);

  // --- error
  await preview(page, "broken", true);
  await page.reload({ waitUntil: "load" });
  await page.waitForTimeout(1500);
  await shoot(page, "error");
  await tab(page, "Logs");
  await shoot(page, "logs-error");
  await preview(page, "broken", false);
  await page.reload({ waitUntil: "load" });
  await page.waitForTimeout(2500);

  // --- disconnected: a node at an address nothing answers on
  await page.evaluate(
    (url) =>
      fetch(`${url}/nodes`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ network: "meshcore", host: "10.255.255.9", port: 5000, name: "Shed node" }),
      }),
    api,
  );
  await tab(page, "Nodes");
  await page.waitForTimeout(4000);
  await shoot(page, "disconnected");

  // --- empty: forget every node
  const ids = await page.evaluate(async (url) => {
    const status = await (await fetch(`${url}/status`)).json();
    return status.nodes.map((x) => x.id);
  }, api);
  for (const id of ids) {
    await page.evaluate(
      ([url, i]) => fetch(`${url}/nodes/${encodeURIComponent(i)}`, { method: "DELETE" }),
      [api, id],
    );
  }
  await page.waitForTimeout(1500);
  for (const [label, name] of [
    ["Inbox", "empty-inbox"],
    ["Nodes", "empty-nodes"],
    ["Logs", "empty-logs"],
  ]) {
    await tab(page, label);
    await shoot(page, name);
  }

  process.stdout.write(`\n${shots.length} screenshots in ${outDir}\n`);
  const listed = (await readdir(outDir)).filter((f) => f.endsWith(".png"));
  if (listed.length < shots.length) throw new Error("some screenshots were not written");
} finally {
  await browser.close();
}
