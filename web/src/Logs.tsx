// SPDX-License-Identifier: GPL-3.0-or-later
import { useMemo, useState } from "react";
import { type Client, type HistoryDto, type LogLevel, type NodeDebugDto } from "./api";
import { NetworkBadge, usePoll } from "./Panel";
import { cx } from "./styles";

const levelClass: Record<LogLevel, string> = {
  info: "muted",
  send: "tx-text",
  receive: "ok",
  warning: "warn",
  error: "warn",
};

const levelLabel: Record<LogLevel, string> = {
  info: "·",
  send: "TX",
  receive: "RX",
  warning: "!",
  error: "✕",
};

function time(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleTimeString();
}

/** Plain text of everything on screen, for pasting into a bug report. */
function asText(nodes: NodeDebugDto[], version: string, history?: HistoryDto | null): string {
  const lines = [`Mesh Messenger ${version} — ${new Date().toISOString()}`];
  if (history) {
    lines.push(`History: ${history.conversations} conversations, ${history.bytes} of ${history.budget} bytes`);
  }
  for (const n of nodes) {
    lines.push(
      "",
      `Node ${n.name} (${n.network}) ${n.host}:${n.port} — ${n.state}${n.detail ? `: ${n.detail}` : ""}`,
      `  firmware ${n.self?.firmware ?? "?"} · model ${n.self?.model ?? "?"} · radio ${n.self?.radio ?? "?"}`,
      `  contacts ${n.contacts}${n.contactCapacity ? `/${n.contactCapacity}` : ""} saved, ${n.contactsHeardOnly} heard only · recycle-oldest ${n.recycleOldestWhenFull ?? "n/a"} · channels ${n.channels}`,
    );
    for (const a of n.activity) lines.push(`  ${a.at} [${a.level}] ${a.text}`);
  }
  return lines.join("\n");
}

/** Which kinds of line to show. "all" is everything; the rest narrow a busy log. */
const views = [
  { id: "all", label: "Everything", levels: null },
  { id: "messages", label: "Messages", levels: ["send", "receive"] as LogLevel[] },
  { id: "problems", label: "Problems", levels: ["warning", "error"] as LogLevel[] },
] as const;

type View = (typeof views)[number]["id"];

/** Bytes as something a person reads at a glance. */
function size(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function Logs({ client }: { client: Client }) {
  const [state] = usePoll(() => client.debug(), 2000, [client]);
  const [filter, setFilter] = useState("");
  const [view, setView] = useState<View>("all");
  const [copied, setCopied] = useState(false);

  const nodes = state.kind === "ready" ? state.data.nodes : [];
  const history = state.kind === "ready" ? state.data.history : null;
  const needle = filter.trim().toLowerCase();
  const levels = views.find((v) => v.id === view)!.levels;
  const shown = useMemo(
    () =>
      nodes.map((n) => ({
        node: n,
        activity: n.activity.filter(
          (a) =>
            (levels === null || levels.includes(a.level)) &&
            (needle === "" || a.text.toLowerCase().includes(needle)),
        ),
      })),
    [nodes, needle, levels],
  );

  const copy = async () => {
    if (state.kind !== "ready") return;
    try {
      await navigator.clipboard.writeText(asText(nodes, state.data.pluginVersion, state.data.history));
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  };

  return (
    <div className={cx("pane")}>
      <section className={cx("card")}>
        <div className={cx("row")}>
          <h2 className={cx("h")}>Activity log</h2>
          <span className={cx("grow")} />
          <span className={cx("tx-label")}>Transmits: no</span>
          <button type="button" className={cx("button")} onClick={() => void copy()}>
            {copied ? "Copied" : "Copy all"}
          </button>
        </div>
        <p className={cx("help")}>
          What each node and Zeus have done recently — connects, commands, adverts, every message in and out, and
          errors. Message text is never logged, only its size and how it arrived. The log is kept in memory only and
          starts empty each time Zeus opens. Copy it into a bug report when something doesn’t work.
        </p>
        <div className={cx("row")} role="group" aria-label="Which lines to show">
          {views.map((v) => (
            <button
              key={v.id}
              type="button"
              aria-pressed={view === v.id}
              className={`${cx("button")} ${view === v.id ? cx("button--primary") : ""}`}
              onClick={() => setView(v.id)}
            >
              {v.label}
            </button>
          ))}
        </div>
        <label className={cx("field")}>
          <span>Search the log</span>
          <input
            className={cx("input")}
            value={filter}
            placeholder="e.g. advert, ack, channel, failed"
            onChange={(e) => setFilter(e.target.value)}
          />
        </label>
        {history && (
          <p className={cx("help")}>
            Holding {history.conversations} conversation{history.conversations === 1 ? "" : "s"},{" "}
            {size(history.bytes)} of {size(history.budget)}. Past that, the conversations heard from longest ago are
            forgotten.
          </p>
        )}
      </section>

      {state.kind === "error" && (
        <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
          Can’t read the log ({state.message}).
        </p>
      )}
      {state.kind === "ready" && nodes.length === 0 && <p className={cx("help")}>No nodes yet.</p>}

      {shown.map(({ node, activity }) => (
        <section key={node.id} className={cx("card")} aria-label={`Log for ${node.name}`}>
          <div className={cx("row")}>
            <NetworkBadge network={node.network} />
            <h2 className={cx("h")}>{node.name}</h2>
            <span className={node.state === "connected" ? cx("ok") : node.state === "error" ? cx("warn") : cx("muted")}>
              {node.state === "connected"
                ? "● Connected"
                : node.state === "error"
                  ? "▲ Problem"
                  : node.state === "disabled"
                    ? "○ Turned off"
                    : `○ ${node.state[0].toUpperCase()}${node.state.slice(1)}`}
            </span>
          </div>
          <p className={`${cx("help")} ${cx("mono")}`}>
            {node.host}:{node.port}
            {node.self?.firmware ? ` · ${node.self.firmware}` : ""}
            {node.self?.model ? ` · ${node.self.model}` : ""}
            {node.self?.radio ? ` · ${node.self.radio}` : ""}
          </p>
          <p className={cx("help")}>
            {node.contacts}
            {node.contactCapacity !== undefined && node.contactCapacity !== null
              ? ` of ${node.contactCapacity}`
              : ""}{" "}
            contact{node.contacts === 1 ? "" : "s"} saved on the node
            {node.contactCapacity !== undefined &&
            node.contactCapacity !== null &&
            node.contacts >= node.contactCapacity ? (
              <span className={cx("warn")}> — full, so it can’t save new stations</span>
            ) : null}
            {node.contactsHeardOnly > 0 ? `, ${node.contactsHeardOnly} heard but not saved` : ""}
            {node.rememberedPeerLimit && node.contactsHeardOnly >= node.rememberedPeerLimit ? (
              <span className={cx("warn")}>
                {" "}
                — Zeus remembers {node.rememberedPeerLimit} of these and is forgetting the oldest
              </span>
            ) : null}{" "}
            · {node.channels} channel{node.channels === 1 ? "" : "s"}
            {node.identity ? ` · paired with ${node.identity.slice(0, 12)}…` : ""}
          </p>
          {node.detail && <p className={cx("help")}>{node.detail}</p>}
          {activity.length === 0 ? (
            <p className={cx("help")}>
              {needle || view !== "all" ? "Nothing in the log matches." : "Nothing logged yet."}
            </p>
          ) : (
            <ol className={cx("log")}>
              {activity
                .slice()
                .reverse()
                .map((a, i) => (
                  <li key={`${a.at}-${i}`} className={cx("log-line")}>
                    <span className={`${cx("mono")} ${cx("muted")}`}>{time(a.at)}</span>
                    <span className={`${cx("mono")} ${cx(levelClass[a.level] ?? "muted")}`}>
                      {levelLabel[a.level] ?? "·"}
                    </span>
                    <span>{a.text}</span>
                  </li>
                ))}
            </ol>
          )}
        </section>
      ))}
    </div>
  );
}
