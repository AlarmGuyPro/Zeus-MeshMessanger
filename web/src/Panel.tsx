// SPDX-License-Identifier: GPL-3.0-or-later
import { useCallback, useEffect, useMemo, useState } from "react";
import type { KeyboardEvent } from "react";
import {
  networkLabel,
  networkShort,
  textLimit,
  utf8Length,
  type Client,
  type ConversationDto,
  type MessageDto,
  type Network,
  type NodeDto,
} from "./api";
import { ROOT, cx } from "./styles";

const POLL_MS = 3000;

type Load<T> = { kind: "loading" } | { kind: "error"; message: string } | { kind: "ready"; data: T };

function NetworkBadge({ network }: { network: Network }) {
  return (
    <span className={cx("badge")} title={networkLabel[network]} aria-label={networkLabel[network]}>
      {networkShort[network]}
    </span>
  );
}

function conversationTitle(c: ConversationDto): string {
  if (c.title) return c.title;
  return c.kind === "channel" ? `Channel ${c.peer}` : c.peer;
}

// Keys typed into our fields must never reach host hotkeys (Zeus binds Space to transmit).
function keepKeysLocal(e: KeyboardEvent<HTMLElement>) {
  const tag = (e.target as HTMLElement).tagName;
  if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
    e.stopPropagation();
  }
}

export function Panel({ client }: { client: Client }) {
  const [nodes, setNodes] = useState<Load<NodeDto[]>>({ kind: "loading" });
  const [conversations, setConversations] = useState<ConversationDto[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [composingNew, setComposingNew] = useState(false);

  const refresh = useCallback(async () => {
    const [status, list] = await Promise.all([client.status(), client.conversations()]);
    return { status, list };
  }, [client]);

  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        const { status, list } = await refresh();
        if (!alive) return;
        setNodes({ kind: "ready", data: status.nodes });
        setConversations(list);
      } catch (err) {
        if (!alive) return;
        setNodes({ kind: "error", message: err instanceof Error ? err.message : "Backend unavailable" });
      }
    };
    void tick();
    const timer = setInterval(() => void tick(), POLL_MS);
    return () => {
      alive = false;
      clearInterval(timer);
    };
  }, [refresh]);

  const selected = conversations.find((c) => c.id === selectedId) ?? null;
  const nodeList = nodes.kind === "ready" ? nodes.data : [];
  const nodeById = useMemo(() => new Map(nodeList.map((n) => [n.id, n])), [nodeList]);

  const bodyMode = selected || composingNew ? "thread" : "list";

  return (
    <div className={ROOT} onKeyDown={keepKeysLocal}>
      {nodes.kind === "loading" && <p className={cx("notice")} role="status">Loading Mesh Messenger…</p>}

      {nodes.kind === "error" && (
        <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
          Can’t reach the Mesh Messenger backend ({nodes.message}). Retrying every {POLL_MS / 1000} seconds.
        </p>
      )}

      {nodes.kind === "ready" && nodes.data.length === 0 && (
        <p className={cx("notice")}>
          No mesh nodes are configured yet. Add a Meshtastic or MeshCore node to start messaging.
        </p>
      )}

      {nodes.kind === "ready" && nodes.data.length > 0 && (
        <>
          <div className={cx("nodes")} aria-label="Mesh nodes">
            {nodes.data.map((n) => (
              <span key={n.id} className={`${cx("node")} ${cx(`node--${n.state}`)}`} title={n.detail ?? undefined}>
                <NetworkBadge network={n.network} />
                <span>{n.name}</span>
                <span className={cx("muted")}>{n.state}</span>
              </span>
            ))}
            <span className={cx("grow")} />
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

          <div className={`${cx("body")} ${cx(`body--${bodyMode}`)}`}>
            <ConversationList
              conversations={conversations}
              nodeById={nodeById}
              selectedId={selectedId}
              onSelect={(id) => {
                setComposingNew(false);
                setSelectedId(id);
              }}
            />
            {composingNew ? (
              <NewConversation
                client={client}
                nodes={nodes.data}
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
                onBack={() => setSelectedId(null)}
              />
            ) : (
              <div className={cx("thread")}>
                <p className={cx("notice")}>Select a conversation, or start a new message.</p>
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
}

function ConversationList(props: {
  conversations: ConversationDto[];
  nodeById: Map<string, NodeDto>;
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  if (props.conversations.length === 0) {
    return (
      <div className={cx("list")}>
        <p className={cx("notice")}>No messages yet.</p>
      </div>
    );
  }
  return (
    <ul className={cx("list")} aria-label="Conversations">
      {props.conversations.map((c) => {
        const node = props.nodeById.get(c.connectorId);
        const isSelected = c.id === props.selectedId;
        return (
          <li key={c.id}>
            <button
              type="button"
              className={`${cx("item")} ${isSelected ? cx("item--selected") : ""}`}
              aria-current={isSelected ? "true" : undefined}
              onClick={() => props.onSelect(c.id)}
            >
              <span className={cx("item-top")}>
                <NetworkBadge network={c.network} />
                <span className={cx("item-title")}>{conversationTitle(c)}</span>
                {c.unread > 0 && (
                  <span className={cx("unread")} aria-label={`${c.unread} unread`}>
                    {c.unread}
                  </span>
                )}
              </span>
              <span className={cx("item-preview")}>
                {node?.name ?? c.connectorId}
                {c.last ? ` · ${c.last.text}` : ""}
              </span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

function Thread(props: { client: Client; conversation: ConversationDto; node: NodeDto | null; onBack: () => void }) {
  const { client, conversation, node } = props;
  const [messages, setMessages] = useState<Load<MessageDto[]>>({ kind: "loading" });
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        const data = await client.messages(conversation.id);
        if (alive) setMessages({ kind: "ready", data });
      } catch (err) {
        if (alive) setMessages({ kind: "error", message: err instanceof Error ? err.message : "Failed to load" });
      }
    };
    void tick();
    const timer = setInterval(() => void tick(), POLL_MS);
    return () => {
      alive = false;
      clearInterval(timer);
    };
  }, [client, conversation.id]);

  const bytes = utf8Length(draft.trim());
  const max = node ? textLimit(node, conversation.kind) : 0;
  const connected = node?.state === "connected";
  const canSend = connected && !sending && bytes > 0 && bytes <= max;
  const via = node ? `${node.name} (${networkLabel[node.network]})` : `${conversation.connectorId} (removed)`;

  const send = async () => {
    if (!canSend) return;
    setSending(true);
    setSendError(null);
    try {
      // Only the conversation id is sent; the backend decides which node transmits.
      const result = await client.reply(conversation.id, draft);
      if (result.ok) {
        setDraft("");
      } else {
        setSendError(result.error ?? "Send failed.");
      }
      setMessages((m) =>
        m.kind === "ready" && result.message ? { kind: "ready", data: [...m.data, result.message] } : m,
      );
    } catch (err) {
      setSendError(err instanceof Error ? err.message : "Send failed.");
    } finally {
      setSending(false);
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
        <span className={cx("muted")}>on {via}</span>
      </div>

      <div className={cx("messages")} aria-live="polite">
        {messages.kind === "loading" && <p className={cx("muted")}>Loading messages…</p>}
        {messages.kind === "error" && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="alert">
            Couldn’t load messages: {messages.message}
          </p>
        )}
        {messages.kind === "ready" && messages.data.length === 0 && <p className={cx("muted")}>No messages yet.</p>}
        {messages.kind === "ready" &&
          messages.data.map((m) => (
            <div
              key={m.id}
              className={`${cx("msg")} ${m.direction === "outbound" ? cx("msg--out") : ""} ${
                m.status === "failed" ? cx("msg--failed") : ""
              }`}
            >
              <div>{m.text}</div>
              <div className={cx("msg-meta")}>
                {m.direction === "inbound" ? (m.fromName ?? m.fromId) : "You"} ·{" "}
                {new Date(m.timestamp).toLocaleTimeString()}
                {m.status === "failed" && ` · Not sent: ${m.error ?? "unknown error"}`}
              </div>
            </div>
          ))}
      </div>

      <div className={cx("composer")}>
        {!connected && (
          <p className={`${cx("notice")} ${cx("notice--warn")}`} role="status">
            {node ? `${node.name} is ${node.state}.` : "This conversation’s node is no longer configured."} Replies
            only go out on {via}; they are never re-sent on another network.
          </p>
        )}
        <label className={cx("field")}>
          <span className={cx("muted")}>Reply via {via}</span>
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
  const [peer, setPeer] = useState("0");
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const node = props.nodes.find((n) => n.id === nodeId) ?? null;
  const bytes = utf8Length(text.trim());
  const max = node ? textLimit(node, kind) : 0;
  const canSend = !!node && node.state === "connected" && !busy && peer.trim() !== "" && bytes > 0 && bytes <= max;

  const submit = async () => {
    if (!node || !canSend) return;
    setBusy(true);
    setError(null);
    try {
      const result = await props.client.start(node.id, kind, peer, text);
      if (result.conversationId && result.ok) {
        props.onStarted(result.conversationId);
      } else {
        setError(result.error ?? "Send failed.");
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Send failed.");
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
      <div className={cx("composer")}>
        <label className={cx("field")}>
          <span>Send from node (sets the network)</span>
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
          <label className={cx("field")}>
            <span>Type</span>
            <select
              className={cx("input")}
              value={kind}
              onChange={(e) => {
                const next = e.target.value === "direct" ? "direct" : "channel";
                setKind(next);
                setPeer(next === "channel" ? "0" : "");
              }}
            >
              <option value="channel">Channel</option>
              <option value="direct">Direct</option>
            </select>
          </label>
          <label className={`${cx("field")} ${cx("grow")}`}>
            <span>{kind === "channel" ? "Channel index" : "Destination node"}</span>
            <input className={cx("input")} value={peer} onChange={(e) => setPeer(e.target.value)} />
          </label>
        </div>
        <label className={cx("field")}>
          <span>Message</span>
          <textarea className={cx("input")} rows={3} value={text} onChange={(e) => setText(e.target.value)} />
        </label>
        <div className={cx("row")}>
          <span className={bytes > max ? cx("over") : cx("muted")}>
            {node ? `${bytes} / ${max} bytes` : "Choose a node first"}
          </span>
          <span className={cx("grow")} />
          <button type="button" className={cx("button")} onClick={props.onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className={`${cx("button")} ${cx("button--primary")}`}
            disabled={!canSend}
            onClick={() => void submit()}
          >
            {busy ? "Sending…" : node ? `Send via ${networkLabel[node.network]}` : "Send"}
          </button>
        </div>
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
