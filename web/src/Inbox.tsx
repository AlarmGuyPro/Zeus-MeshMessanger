// SPDX-License-Identifier: GPL-3.0-or-later
import { useEffect, useMemo, useState } from "react";
import {
  errorText,
  networkLabel,
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
import { cx } from "./styles";

function conversationTitle(c: ConversationDto): string {
  if (c.title) return c.title;
  if (c.kind === "channel") return `Channel ${c.peer}`;
  return c.peer.length > 12 ? `${c.peer.slice(0, 12)}…` : c.peer;
}

function kindLabel(kind: Kind): string {
  return kind === "channel" ? "channel" : kind === "room" ? "room" : "direct";
}

export function Inbox({ client, status }: { client: Client; status: StatusDto }) {
  const [list] = usePoll(() => client.conversations(), 2000, [client, status.version]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [composingNew, setComposingNew] = useState(false);

  const conversations = list.kind === "ready" ? list.data : [];
  const selected = conversations.find((c) => c.id === selectedId) ?? null;
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
              setComposingNew(true);
            }}
          >
            New message
          </button>
        </div>
        {list.kind === "error" && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
            Couldn’t load conversations: {list.message}
          </p>
        )}
        {list.kind === "ready" && conversations.length === 0 && <p className={cx("notice")}>No messages yet.</p>}
        <ul aria-label="Conversations" style={{ margin: 0, padding: 0, listStyle: "none" }}>
          {conversations.map((c) => {
            const node = nodeById.get(c.connectorId);
            const isSelected = c.id === selectedId;
            return (
              <li key={c.id}>
                <button
                  type="button"
                  className={`${cx("item")} ${isSelected ? cx("item--selected") : ""}`}
                  aria-current={isSelected ? "true" : undefined}
                  onClick={() => {
                    setComposingNew(false);
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
                    {c.last ? ` · ${c.last.text}` : ""}
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
          onCancel={() => setComposingNew(false)}
          onStarted={(id) => {
            setComposingNew(false);
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

function Thread(props: {
  client: Client;
  conversation: ConversationDto;
  node: NodeDto | null;
  version: number;
  onBack: () => void;
}) {
  const { client, conversation, node } = props;
  const [messages] = usePoll(() => client.messages(conversation.id), 2000, [client, conversation.id, props.version]);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [muted, setMuted] = useState(conversation.muted);

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
      if (result.ok) setDraft("");
      else setSendError(result.error ?? "Send failed.");
    } catch (err) {
      setSendError(errorText(err));
    } finally {
      setSending(false);
    }
  };

  const list = messages.kind === "ready" ? messages.data : [];
  const firstLive = list.findIndex((m) => !m.queuedWhileAway);
  const hasQueued = list.some((m) => m.queuedWhileAway);

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
      </div>

      <div className={cx("messages")} aria-live="polite">
        {messages.kind === "loading" && <p className={cx("muted")}>Loading messages…</p>}
        {messages.kind === "error" && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
            Couldn’t load messages: {messages.message}
          </p>
        )}
        {messages.kind === "ready" && list.length === 0 && <p className={cx("muted")}>No messages yet.</p>}
        {hasQueued && <div className={cx("divider")}>Received while Zeus was closed</div>}
        {list.map((m, i) => {
          const s = statusLine(m);
          const rx = receptionLine(m);
          return (
            <div key={m.id} style={{ display: "contents" }}>
              {hasQueued && i === firstLive && i > 0 && <div className={cx("divider")}>Since Zeus connected</div>}
              <div
                className={`${cx("msg")} ${m.direction === "outbound" ? cx("msg--out") : ""} ${
                  m.status === "failed" || m.status === "notconfirmed" ? cx("msg--failed") : ""
                }`}
              >
                {m.direction === "inbound" && conversation.kind !== "direct" && (
                  <div style={{ fontWeight: 600, fontSize: 12 }}>{m.fromName ?? m.fromId}</div>
                )}
                <div>{m.text}</div>
                <div className={cx("msg-meta")}>
                  {new Date(m.timestamp).toLocaleString([], { dateStyle: "short", timeStyle: "short" })}
                  {rx && ` · ${rx}`}
                  {s.text && <span className={s.warn ? cx("warn") : undefined}> · {s.text}</span>}
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
  onCancel: () => void;
  onStarted: (conversationId: string) => void;
}) {
  // With more than one node, nothing is preselected: the operator must choose the network.
  const [nodeId, setNodeId] = useState(props.nodes.length === 1 ? props.nodes[0].id : "");
  const [kind, setKind] = useState<"channel" | "direct">("channel");
  const [peer, setPeer] = useState("");
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

  useEffect(() => {
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
      <div className={cx("composer")} style={{ flex: 1, overflowY: "auto" }}>
        <label className={cx("field")}>
          <span>Send from node (this sets the network)</span>
          <select className={cx("input")} value={nodeId} onChange={(e) => setNodeId(e.target.value)}>
            <option value="" disabled>
              Choose a node…
            </option>
            {props.nodes.map((n) => (
              <option key={n.id} value={n.id}>
                {networkLabel[n.network]} · {n.name} ({n.state})
              </option>
            ))}
          </select>
        </label>
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
            Direct / room
          </button>
        </div>
        {kind === "channel" ? (
          <label className={cx("field")}>
            <span>Channel</span>
            <select className={cx("input")} value={peer} onChange={(e) => setPeer(e.target.value)} disabled={!node}>
              {!node && <option value="">Choose a node first</option>}
              {node?.channels.map((c) => (
                <option key={c.index} value={String(c.index)}>
                  #{c.index} {c.name} ({c.kind})
                </option>
              ))}
            </select>
          </label>
        ) : (
          <label className={cx("field")}>
            <span>To</span>
            <select className={cx("input")} value={peer} onChange={(e) => setPeer(e.target.value)} disabled={!node}>
              <option value="">{node ? (peers.length ? "Choose…" : "No contacts known yet") : "Choose a node first"}</option>
              {peers.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.kind === "room" ? "Room · " : ""}
                  {p.name}
                  {p.shortName ? ` (${p.shortName})` : ""}
                </option>
              ))}
            </select>
          </label>
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
