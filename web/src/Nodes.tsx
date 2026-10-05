// SPDX-License-Identifier: GPL-3.0-or-later
import { useState } from "react";
import { ago, type Client, type NodeDto } from "./api";
import { NetworkBadge, usePoll } from "./Panel";
import { cx } from "./styles";

/** Heard nodes / contacts for each of our nodes. */
export function Nodes({ client, nodes }: { client: Client; nodes: NodeDto[] }) {
  const [nodeId, setNodeId] = useState(nodes[0]?.id ?? "");
  const node = nodes.find((n) => n.id === nodeId) ?? nodes[0];
  const [peers] = usePoll(() => (node ? client.peers(node.id) : Promise.resolve([])), 5000, [client, node?.id]);

  if (!node) return null;
  const list = peers.kind === "ready" ? peers.data : [];

  return (
    <div className={cx("pane")}>
      <div className={cx("row")}>
        {nodes.map((n) => (
          <button
            key={n.id}
            type="button"
            className={`${cx("button")} ${n.id === node.id ? cx("button--primary") : ""}`}
            aria-pressed={n.id === node.id}
            onClick={() => setNodeId(n.id)}
          >
            <NetworkBadge network={n.network} /> {n.name}
          </button>
        ))}
      </div>
      <div className={cx("card")}>
        <div className={cx("card-head")}>
          <NetworkBadge network={node.network} />
          <h2 className={cx("h")}>{node.self?.name ?? node.name}</h2>
          <span className={cx("muted")}>
            {[node.self?.model, node.self?.firmware, node.self?.radio].filter(Boolean).join(" · ") || node.detail}
          </span>
        </div>
        <span className={cx("help")}>
          {node.network === "meshcore" ? "Contacts on this node" : "Nodes this node has heard"} · SNR and hops are from the last
          packet heard.
        </span>
        {peers.kind === "loading" && <p className={cx("muted")}>Loading…</p>}
        {peers.kind === "error" && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
            {peers.message}
          </p>
        )}
        {peers.kind === "ready" && list.length === 0 && <p className={cx("muted")}>None yet.</p>}
        {list.length > 0 && (
          <div className={cx("scroll-x")}>
            <table className={cx("table")}>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Type</th>
                  <th>Heard</th>
                  <th className={cx("num")}>SNR</th>
                  <th className={cx("num")}>Hops</th>
                  <th className={cx("num")}>Batt</th>
                </tr>
              </thead>
              <tbody>
                {list.map((p) => (
                  <tr key={p.id}>
                    <td>
                      {p.favorite ? "★ " : ""}
                      <strong>{p.name}</strong>
                      {p.shortName ? <span className={cx("muted")}> ({p.shortName})</span> : null}
                      <div className={`${cx("muted")} ${cx("mono")}`} style={{ fontSize: 11 }}>
                        {p.id.length > 16 ? `${p.id.slice(0, 12)}…` : p.id}
                      </div>
                    </td>
                    <td>{p.kind}</td>
                    <td className={cx("mono")}>{ago(p.lastHeard)}</td>
                    <td className={`${cx("num")} ${p.snr !== undefined && p.snr !== null && p.snr < -10 ? cx("warn") : ""}`}>
                      {p.snr ?? "—"}
                    </td>
                    <td className={cx("num")}>{p.hops ?? "—"}</td>
                    <td className={cx("num")}>{p.batteryPercent !== undefined && p.batteryPercent !== null ? `${p.batteryPercent}%` : "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
