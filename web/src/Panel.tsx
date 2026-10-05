// SPDX-License-Identifier: GPL-3.0-or-later
import { useCallback, useEffect, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import { errorText, networkLabel, networkShort, type Client, type Network, type NodeDto, type StatusDto } from "./api";
import { Inbox } from "./Inbox";
import { Nodes } from "./Nodes";
import { Settings } from "./Settings";
import { ROOT, cx } from "./styles";

export type Load<T> = { kind: "loading" } | { kind: "error"; message: string } | { kind: "ready"; data: T };

/** Calls `load` now and every `ms`, ignoring results after unmount. */
export function usePoll<T>(load: () => Promise<T>, ms: number, deps: unknown[]): [Load<T>, () => void] {
  const [state, setState] = useState<Load<T>>({ kind: "loading" });
  const loadRef = useRef(load);
  loadRef.current = load;
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let alive = true;
    const run = async () => {
      try {
        const data = await loadRef.current();
        if (alive) setState({ kind: "ready", data });
      } catch (err) {
        if (alive) setState({ kind: "error", message: errorText(err) });
      }
    };
    void run();
    const timer = setInterval(() => void run(), ms);
    return () => {
      alive = false;
      clearInterval(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ms, tick, ...deps]);
  const refresh = useCallback(() => setTick((t) => t + 1), []);
  return [state, refresh];
}

export function NetworkBadge({ network }: { network: Network }) {
  return (
    <span className={`${cx("badge")} ${cx(`badge--${network}`)}`} title={networkLabel[network]} aria-label={networkLabel[network]}>
      {networkShort[network]}
    </span>
  );
}

// Keys typed into our fields must never reach host hotkeys (Zeus binds Space to transmit).
function keepKeysLocal(e: KeyboardEvent<HTMLElement>) {
  const tag = (e.target as HTMLElement).tagName;
  if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
    e.stopPropagation();
  }
}

type Tab = "inbox" | "nodes" | "settings";

export function Panel({ client }: { client: Client }) {
  const [tab, setTab] = useState<Tab>("inbox");
  const [status, refreshStatus] = usePoll(() => client.status(), 2000, [client]);

  const nodes = status.kind === "ready" ? status.data.nodes : [];
  const noNodes = status.kind === "ready" && nodes.length === 0;

  return (
    <div className={ROOT} onKeyDown={keepKeysLocal}>
      <div className={cx("tabs")} role="tablist" aria-label="Mesh Messenger">
        {(["inbox", "nodes", "settings"] as Tab[]).map((t) => (
          <button
            key={t}
            type="button"
            role="tab"
            aria-selected={tab === t}
            className={`${cx("tab")} ${tab === t ? cx("tab--on") : ""}`}
            onClick={() => setTab(t)}
          >
            {t === "inbox" ? "Inbox" : t === "nodes" ? "Nodes" : "Settings"}
          </button>
        ))}
      </div>

      {status.kind === "loading" && (
        <p className={cx("notice")} role="status">
          Loading Mesh Messenger…
        </p>
      )}
      {status.kind === "error" && (
        <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
          Can’t reach the Mesh Messenger backend ({status.message}). Retrying.
        </p>
      )}

      {status.kind === "ready" && (
        <>
          {tab !== "settings" && <NodeBar client={client} nodes={nodes} onChanged={refreshStatus} />}
          {noNodes && tab !== "settings" ? (
            <div className={cx("pane")}>
              <div className={cx("card")}>
                <h2 className={cx("h")}>Add your first node</h2>
                <p className={cx("help")}>
                  Zeus talks to a Meshtastic or MeshCore node on your network. Scan finds it for you — no IP address or
                  router settings needed.
                </p>
                <div>
                  <button type="button" className={`${cx("button")} ${cx("button--primary")}`} onClick={() => setTab("settings")}>
                    Add node
                  </button>
                </div>
              </div>
            </div>
          ) : tab === "inbox" ? (
            <Inbox client={client} status={status.data} />
          ) : tab === "nodes" ? (
            <Nodes client={client} nodes={nodes} />
          ) : (
            <Settings client={client} status={status.data} onChanged={refreshStatus} />
          )}
        </>
      )}
    </div>
  );
}

function stateText(n: NodeDto): string {
  switch (n.state) {
    case "connected":
      return "● connected";
    case "connecting":
      return "… connecting";
    case "searching":
      return "… looking for it";
    case "error":
      return "▲ problem";
    case "disabled":
      return "○ off";
    default:
      return "○ disconnected";
  }
}

function NodeBar({ client, nodes, onChanged }: { client: Client; nodes: NodeDto[]; onChanged: () => void }) {
  if (nodes.length === 0) return null;
  const moves = nodes.filter((n) => n.pendingMove);
  return (
    <div>
      <div className={cx("nodes")} aria-label="Mesh nodes">
        {nodes.map((n) => (
          <span key={n.id} className={`${cx("node")} ${cx(`node--${n.state}`)}`} title={n.detail ?? undefined}>
            <NetworkBadge network={n.network} />
            <span>{n.name}</span>
            <span className={n.state === "connected" ? cx("ok") : n.state === "error" ? cx("warn") : cx("muted")}>
              {stateText(n)}
            </span>
          </span>
        ))}
      </div>
      {nodes
        .filter((n) => n.state === "error" && n.detail)
        .map((n) => (
          <p key={n.id} className={`${cx("notice")} ${cx("notice--warn")}`} role="status">
            {n.name}: {n.detail}
          </p>
        ))}
      {moves.map((n) => (
        <div key={n.id} className={`${cx("notice")} ${cx("notice--warn")}`} role="status">
          <span>
            {n.name} was found at <span className={cx("mono")}>{n.pendingMove}</span>. Passwords are saved for this node, so
            Zeus waits for you before switching.{" "}
          </span>
          <span className={cx("row")} style={{ marginTop: 6 }}>
            <button
              type="button"
              className={`${cx("button")} ${cx("button--primary")}`}
              onClick={() => void client.acceptMove(n.id).then(onChanged)}
            >
              Use new address
            </button>
            <button type="button" className={cx("button")} onClick={() => void client.ignoreMove(n.id).then(onChanged)}>
              Ignore
            </button>
          </span>
        </div>
      ))}
    </div>
  );
}
