// SPDX-License-Identifier: GPL-3.0-or-later
import { useMemo, useState } from "react";
import { ago, errorText, type Client, type NodeDto, type PeerDto } from "./api";
import { NetworkBadge, usePoll } from "./Panel";
import { cx } from "./styles";

function matches(p: PeerDto, needle: string): boolean {
  if (!needle) return true;
  return (
    p.name.toLowerCase().includes(needle) ||
    (p.shortName ?? "").toLowerCase().includes(needle) ||
    p.id.toLowerCase().includes(needle) ||
    p.kind.toLowerCase().includes(needle)
  );
}

/** Heard nodes / contacts for each of our nodes. */
export function Nodes({ client, nodes, onChanged }: { client: Client; nodes: NodeDto[]; onChanged: () => void }) {
  const [nodeId, setNodeId] = useState(nodes[0]?.id ?? "");
  const node = nodes.find((n) => n.id === nodeId) ?? nodes[0];
  const [peers, reloadPeers] = usePoll(() => (node ? client.peers(node.id) : Promise.resolve([])), 5000, [
    client,
    node?.id,
  ]);
  const [search, setSearch] = useState("");
  const [busy, setBusy] = useState<"refresh" | "advert" | "recycle" | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  const [note, setNote] = useState<{ kind: "ok" | "warn"; text: string } | null>(null);

  const all = peers.kind === "ready" ? peers.data : [];
  const needle = search.trim().toLowerCase();
  const list = useMemo(() => all.filter((p) => matches(p, needle)), [all, needle]);
  const heardOnly = all.filter((p) => !p.savedOnNode).length;

  if (!node) return null;

  const capacity = node?.peerCapacity;
  const saved = all.filter((p) => p.savedOnNode).length;
  const full = capacity !== undefined && saved >= capacity;
  const nearlyFull = capacity !== undefined && !full && saved >= capacity * 0.9;

  const remove = async (peerId: string, name: string) => {
    setRemoving(peerId);
    setNote(null);
    try {
      const result = await client.removePeer(node.id, peerId);
      setNote(
        result.ok
          ? { kind: "ok", text: `Removed ${name} from the node.` }
          : { kind: "warn", text: result.error ?? "Couldn’t remove it." },
      );
      reloadPeers();
      onChanged();
    } catch (err) {
      setNote({ kind: "warn", text: errorText(err) });
    } finally {
      setRemoving(null);
    }
  };

  const run = async (what: "refresh" | "advert" | "recycle", action: () => Promise<{ ok?: boolean; error?: string }>) => {
    setBusy(what);
    setNote(null);
    try {
      const result = await action();
      if (result.ok === false) setNote({ kind: "warn", text: result.error ?? "That didn’t work." });
      else if (what === "refresh") setNote({ kind: "ok", text: "Contacts re-read from the node." });
      else if (what === "recycle") setNote({ kind: "ok", text: "Saved on the node." });
      else setNote({ kind: "ok", text: "Advert sent. Other stations will list this node shortly." });
      reloadPeers();
      onChanged();
    } catch (err) {
      setNote({ kind: "warn", text: errorText(err) });
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className={cx("pane")}>
      {nodes.length > 1 && (
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
      )}
      <div className={cx("card")}>
        <div className={cx("card-head")}>
          <NetworkBadge network={node.network} />
          <h2 className={cx("h")}>{node.self?.name ?? node.name}</h2>
          <span className={cx("muted")}>
            {[node.self?.model, node.self?.firmware, node.self?.radio].filter(Boolean).join(" · ") || node.detail}
          </span>
        </div>

        <p className={cx("help")}>
          {node.network === "meshcore"
            ? "Stations this node knows. It learns them from adverts: another station announces itself and appears here."
            : "Nodes this node has heard. Meshtastic nodes announce themselves regularly, so this list fills in on its own."}{" "}
          SNR and hops are from the last packet heard.
        </p>

        <div className={cx("row")}>
          <button
            type="button"
            className={cx("button")}
            disabled={busy !== null || node.state !== "connected"}
            onClick={() => void run("refresh", () => client.refreshPeers(node.id).then(() => ({ ok: true })))}
          >
            {busy === "refresh" ? "Re-reading…" : "Re-read from node"}
          </button>
          <span className={cx("tx-label")}>Transmits: no</span>
          {node.network === "meshcore" && (
            <>
              <button
                type="button"
                className={cx("button")}
                disabled={busy !== null || node.state !== "connected"}
                onClick={() => void run("advert", () => client.announce(node.id, true))}
              >
                {busy === "advert" ? "Sending…" : "Send advert"}
              </button>
              <span className={`${cx("tx-label")} ${cx("tx-label--auto")}`}>Transmits: yes</span>
            </>
          )}
        </div>
        <p className={cx("help")}>
          <strong>Re-read from node</strong> asks the node for its contact list again, which helps after adding a
          contact from the phone app. <strong>Send advert</strong> announces this node on the air so other stations can
          add it and message you; it is the only button here that transmits.
        </p>
        {note && (
          <p className={`${cx("notice")} ${note.kind === "warn" ? cx("notice--warn") : ""}`} role="status">
            {note.text}
          </p>
        )}

        {heardOnly > 0 && (
          <p className={cx("help")}>
            {heardOnly} station{heardOnly === 1 ? " was" : "s were"} heard but not saved as contacts, because the node is
            set to add contacts manually. Zeus saves one automatically the first time you message it.
          </p>
        )}

        {capacity !== undefined && (full || nearlyFull) && (
          <div className={`${cx("notice")} ${cx("notice--warn")}`} role="status">
            <strong>
              The node’s contact list is {full ? "full" : "nearly full"} ({saved} of {capacity}).
            </strong>{" "}
            {full
              ? "It cannot save any more stations, so a direct message to a station it hasn’t already saved will fail. Remove contacts you don’t need below, or let the node recycle the oldest one automatically."
              : "Once it is full the node can’t save new stations, and direct messages to them fail."}{" "}
            On a busy mesh the node adds every station it hears, so the list fills on its own.
          </div>
        )}

        {node.recycleOldestWhenFull !== undefined && node.recycleOldestWhenFull !== null && (
          <>
            <label className={cx("row")}>
              <input
                type="checkbox"
                checked={node.recycleOldestWhenFull}
                disabled={busy !== null || node.state !== "connected"}
                onChange={(e) =>
                  void run("recycle", () => client.setRecycleOldest(node.id, e.target.checked))
                }
              />
              <span>Recycle the oldest contact when the list is full</span>
              <span className={cx("tx-label")}>Transmits: no</span>
            </label>
            <p className={cx("help")}>
              A setting on the node itself. With it on, the node drops its oldest non-favourite contact to make room for
              a new one, so the list never blocks new stations. With it off, a full list stays full until you remove
              something.
            </p>
          </>
        )}

        <label className={cx("field")}>
          <span>Search this list</span>
          <input
            className={cx("input")}
            value={search}
            placeholder="Name, short name or key"
            onChange={(e) => setSearch(e.target.value)}
          />
        </label>

        {peers.kind === "loading" && <p className={cx("muted")}>Loading…</p>}
        {peers.kind === "error" && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
            {peers.message}
          </p>
        )}
        {peers.kind === "ready" && all.length === 0 && (
          <p className={cx("muted")}>
            None yet. Ask another station to send an advert, or press <strong>Send advert</strong> so they can add this
            node and reply.
          </p>
        )}
        {peers.kind === "ready" && all.length > 0 && list.length === 0 && (
          <p className={cx("muted")}>Nothing matches “{search}”.</p>
        )}
        {list.length > 0 && (
          <>
            <p className={cx("help")}>
              Showing {list.length} of {all.length}
              {capacity !== undefined ? ` · ${saved} of ${capacity} contact slots used on the node` : ""}.
            </p>
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
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {list.map((p) => (
                    <tr key={p.id}>
                      <td>
                        {p.favorite ? "★ " : ""}
                        <strong>{p.name}</strong>
                        {p.shortName ? <span className={cx("muted")}> ({p.shortName})</span> : null}
                        {!p.savedOnNode ? (
                          <span
                            className={cx("chip")}
                            title="Heard, but not saved as a contact on the node. Zeus saves it when you first message it."
                          >
                            heard · not saved
                          </span>
                        ) : null}
                        <div className={`${cx("muted")} ${cx("mono")}`} style={{ fontSize: 11 }}>
                          {p.id.length > 16 ? `${p.id.slice(0, 12)}…` : p.id}
                        </div>
                      </td>
                      <td>{p.kind}</td>
                      <td className={cx("mono")}>{ago(p.lastHeard)}</td>
                      <td
                        className={`${cx("num")} ${p.snr !== undefined && p.snr !== null && p.snr < -10 ? cx("warn") : ""}`}
                      >
                        {p.snr ?? "—"}
                      </td>
                      <td className={cx("num")}>{p.hops ?? "—"}</td>
                      <td className={cx("num")}>
                        {p.batteryPercent !== undefined && p.batteryPercent !== null ? `${p.batteryPercent}%` : "—"}
                      </td>
                      <td>
                        {p.savedOnNode && node.network === "meshcore" && (
                          <button
                            type="button"
                            className={cx("button")}
                            disabled={removing !== null || node.state !== "connected"}
                            title={`Remove ${p.name} from the node's contact list`}
                            onClick={() => void remove(p.id, p.name)}
                          >
                            {removing === p.id ? "Removing…" : "Remove"}
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
