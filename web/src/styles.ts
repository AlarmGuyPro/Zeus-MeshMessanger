// SPDX-License-Identifier: GPL-3.0-or-later
// Zeus styling contract: every selector sits under the feature root class,
// colors come only from the public tokens, no animation.
export const ROOT = "io-github-alarmguypro-meshmessenger";
export const cx = (name: string) => `${ROOT}__${name}`;

const r = `.${ROOT}`;
const e = (name: string) => `.${ROOT}__${name}`;

export const css = `
${r} {
  container-type: inline-size;
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow-y: auto;
  color: var(--fg-1);
  background: var(--bg-1);
  font-family: var(--font-sans);
  font-size: 13px;
}
${r} *, ${r} *::before, ${r} *::after { box-sizing: border-box; }
${e("nodes")} {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 8px;
  border-bottom: 1px solid var(--line);
  background: var(--bg-2);
}
${e("node")} {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 8px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--bg-1);
}
${e("node--connected")} { border-color: var(--ok); }
${e("node--error")} { border-color: var(--amber); }
${e("badge")} {
  display: inline-block;
  min-width: 2.4em;
  padding: 1px 5px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-xs);
  font-family: var(--font-mono);
  font-size: 11px;
  font-weight: 600;
  text-align: center;
  color: var(--fg-0);
  background: var(--bg-3);
}
${e("muted")} { color: var(--fg-2); }
${e("body")} {
  display: grid;
  grid-template-columns: minmax(180px, 35%) 1fr;
  flex: 1;
  /* Short panels (or 200% zoom) scroll the whole panel rather than squeezing the thread to nothing. */
  min-height: 20em;
}
${e("list")} {
  display: flex;
  flex-direction: column;
  overflow-y: auto;
  min-width: 0;
  border-right: 1px solid var(--line);
  margin: 0;
  padding: 0;
  list-style: none;
}
${e("item")} {
  display: block;
  width: 100%;
  min-height: 44px;
  padding: 8px;
  border: 0;
  border-bottom: 1px solid var(--line);
  border-left: 3px solid var(--bg-1);
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}
${e("item--selected")} { border-left-color: var(--accent); background: var(--bg-2); }
${e("item-top")} { display: flex; min-width: 0; align-items: center; gap: 6px; }
${e("item-title")} { flex: 1; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
${e("item-preview")} { display: block; margin-top: 2px; color: var(--fg-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
${e("unread")} {
  padding: 0 6px;
  border-radius: var(--r-lg);
  background: var(--accent);
  color: var(--bg-0);
  font-size: 11px;
  font-weight: 700;
}
${e("thread")} { display: flex; flex-direction: column; min-height: 0; }
${e("thread-head")} {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px;
  border-bottom: 1px solid var(--line);
  background: var(--panel-top);
}
${e("messages")} { flex: 1; overflow-y: auto; padding: 8px; display: flex; flex-direction: column; gap: 6px; }
${e("msg")} {
  max-width: 85%;
  padding: 6px 8px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--bg-2);
  overflow-wrap: anywhere;
}
${e("msg--out")} { align-self: flex-end; background: var(--bg-3); }
${e("msg--failed")} { border-color: var(--amber); }
${e("msg-meta")} { margin-top: 2px; font-size: 11px; color: var(--fg-2); }
${e("composer")} {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px;
  border-top: 1px solid var(--line);
  background: var(--panel-bot);
}
${e("row")} { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
${e("grow")} { flex: 1; }
${e("field")} { display: flex; flex-direction: column; gap: 2px; }
${e("input")} {
  width: 100%;
  min-height: 32px;
  padding: 6px 8px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  background: var(--bg-inset);
  color: var(--fg-0);
  font: inherit;
  resize: vertical;
}
${e("button")} {
  min-height: 32px;
  padding: 4px 12px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  background: var(--bg-3);
  color: var(--fg-0);
  font: inherit;
  cursor: pointer;
}
${e("button--primary")} { border-color: var(--accent); }
${e("button")}:disabled { cursor: not-allowed; color: var(--fg-3); border-color: var(--line); }
${r} :focus-visible { outline: 2px solid var(--accent-bright); outline-offset: 2px; }
${e("notice")} {
  margin: 8px;
  padding: 8px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--bg-2);
}
${e("notice--warn")} { border-color: var(--amber); }
${e("over")} { color: var(--amber); font-weight: 600; }
${e("back")} { display: none; }
${e("tabs")} {
  display: flex;
  flex-wrap: wrap;
  align-items: stretch;
  gap: 2px;
  padding: 0 6px;
  border-bottom: 1px solid var(--line);
  background: var(--bg-0);
}
${e("tab")} {
  min-height: 36px;
  padding: 0 12px;
  border: 0;
  border-bottom: 2px solid var(--bg-0);
  background: none;
  color: var(--fg-2);
  font: inherit;
  font-weight: 600;
  cursor: pointer;
}
${e("tab--on")} { color: var(--fg-0); border-bottom-color: var(--accent); }
${e("pane")} { flex: 1; min-height: 0; overflow-y: auto; padding: 12px; display: flex; flex-direction: column; gap: 12px; }
${e("card")} { padding: 12px; border: 1px solid var(--panel-border); border-radius: var(--r-md); background: var(--bg-2); display: flex; flex-direction: column; gap: 8px; }
${e("card-head")} { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
${e("h")} { margin: 0; font-size: 14px; font-weight: 600; color: var(--fg-0); }
${e("table")} { width: 100%; border-collapse: collapse; }
${e("table")} th { padding: 6px 8px; text-align: left; color: var(--fg-2); font-size: 11px; font-weight: 600; border-bottom: 1px solid var(--line); }
${e("table")} td { padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: middle; }
${e("num")} { font-family: var(--font-mono); text-align: right; white-space: nowrap; }
${e("table")} th${e("num")} { text-align: right; }
${e("mono")} { font-family: var(--font-mono); }
${e("scroll-x")} { overflow-x: auto; }
${e("tx-label")} {
  display: inline-block;
  padding: 1px 6px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-xs);
  color: var(--fg-2);
  font-size: 11px;
  white-space: nowrap;
}
${e("tx-label--auto")} { border-color: var(--amber); color: var(--amber); }
${e("help")} { color: var(--fg-2); font-size: 12px; line-height: 1.5; }
${e("progress")} { height: 4px; border-radius: var(--r-xs); background: var(--bg-3); overflow: hidden; }
${e("progress-bar")} { height: 4px; background: var(--accent); }
${e("divider")} { display: flex; align-items: center; gap: 8px; color: var(--fg-2); font-size: 12px; }
${e("divider")}::before, ${e("divider")}::after { content: ""; flex: 1; height: 1px; background: var(--line); }
${e("ok")} { color: var(--ok); }
${e("warn")} { color: var(--amber); }
${e("chip")} {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 28px;
  padding: 0 10px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-lg);
  background: var(--bg-1);
  font-size: 12px;
}
@container (max-width: 520px) {
  ${e("body")} { grid-template-columns: 1fr; }
  ${e("body--thread")} ${e("list")} { display: none; }
  ${e("body--list")} ${e("thread")} { display: none; }
  ${e("list")} { border-right: 0; }
  ${e("back")} { display: inline-block; }
}
`;

let injected = false;

export function injectStyles(): void {
  if (injected) return;
  injected = true;
  const style = document.createElement("style");
  style.setAttribute("data-feature", ROOT);
  style.textContent = css;
  document.head.appendChild(style);
}
