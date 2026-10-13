// SPDX-License-Identifier: GPL-3.0-or-later
import { useEffect, useMemo, useRef, useState } from "react";
import {
  errorText,
  channelKindLabel,
  networkLabel,
  networkShort,
  textLimit,
  utf8Length,
  type Client,
  type ConversationDto,
  type Kind,
  type MessageDto,
  type NodeDto,
  type PeerDto,
  type StatusDto,
} from "./api";
import { NetworkBadge, usePoll } from "./Panel";
import { Picker, type PickerOption } from "./Picker";
import { cx } from "./styles";

function conversationTitle(c: ConversationDto): string {
  if (c.title) return c.title;
  if (c.kind === "channel") return `Channel ${c.peer}`;
  return c.peer.length > 12 ? `${c.peer.slice(0, 12)}…` : c.peer;
}

function kindLabel(kind: Kind): string {
  return kind === "channel" ? "Channel" : kind === "room" ? "Room" : "Direct";
}

type Folder = "all" | "direct" | "channel" | "room";

const folderLabel: Record<Folder, string> = {
  all: "All",
  direct: "Direct",
  channel: "Channels",
  room: "Rooms",
};

export function Inbox({ client, status }: { client: Client; status: StatusDto }) {
  const [list] = usePoll(() => client.conversations(), 2000, [client, status.version]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // null when not composing; the object carries any prefill, e.g. from clicking a sender in a channel.
  const [composingNew, setComposingNew] = useState<NewMessagePrefill | null>(null);
  const [search, setSearch] = useState("");
  const [folder, setFolder] = useState<Folder>("all");

  const everything = list.kind === "ready" ? list.data : [];
  const needle = search.trim().toLowerCase();
  const conversations = needle
    ? everything.filter(
        (c) =>
          conversationTitle(c).toLowerCase().includes(needle) ||
          c.peer.toLowerCase().includes(needle) ||
          (c.last?.text ?? "").toLowerCase().includes(needle) ||
          (c.last?.fromName ?? "").toLowerCase().includes(needle),
      )
    : everything;
  const counts = useMemo(() => {
    const unread = { all: 0, direct: 0, channel: 0, room: 0 } as Record<Folder, number>;
    for (const c of everything) {
      unread.all += c.unread;
      unread[c.kind] += c.unread;
    }
    return unread;
  }, [everything]);
  const inFolder = folder === "all" ? conversations : conversations.filter((c) => c.kind === folder);
  // Unread first, then whatever has spoken most recently; silent joined channels sink to the bottom.
  const ordered = useMemo(
    () =>
      [...inFolder].sort((a, b) => {
        if ((b.unread > 0 ? 1 : 0) !== (a.unread > 0 ? 1 : 0)) return (b.unread > 0 ? 1 : 0) - (a.unread > 0 ? 1 : 0);
        const at = a.last ? Date.parse(a.last.timestamp) : 0;
        const bt = b.last ? Date.parse(b.last.timestamp) : 0;
        if (bt !== at) return bt - at;
        return conversationTitle(a).localeCompare(conversationTitle(b));
      }),
    [inFolder],
  );
  // Looked up in the unfiltered list: a thread stays open while you type in the search box.
  const selected = everything.find((c) => c.id === selectedId) ?? null;
  const nodeById = useMemo(() => new Map(status.nodes.map((n) => [n.id, n])), [status.nodes]);
  const bodyMode = selected || composingNew ? "thread" : "list";

  return (
    <div className={`${cx("body")} ${cx(`body--${bodyMode}`)}`}>
      <div className={cx("list")}>
        <div style={{ padding: 8 }}>
          <button
            type="button"
            className={`${cx("button")} ${cx("button--primary")}`}
            onClick={() => {
              setSelectedId(null);
              setComposingNew({});
            }}
          >
            New message
          </button>
          {everything.length > 3 && (
            <input
              className={cx("input")}
              style={{ marginTop: 8 }}
              value={search}
              placeholder="Search names and messages"
              aria-label="Search conversations"
              onChange={(e) => setSearch(e.target.value)}
            />
          )}
        </div>
        <div className={cx("folders")} role="tablist" aria-label="Conversation folders">
          {(["all", "direct", "channel", "room"] as Folder[]).map((f) => (
            <button
              key={f}
              type="button"
              role="tab"
              aria-selected={folder === f}
              className={`${cx("folder")} ${folder === f ? cx("folder--on") : ""}`}
              onClick={() => setFolder(f)}
            >
              {folderLabel[f]}
              {counts[f] > 0 && <span className={cx("unread")}>{counts[f]}</span>}
            </button>
          ))}
        </div>
        {list.kind === "error" && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
            Couldn’t load conversations: {list.message}
          </p>
        )}
        {list.kind === "ready" && everything.length === 0 && <p className={cx("notice")}>No messages yet.</p>}
        {list.kind === "ready" && everything.length > 0 && conversations.length === 0 && (
          <p className={cx("notice")}>Nothing matches “{search}”.</p>
        )}
        {list.kind === "ready" && conversations.length > 0 && ordered.length === 0 && (
          <p className={cx("notice")}>Nothing in {folderLabel[folder]} yet.</p>
        )}
        <ul aria-label="Conversations" style={{ margin: 0, padding: 0, listStyle: "none" }}>
          {ordered.map((c) => {
            const node = nodeById.get(c.connectorId);
            const isSelected = c.id === selectedId;
            return (
              <li key={c.id}>
                <button
                  type="button"
                  className={`${cx("item")} ${isSelected ? cx("item--selected") : ""}`}
                  aria-current={isSelected ? "true" : undefined}
                  onClick={() => {
                    setComposingNew(null);
                    setSelectedId(c.id);
                  }}
                >
                  <span className={cx("item-top")}>
                    <NetworkBadge network={c.network} />
                    <span className={cx("item-title")}>{conversationTitle(c)}</span>
                    {c.unread > 0 && !isSelected && (
                      <span className={cx("unread")} aria-label={`${c.unread} unread`}>
                        {c.unread}
                      </span>
                    )}
                  </span>
                  <span className={cx("item-preview")}>
                    {kindLabel(c.kind)} · {node?.name ?? "removed node"}
                    {c.last ? ` · ${c.last.text}` : c.kind === "channel" ? " · joined, nothing heard yet" : ""}
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      </div>
      {composingNew ? (
        <NewConversation
          client={client}
          nodes={status.nodes}
          prefill={composingNew}
          onCancel={() => setComposingNew(null)}
          onStarted={(id) => {
            setComposingNew(null);
            setSelectedId(id);
          }}
        />
      ) : selected ? (
        <Thread
          key={selected.id}
          client={client}
          conversation={selected}
          node={nodeById.get(selected.connectorId) ?? null}
          version={status.version}
          onBack={() => setSelectedId(null)}
          onDeleted={() => setSelectedId(null)}
          onMessageSender={(peers, name) => {
            const connectorId = selected.connectorId;
            if (peers.length === 1) {
              const existing = everything.find((c) => c.id === `${connectorId}:dm:${peers[0]}`);
              if (existing) {
                setSelectedId(existing.id);
                return;
              }
            }
            setSelectedId(null);
            setComposingNew({
              nodeId: connectorId,
              kind: "direct",
              peer: peers.length === 1 ? peers[0] : undefined,
              note:
                peers.length > 1
                  ? `More than one contact is called “${name}”. Names on a channel aren’t verified, so pick the right one by its key.`
                  : undefined,
            });
          }}
        />
      ) : (
        <div className={cx("thread")}>
          <p className={cx("notice")}>Select a conversation, or start a new message.</p>
        </div>
      )}
    </div>
  );
}

function statusLine(m: MessageDto): { text: string; warn: boolean } {
  switch (m.status) {
    case "sending":
      return { text: "Sending…", warn: false };
    case "sent":
      return { text: m.flood ? "Sent (flood)" : "Sent", warn: false };
    case "delivered":
      return { text: m.roundTripMs ? `Delivered · ${(m.roundTripMs / 1000).toFixed(1)} s` : "Delivered", warn: false };
    case "notconfirmed":
      return { text: "Sent · no acknowledgement", warn: true };
    case "failed":
      return { text: `Not sent — ${m.error ?? "unknown error"}`, warn: true };
    default:
      return { text: "", warn: false };
  }
}

function receptionLine(m: MessageDto): string {
  const r = m.reception;
  if (!r) return "";
  const parts: string[] = [];
  if (r.snr !== undefined && r.snr !== null) parts.push(`SNR ${r.snr.toFixed(2)} dB`);
  if (r.rssi !== undefined && r.rssi !== null) parts.push(`RSSI ${r.rssi} dBm`);
  if (r.direct) parts.push("direct");
  else if (r.hops !== undefined && r.hops !== null) parts.push(`${r.hops} hop${r.hops === 1 ? "" : "s"}`);
  if (r.viaMqtt) parts.push("via MQTT");
  return parts.join(" · ");
}

/**
 * The sender's name above a message in a channel or room. It becomes a button
 * when we can work out who they are, so the operator can answer them privately
 * instead of in front of everyone. On MeshCore that depends on the name
 * matching a contact, because a channel message carries no sender key.
 */
function Sender({ message, onMessage }: { message: MessageDto; onMessage: (peers: string[], name: string) => void }) {
  const name = message.fromName ?? message.fromId;
  const peers = message.senderPeers;
  const style = { fontWeight: 600, fontSize: 12 } as const;

  if (!peers || peers.length === 0) {
    return (
      <div style={style} title={peers ? "Zeus doesn’t know this station’s key, so it can’t message them privately." : undefined}>
        {name}
        {peers && <span className={cx("muted")}> · not in contacts</span>}
      </div>
    );
  }
  return (
    <div style={style}>
      <button
        type="button"
        className={cx("link")}
        style={style}
        title={peers.length > 1 ? `${peers.length} contacts share this name — pick one` : `Message ${name} directly`}
        onClick={() => onMessage(peers, name)}
      >
        {name}
      </button>
    </div>
  );
}

/** What clicking a channel sender, or New message, hands to the composer. */
interface NewMessagePrefill {
  nodeId?: string;
  kind?: "channel" | "direct";
  peer?: string;
  note?: string;
}

function Thread(props: {
  client: Client;
  conversation: ConversationDto;
  node: NodeDto | null;
  version: number;
  onBack: () => void;
  onDeleted: () => void;
  /** Called with the peers a sender resolved to, so the panel can open or start that direct conversation. */
  onMessageSender: (peers: string[], name: string) => void;
}) {
  const { client, conversation, node } = props;
  const [messages, reload] = usePoll(() => client.messages(conversation.id), 2000, [
    client,
    conversation.id,
    props.version,
  ]);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [muted, setMuted] = useState(conversation.muted);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [retrying, setRetrying] = useState<string | null>(null);
  const thread = useRef<HTMLDivElement | null>(null);

  // Newest is at the top, so "show me what just happened" means the top of the list.
  // Opening a conversation and sending both go there; an arrival while the operator is
  // reading older traffic deliberately leaves the scroll alone.
  //
  // The braces are load-bearing. React keeps whatever an effect RETURNS and calls it later
  // as the cleanup, so `() => el?.scrollTo(...)` hands React the return value of scrollTo.
  // That is undefined per the specification and in a plain browser, but a host whose webview
  // ships a chainable scroll polyfill returns the element — React then calls the element on
  // the next conversation change and throws "d is not a function" from inside its own
  // minified code, which is exactly how this was found, on a bench and not here.
  const toNewest = () => {
    thread.current?.scrollTo({ top: 0 });
  };
  useEffect(toNewest, [conversation.id]);

  const bytes = utf8Length(draft.trim());
  const max = node ? textLimit(node, conversation.kind) : 0;
  const connected = node?.state === "connected";
  const canSend = connected && !sending && bytes > 0 && bytes <= max;
  const via = node ? `${node.name} (${networkLabel[node.network]})` : "a node that's no longer configured";

  const send = async () => {
    if (!canSend) return;
    setSending(true);
    setSendError(null);
    try {
      // Only the conversation id is sent; the backend decides which node transmits.
      const result = await client.reply(conversation.id, draft);
      if (result.ok) {
        setDraft("");
        toNewest();
      }
      else setSendError(result.error ?? "Send failed.");
    } catch (err) {
      setSendError(errorText(err));
    } finally {
      setSending(false);
    }
  };

  const list = messages.kind === "ready" ? messages.data : [];
  const hasQueued = list.some((m) => m.queuedWhileAway);
  // Newest at the top, the way a mail client reads: what just arrived is where you are
  // already looking, and older traffic is below rather than off the bottom of a long thread.
  // The store keeps them oldest-first, so the reversal is purely for display.
  const newestFirst = useMemo(() => list.slice().reverse(), [list]);
  // In this order the queued block sits underneath, so the marker goes on the first
  // message that was waiting when Zeus opened.
  const firstQueued = newestFirst.findIndex((m) => m.queuedWhileAway);
  const failedCount = list.filter(
    (m) => m.direction === "outbound" && (m.status === "failed" || m.status === "notconfirmed"),
  ).length;

  const retry = async (messageId: string) => {
    setRetrying(messageId);
    setNotice(null);
    try {
      const result = await client.retry(conversation.id, messageId);
      if (!result.ok) setNotice(result.error ?? "Still couldn’t send it.");
      reload();
    } catch (err) {
      setNotice(errorText(err));
    } finally {
      setRetrying(null);
    }
  };

  const clearFailed = async () => {
    setBusy(true);
    setNotice(null);
    try {
      const result = await client.clearFailed(conversation.id);
      setNotice(result.count > 0 ? `Removed ${result.count} message${result.count === 1 ? "" : "s"}.` : "Nothing to remove.");
      reload();
    } catch (err) {
      setNotice(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    setBusy(true);
    try {
      await client.deleteConversation(conversation.id);
      props.onDeleted();
    } catch (err) {
      setNotice(errorText(err));
      setBusy(false);
    }
  };

  return (
    <section className={cx("thread")} aria-label={`Conversation ${conversationTitle(conversation)}`}>
      <div className={cx("thread-head")}>
        <button type="button" className={`${cx("button")} ${cx("back")}`} onClick={props.onBack}>
          Back
        </button>
        <NetworkBadge network={conversation.network} />
        <strong>{conversationTitle(conversation)}</strong>
        <span className={cx("muted")}>
          {kindLabel(conversation.kind)} · on {via}
        </span>
        <span className={cx("grow")} />
        <button
          type="button"
          className={cx("button")}
          aria-pressed={muted}
          onClick={() => {
            const next = !muted;
            setMuted(next);
            void client.mute(conversation.id, next);
          }}
        >
          {muted ? "Unmute" : "Mute"}
        </button>
        {list.length > 0 && (
          <button type="button" className={cx("button")} disabled={busy} onClick={() => setConfirmDelete(true)}>
            Delete…
          </button>
        )}
      </div>

      {(failedCount > 0 || notice || confirmDelete) && (
        <div className={cx("thread-tools")}>
          {failedCount > 0 && (
            <>
              <span className={cx("muted")}>
                {failedCount} message{failedCount === 1 ? "" : "s"} never went out.
              </span>
              <button type="button" className={cx("button")} disabled={busy} onClick={() => void clearFailed()}>
                Clear them
              </button>
              <span className={cx("tx-label")}>Transmits: no</span>
            </>
          )}
          {notice && <span className={cx("muted")}>{notice}</span>}
          {confirmDelete && (
            <>
              <span className={cx("warn")}>
                Delete this conversation from Zeus? The messages are removed from Zeus only.
              </span>
              <button
                type="button"
                className={`${cx("button")} ${cx("button--primary")}`}
                disabled={busy}
                onClick={() => void remove()}
              >
                Delete
              </button>
              <button type="button" className={cx("button")} onClick={() => setConfirmDelete(false)}>
                Keep
              </button>
            </>
          )}
        </div>
      )}

      <div className={cx("messages")} ref={thread} aria-live="polite" aria-label="Messages, newest first">
        {messages.kind === "loading" && <p className={cx("muted")}>Loading messages…</p>}
        {messages.kind === "error" && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
            Couldn’t load messages: {messages.message}
          </p>
        )}
        {messages.kind === "ready" && list.length === 0 && (
          <p className={cx("muted")}>
            {conversation.kind === "channel"
              ? "Nothing heard on this channel yet. Anything you send goes out to everyone on it."
              : "No messages yet."}
          </p>
        )}
        {hasQueued && firstQueued !== 0 && <div className={cx("divider")}>Since Zeus connected</div>}
        {newestFirst.map((m, i) => {
          const s = statusLine(m);
          const rx = receptionLine(m);
          return (
            <div key={m.id} style={{ display: "contents" }}>
              {hasQueued && i === firstQueued && (
                <div className={cx("divider")}>Received while Zeus was closed</div>
              )}
              <div
                className={`${cx("msg")} ${m.direction === "outbound" ? cx("msg--out") : ""} ${
                  m.status === "failed" || m.status === "notconfirmed" ? cx("msg--failed") : ""
                }`}
              >
                {m.direction === "inbound" && conversation.kind !== "direct" && (
                  <Sender message={m} onMessage={props.onMessageSender} />
                )}
                <div>{m.text}</div>
                <div className={cx("msg-meta")}>
                  {new Date(m.timestamp).toLocaleString([], { dateStyle: "short", timeStyle: "short" })}
                  {rx && ` · ${rx}`}
                  {s.text && <span className={s.warn ? cx("warn") : undefined}> · {s.text}</span>}
                  {m.direction === "outbound" && (m.status === "failed" || m.status === "notconfirmed") && (
                    <>
                      {" · "}
                      <button
                        type="button"
                        className={cx("link")}
                        disabled={!connected || retrying !== null}
                        onClick={() => void retry(m.id)}
                      >
                        {retrying === m.id ? "Sending…" : "Send again"}
                      </button>
                    </>
                  )}
                </div>
              </div>
            </div>
          );
        })}
      </div>

      <div className={cx("composer")}>
        {!connected && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="status">
            {node ? `${node.name} is ${node.state}.` : "This conversation’s node is no longer configured."} Replies only go
            out on {via}; they are never re-sent on another network.
          </p>
        )}
        <label className={cx("field")}>
          <span className={cx("muted")}>
            {conversation.kind === "room" ? "Post to the room via" : "Reply via"} {via}
          </span>
          <textarea
            className={cx("input")}
            rows={2}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                void send();
              }
            }}
          />
        </label>
        <div className={cx("row")}>
          <span className={bytes > max ? cx("over") : cx("muted")}>
            {bytes} / {max} bytes
          </span>
          <span className={cx("grow")} />
          <button
            type="button"
            className={`${cx("button")} ${cx("button--primary")}`}
            disabled={!canSend}
            onClick={() => void send()}
          >
            {sending ? "Sending…" : "Send"}
          </button>
        </div>
        {sendError && (
          <p className={cx("over")} role="alert">
            {sendError}
          </p>
        )}
      </div>
    </section>
  );
}

function NewConversation(props: {
  client: Client;
  nodes: NodeDto[];
  prefill: NewMessagePrefill;
  onCancel: () => void;
  onStarted: (conversationId: string) => void;
}) {
  // With more than one node, nothing is preselected: the operator must choose
  // the network — unless we already know, because this came from a message.
  const [nodeId, setNodeId] = useState(
    props.prefill.nodeId ?? (props.nodes.length === 1 ? props.nodes[0].id : ""),
  );
  const [kind, setKind] = useState<"channel" | "direct">(props.prefill.kind ?? "channel");
  const [peer, setPeer] = useState(props.prefill.peer ?? "");
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [peers, setPeers] = useState<PeerDto[]>([]);

  const node = props.nodes.find((n) => n.id === nodeId) ?? null;

  useEffect(() => {
    let alive = true;
    setPeers([]);
    if (!nodeId) return;
    props.client
      .peers(nodeId)
      .then((p) => alive && setPeers(p.filter((x) => x.kind === "chat" || x.kind === "room")))
      .catch(() => alive && setPeers([]));
    return () => {
      alive = false;
    };
  }, [nodeId, props.client]);

  // Changing the node or the kind invalidates whoever was chosen. The first run
  // is skipped so it doesn't immediately wipe a peer we were opened with.
  const settled = useRef(props.prefill.peer === undefined);
  useEffect(() => {
    if (!settled.current) {
      settled.current = true;
      return;
    }
    setPeer(kind === "channel" && node && node.channels.length > 0 ? String(node.channels[0].index) : "");
  }, [kind, nodeId]); // eslint-disable-line react-hooks/exhaustive-deps

  const selectedPeer = peers.find((p) => p.id === peer);
  const effectiveKind = kind === "direct" && selectedPeer?.kind === "room" ? "room" : kind;
  const bytes = utf8Length(text.trim());
  const max = node ? textLimit(node, effectiveKind) : 0;
  const canSend = !!node && node.state === "connected" && !busy && peer !== "" && bytes > 0 && bytes <= max;

  const submit = async () => {
    if (!node || !canSend) return;
    setBusy(true);
    setError(null);
    try {
      const result = await props.client.start(node.id, effectiveKind, peer, text);
      if (result.conversationId && result.ok) props.onStarted(result.conversationId);
      else setError(result.error ?? "Send failed.");
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className={cx("thread")} aria-label="New message">
      <div className={cx("thread-head")}>
        <button type="button" className={`${cx("button")} ${cx("back")}`} onClick={props.onCancel}>
          Back
        </button>
        <strong>New message</strong>
      </div>
      {props.prefill.note && (
        <div className={cx("thread-tools")}>
          <span className={cx("muted")}>{props.prefill.note}</span>
        </div>
      )}
      <div className={cx("composer")} style={{ flex: 1, overflowY: "auto" }}>
        <Picker
          label="Send from node (this sets the network)"
          value={nodeId}
          placeholder="Type to search your nodes"
          options={props.nodes.map((n) => ({
            id: n.id,
            label: n.name,
            tag: networkShort[n.network],
            detail: `${networkLabel[n.network]} · ${n.state}`,
          }))}
          onChange={setNodeId}
        />
        <div className={cx("row")}>
          <button
            type="button"
            className={`${cx("button")} ${kind === "channel" ? cx("button--primary") : ""}`}
            aria-pressed={kind === "channel"}
            onClick={() => setKind("channel")}
          >
            Channel
          </button>
          <button
            type="button"
            className={`${cx("button")} ${kind === "direct" ? cx("button--primary") : ""}`}
            aria-pressed={kind === "direct"}
            onClick={() => setKind("direct")}
          >
            Direct / Room
          </button>
        </div>
        {kind === "channel" ? (
          <Picker
            label="Channel"
            value={peer}
            disabled={!node}
            emptyText="Choose a node first"
            placeholder="Type to search channels"
            options={(node?.channels ?? []).map((c) => ({
              id: String(c.index),
              label: c.name,
              detail: `Slot ${c.index} · ${channelKindLabel[c.kind] ?? c.kind}`,
            }))}
            onChange={setPeer}
          />
        ) : (
          <Picker
            label="To"
            value={peer}
            disabled={!node}
            emptyText={node ? "No stations known yet — ask them to send an advert" : "Choose a node first"}
            options={peers.map(
              (p): PickerOption => ({
                id: p.id,
                label: p.name,
                tag: p.kind === "room" ? "Room" : undefined,
                detail: [p.shortName, p.id.length > 16 ? `${p.id.slice(0, 12)}…` : p.id].filter(Boolean).join(" · "),
                note: p.savedOnNode ? undefined : "heard, will be saved on send",
              }),
            )}
            onChange={setPeer}
          />
        )}
        <label className={cx("field")}>
          <span>Message</span>
          <textarea className={cx("input")} rows={3} value={text} onChange={(e) => setText(e.target.value)} />
        </label>
        <div className={cx("row")}>
          <span className={bytes > max ? cx("over") : cx("muted")}>{node ? `${bytes} / ${max} bytes` : "Choose a node first"}</span>
          <span className={cx("grow")} />
          <button type="button" className={cx("button")} onClick={props.onCancel}>
            Cancel
          </button>
          <button type="button" className={`${cx("button")} ${cx("button--primary")}`} disabled={!canSend} onClick={() => void submit()}>
            {busy ? "Sending…" : node ? `Send via ${networkLabel[node.network]}` : "Send"}
          </button>
        </div>
        {node && (
          <p className={cx("help")}>
            This conversation will belong to {node.name} ({networkLabel[node.network]}). Every reply in it goes out on that node
            only.
          </p>
        )}
        {node && node.state !== "connected" && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="status">
            {node.name} is {node.state}.
          </p>
        )}
        {error && (
          <p className={cx("over")} role="alert">
            {error}
          </p>
        )}
      </div>
    </section>
  );
}
