// SPDX-License-Identifier: GPL-3.0-or-later
import { useMemo, useState } from "react";
import { type Client, type LogLevel, type NodeDebugDto } from "./api";
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
function asText(nodes: NodeDebugDto[], version: string): string {
  const lines = [`Mesh Messenger ${version} — ${new Date().toISOString()}`];
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

export function Debug({ client }: { client: Client }) {
  const [state] = usePoll(() => client.debug(), 2000, [client]);
  const [filter, setFilter] = useState("");
  const [copied, setCopied] = useState(false);

  const nodes = state.kind === "ready" ? state.data.nodes : [];
  const needle = filter.trim().toLowerCase();
  const shown = useMemo(
    () =>
      nodes.map((n) => ({
        node: n,
        activity: needle ? n.activity.filter((a) => a.text.toLowerCase().includes(needle)) : n.activity,
      })),
    [nodes, needle],
  );

  const copy = async () => {
    if (state.kind !== "ready") return;
    try {
      await navigator.clipboard.writeText(asText(nodes, state.data.pluginVersion));
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
          What each node and Zeus have done recently — connects, commands, replies and errors. Message text is never
          logged. The log is kept in memory only and starts empty each time Zeus opens. Copy it into a bug report when
          something doesn’t work.
        </p>
        <label className={cx("field")}>
          <span>Search the log</span>
          <input
            className={cx("input")}
            value={filter}
            placeholder="e.g. advert, ack, failed"
            onChange={(e) => setFilter(e.target.value)}
          />
        </label>
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
            <span className={node.state === "connected" ? cx("ok") : cx("muted")}>{node.state}</span>
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
            {node.contactsHeardOnly > 0 ? `, ${node.contactsHeardOnly} heard but not saved` : ""} · {node.channels}{" "}
            channel{node.channels === 1 ? "" : "s"}
            {node.identity ? ` · paired with ${node.identity.slice(0, 12)}…` : ""}
          </p>
          {node.detail && <p className={cx("help")}>{node.detail}</p>}
          {activity.length === 0 ? (
            <p className={cx("help")}>{needle ? "Nothing in the log matches." : "Nothing logged yet."}</p>
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
