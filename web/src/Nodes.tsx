// SPDX-License-Identifier: GPL-3.0-or-later
import { useMemo, useState } from "react";
import { ago, channelKindLabel, errorText, peerKindLabel, type Client, type NodeDto, type PeerDto } from "./api";
import { NetworkBadge, NodeState, usePoll } from "./Panel";
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

/**
 * When to show in the Updated column. MeshCore keeps a time of its own for when
 * our node last changed the record, which is the one to trust. Meshtastic keeps
 * no such thing, so there the only time available is when the station was last
 * heard — without this fallback the column was empty on every Meshtastic node.
 */
function updatedAt(p: PeerDto): string | undefined {
  return p.lastUpdated ?? p.lastHeard;
}

/**
 * The tooltip for the Updated column. MeshCore stores two times: when our node
 * last changed the record, and the timestamp the OTHER station put in its advert
 * by its own clock, which is often wrong and sometimes in the future.
 */
function heardTitle(p: PeerDto): string {
  const parts: string[] = [];
  if (!p.lastUpdated && p.lastHeard) return `Last heard ${new Date(p.lastHeard).toLocaleString()}`;
  parts.push(p.lastUpdated ? `Your node updated this record ${new Date(p.lastUpdated).toLocaleString()}` : "Your node hasn't recorded an update time");
  if (p.lastHeard) {
    const advert = new Date(p.lastHeard);
    const ahead = advert.getTime() - Date.now() > 60_000;
    parts.push(
      `The station's own advert says ${advert.toLocaleString()}${ahead ? " — that is in the future, so its clock is wrong" : ""}`,
    );
  }
  return parts.join(". ");
}

/** Heard nodes / contacts for each of our nodes. */
/**
 * The channels a node is listening on, and joining or leaving one. A MeshCore
 * channel is just a name and a 128-bit key held in one of the node's slots: a
 * "#name" channel derives its key from the name, so anyone who types the same
 * name lands on the same channel, while any other channel's key has to be
 * shared out of band. Nothing here transmits.
 */
function Channels({ client, node, onChanged }: { client: Client; node: NodeDto; onChanged: () => void }) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [secret, setSecret] = useState("");
  const [busy, setBusy] = useState(false);
  const [leaving, setLeaving] = useState<number | null>(null);
  const [note, setNote] = useState<{ kind: "ok" | "warn"; text: string } | null>(null);

  if (!node.canEditChannels) {
    return (
      <p className={cx("help")}>
        This node is listening on {node.channels.length} channel{node.channels.length === 1 ? "" : "s"}. Meshtastic
        channels are set in the Meshtastic app, not from Zeus.
      </p>
    );
  }

  const needsKey = name.trim().length > 0 && !name.trim().startsWith("#");
  const hashtag = name.trim().startsWith("#");
  const join = async () => {
    setBusy(true);
    setNote(null);
    try {
      const result = await client.joinChannel(node.id, name.trim(), secret.trim() || undefined);
      if (result.ok) {
        setNote({ kind: "ok", text: `Joined ${name.trim()}.` });
        setName("");
        setSecret("");
        setOpen(false);
      } else {
        setNote({ kind: "warn", text: result.error ?? "Couldn’t join it." });
      }
      onChanged();
    } catch (err) {
      setNote({ kind: "warn", text: errorText(err) });
    } finally {
      setBusy(false);
    }
  };

  const leave = async (index: number, label: string) => {
    setLeaving(null);
    setBusy(true);
    setNote(null);
    try {
      const result = await client.leaveChannel(node.id, index);
      setNote(
        result.ok
          ? { kind: "ok", text: `Left ${label}. Messages already in Zeus are kept.` }
          : { kind: "warn", text: result.error ?? "Couldn’t leave it." },
      );
      onChanged();
    } catch (err) {
      setNote({ kind: "warn", text: errorText(err) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <div className={cx("row")}>
        <strong>Channels</strong>
        <span className={cx("grow")} />
        <span className={cx("tx-label")}>Transmits: no</span>
        <button type="button" className={cx("button")} disabled={busy} onClick={() => setOpen(!open)}>
          {open ? "Cancel" : "Join a channel"}
        </button>
      </div>

      {open && (
        <>
          <label className={cx("field")}>
            <span>Channel name</span>
            <input
              className={cx("input")}
              value={name}
              placeholder="#denver, or a name you were given"
              onChange={(e) => setName(e.target.value)}
            />
          </label>
          <label className={cx("field")}>
            <span>Key {needsKey ? "(required)" : "(not needed for a # channel)"}</span>
            <input
              className={`${cx("input")} ${cx("mono")}`}
              value={secret}
              placeholder="32 hex characters"
              onChange={(e) => setSecret(e.target.value)}
            />
          </label>
          {hashtag && name.trim() !== name.trim().toLowerCase() && (
            <p className={`${cx("notice")} ${cx("notice--warn")}`} role="status">
              <strong>Capitals matter here.</strong> The key comes from the name exactly as typed, so{" "}
              <span className={cx("mono")}>{name.trim()}</span> and{" "}
              <span className={cx("mono")}>{name.trim().toLowerCase()}</span> are two different channels that cannot
              hear each other. Nearly every open channel is all lowercase — check how the name is spelled on the phone
              or node you want to talk to before joining.
            </p>
          )}
          <p className={cx("help")}>
            A name starting with <strong>#</strong> makes its own key from the name, so everyone who types it{" "}
            <strong>exactly</strong> the same way — capitals and all — is on the same channel. That is how open local
            channels work. Any other channel needs its key, which whoever runs it has to give you.
          </p>
          <div className={cx("row")}>
            <button
              type="button"
              className={`${cx("button")} ${cx("button--primary")}`}
              disabled={busy || name.trim().length === 0 || (needsKey && secret.trim().length === 0)}
              onClick={() => void join()}
            >
              {busy ? "Joining…" : "Join"}
            </button>
          </div>
        </>
      )}

      {note && (
        <p className={note.kind === "warn" ? `${cx("notice")} ${cx("notice--warn")}` : cx("help")} role="status">
          {note.text}
        </p>
      )}

      <ul className={cx("log")}>
        {node.channels.map((ch) => (
          <li key={ch.index} className={cx("row")}>
            <span className={cx("chip")}>{ch.index}</span>
            <strong>{ch.name}</strong>
            <span className={cx("muted")}>{channelKindLabel[ch.kind] ?? ch.kind}</span>
            <span className={cx("grow")} />
            {ch.index === 0 ? (
              <span className={cx("muted")}>Primary</span>
            ) : leaving === ch.index ? (
              <>
                <span className={cx("warn")}>Leave {ch.name}?</span>
                <button
                  type="button"
                  className={`${cx("button")} ${cx("button--primary")}`}
                  disabled={busy}
                  onClick={() => void leave(ch.index, ch.name)}
                >
                  Leave
                </button>
                <button type="button" className={cx("button")} disabled={busy} onClick={() => setLeaving(null)}>
                  Stay
                </button>
              </>
            ) : (
              <button type="button" className={cx("button")} disabled={busy} onClick={() => setLeaving(ch.index)}>
                Leave…
              </button>
            )}
          </li>
        ))}
      </ul>
    </>
  );
}

/** Which contacts a tidy-up would clear off the node. */
interface CleanupWhat {
  repeaters?: boolean;
  notHeardDays?: number;
}

export function Nodes({ client, nodes, onChanged }: { client: Client; nodes: NodeDto[]; onChanged: () => void }) {
  const [nodeId, setNodeId] = useState(nodes[0]?.id ?? "");
  const node = nodes.find((n) => n.id === nodeId) ?? nodes[0];
  const [peers, reloadPeers] = usePoll(() => (node ? client.peers(node.id) : Promise.resolve([])), 5000, [
    client,
    node?.id,
  ]);
  const [search, setSearch] = useState("");
  const [showRepeaters, setShowRepeaters] = useState(false);
  const [busy, setBusy] = useState<"refresh" | "advert" | "recycle" | "cleanup" | null>(null);
  const [staleDays, setStaleDays] = useState(30);
  const [cleanup, setCleanup] = useState<{ what: CleanupWhat; label: string; count: number } | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  const [starring, setStarring] = useState<string | null>(null);
  const [note, setNote] = useState<{ kind: "ok" | "warn"; text: string } | null>(null);

  const all = peers.kind === "ready" ? peers.data : [];
  const needle = search.trim().toLowerCase();
  // Searching always looks at everything, so a filter never hides what you asked for.
  const list = useMemo(() => {
    const found = all.filter((p) => matches(p, needle));
    const visible = needle || showRepeaters ? found : found.filter((p) => p.kind !== "repeater");
    return [...visible].sort((a, b) => {
      if (a.favorite !== b.favorite) return a.favorite ? -1 : 1;
      const at = updatedAt(a) ? Date.parse(updatedAt(a)!) : 0;
      const bt = updatedAt(b) ? Date.parse(updatedAt(b)!) : 0;
      if (bt !== at) return bt - at;
      return a.name.localeCompare(b.name);
    });
  }, [all, needle, showRepeaters]);
  const heardOnly = all.filter((p) => !p.savedOnNode).length;
  const repeaters = all.filter((p) => p.kind === "repeater").length;

  if (!node) return null;

  const capacity = node?.peerCapacity;
  const saved = all.filter((p) => p.savedOnNode).length;
  const recycling = node?.recycleOldestWhenFull === true;
  // With recycling on, a full list is the normal steady state, not a fault.
  // Meshtastic reports no capacity at all, and the API sends that as null
  // rather than omitting it. Treating null as a number made every Meshtastic
  // node look like a contact list that was full at zero.
  const hasCapacity = capacity !== undefined && capacity !== null;
  const blocked = hasCapacity && saved >= capacity && !recycling;

  const star = async (peerId: string, name: string, favourite: boolean) => {
    setStarring(peerId);
    setNote(null);
    try {
      const result = await client.setFavourite(node.id, peerId, favourite);
      setNote(
        result.ok
          ? { kind: "ok", text: favourite ? `${name} is a favourite and won’t be recycled.` : `${name} is no longer a favourite.` }
          : { kind: "warn", text: result.error ?? "Couldn’t change it." },
      );
      reloadPeers();
    } catch (err) {
      setNote({ kind: "warn", text: errorText(err) });
    } finally {
      setStarring(null);
    }
  };

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

  // Two steps on purpose: ask the node how many would go, show that number, and
  // only remove anything after the operator has seen it and said yes.
  const proposeCleanup = async (what: CleanupWhat, label: string) => {
    setBusy("cleanup");
    setNote(null);
    try {
      const result = await client.cleanupPeers(node.id, what, true);
      if (!result.ok) setNote({ kind: "warn", text: result.error ?? "Couldn’t check the list." });
      else if (result.count === 0) setNote({ kind: "ok", text: `Nothing to remove — no ${label}.` });
      else setCleanup({ what, label, count: result.count });
    } catch (err) {
      setNote({ kind: "warn", text: errorText(err) });
    } finally {
      setBusy(null);
    }
  };

  const confirmCleanup = async () => {
    if (!cleanup) return;
    setBusy("cleanup");
    setNote(null);
    try {
      const result = await client.cleanupPeers(node.id, cleanup.what, false);
      setNote(
        result.ok
          ? { kind: "ok", text: `Removed ${result.count} contact${result.count === 1 ? "" : "s"} from the node.` }
          : {
              kind: "warn",
              // A partial result is the common case when a node gets behind, so say how far
              // it got rather than only what went wrong.
              text: `Removed ${result.count} of ${cleanup.count}. ${result.error ?? "Couldn’t remove them all."}`,
            },
      );
      setCleanup(null);
      reloadPeers();
      onChanged();
    } catch (err) {
      setNote({ kind: "warn", text: errorText(err) });
    } finally {
      setBusy(null);
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
          <NodeState node={node} />
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
          contact from the phone app.
          {node.network === "meshcore" ? (
            <>
              {" "}
              <strong>Send advert</strong> announces this node on the air so other stations can add it and message you;
              it is the only button here that transmits.
            </>
          ) : (
            " Nothing on this tab transmits."
          )}
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

        {blocked && (
          <div className={`${cx("notice")} ${cx("notice--warn")}`} role="status">
            <strong>
              The node’s contact list is full ({saved} of {capacity}) and recycling is off.
            </strong>{" "}
            It can’t save any more stations, so a direct message to one it hasn’t already saved will fail. Tick the box
            below, or remove contacts you don’t need.
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

        <Channels client={client} node={node} onChanged={onChanged} />

        {hasCapacity && all.length > 0 && (
          <>
            <div className={cx("row")}>
              <strong>Tidy the list</strong>
              <span className={cx("grow")} />
              <span className={cx("tx-label")}>Transmits: no</span>
            </div>
            <div className={cx("row")}>
              <button
                type="button"
                className={cx("button")}
                disabled={busy !== null || repeaters === 0}
                onClick={() => void proposeCleanup({ repeaters: true }, "repeaters to remove")}
              >
                Remove all repeaters{repeaters > 0 ? ` (${repeaters})` : ""}
              </button>
              <button
                type="button"
                className={cx("button")}
                disabled={busy !== null}
                onClick={() => void proposeCleanup({ notHeardDays: staleDays }, `contacts that old`)}
              >
                Remove any not heard in
              </button>
              <select
                className={cx("input")}
                style={{ width: "auto" }}
                aria-label="How old a contact must be to remove it"
                value={staleDays}
                onChange={(e) => setStaleDays(Number(e.target.value))}
              >
                <option value={7}>7 days</option>
                <option value={30}>30 days</option>
                <option value={90}>90 days</option>
              </select>
            </div>
            <p className={cx("help")}>
              Removing a contact frees a slot on the node and nothing else — the other station keeps its copy of you,
              and nothing goes out on the air. <strong>Favourites and room servers are never removed</strong>, nor is
              any station whose age the node doesn’t know.
            </p>
            {cleanup && (
              <div className={cx("thread-tools")}>
                <span className={cx("warn")}>
                  Remove {cleanup.count} contact{cleanup.count === 1 ? "" : "s"} from {node.name}?
                  {cleanup.count >= 50 && " That takes a minute or two — the node is removed one contact at a time so it keeps up. Leave Zeus open; the count updates as it goes."}
                </span>
                <button
                  type="button"
                  className={`${cx("button")} ${cx("button--primary")}`}
                  disabled={busy !== null}
                  onClick={() => void confirmCleanup()}
                >
                  {busy === "cleanup" ? "Removing…" : "Remove them"}
                </button>
                <button type="button" className={cx("button")} disabled={busy !== null} onClick={() => setCleanup(null)}>
                  Keep them
                </button>
              </div>
            )}
          </>
        )}

        <label className={cx("field")}>
          <span>Search this list</span>
          <input
            className={cx("input")}
            value={search}
            placeholder="Type any part of a name, short name or key"
            onChange={(e) => setSearch(e.target.value)}
          />
        </label>

        {repeaters > 0 && (
          <label className={cx("row")}>
            <input type="checkbox" checked={showRepeaters} onChange={(e) => setShowRepeaters(e.target.checked)} />
            <span>
              Show repeaters ({repeaters}) — hidden by default because a busy mesh has hundreds. Search always finds
              them.
            </span>
          </label>
        )}

        {peers.kind === "loading" && <p className={cx("muted")}>Loading…</p>}
        {peers.kind === "error" && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
            {peers.message}
          </p>
        )}
        {peers.kind === "ready" && all.length === 0 && (
          <p className={cx("muted")}>
            {node.network === "meshcore" ? (
              <>
                None yet. Ask another station to send an advert, or press <strong>Send advert</strong> so they can add
                this node and reply.
              </>
            ) : (
              "None yet. Meshtastic nodes announce themselves regularly, so this fills in on its own once one is in range."
            )}
          </p>
        )}
        {peers.kind === "ready" && all.length > 0 && list.length === 0 && (
          <p className={cx("muted")}>Nothing matches “{search}”.</p>
        )}
        {list.length > 0 && (
          <>
            <p className={cx("help")}>
              Showing {list.length} of {all.length}
              {hasCapacity ? ` · ${saved} of ${capacity} contact slots used` : ""}
              {hasCapacity ? (recycling ? " · recycling on" : " · recycling off") : ""}.
            </p>
            <div className={cx("scroll-x")}>
              <table className={cx("table")}>
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Type</th>
                    <th>Updated</th>
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
                        {node.network === "meshcore" && p.savedOnNode ? (
                          <button
                            type="button"
                            className={cx("star")}
                            aria-pressed={p.favorite}
                            disabled={starring !== null || node.state !== "connected"}
                            title={
                              p.favorite
                                ? "A favourite: the node will never recycle it"
                                : "Make a favourite so the node never recycles it"
                            }
                            onClick={() => void star(p.id, p.name, !p.favorite)}
                          >
                            {p.favorite ? "★" : "☆"}
                          </button>
                        ) : p.favorite ? (
                          "★ "
                        ) : null}
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
                      <td>{peerKindLabel[p.kind] ?? p.kind}</td>
                      <td className={cx("mono")} title={heardTitle(p)}>
                        {ago(updatedAt(p))}
                      </td>
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
