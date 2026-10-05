// SPDX-License-Identifier: GPL-3.0-or-later
// Typed wrappers over the host's callBackend. Paths are relative to
// /api/plugins/io.github.alarmguypro.meshmessenger/ — never call Zeus routes directly.

export interface ZeusPluginApi {
  registerPanel(spec: { id: string; component: () => unknown }): void;
  callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

export type Network = "meshtastic" | "meshcore";
export type NodeState = "disconnected" | "connecting" | "connected" | "error";

export interface NodeDto {
  id: string;
  network: Network;
  name: string;
  state: NodeState;
  detail: string | null;
  maxDirectTextBytes: number;
  maxChannelTextBytes: number;
}

export interface MessageDto {
  id: string;
  direction: "inbound" | "outbound";
  fromId: string;
  fromName: string | null;
  text: string;
  timestamp: string;
  status: "received" | "sent" | "failed";
  error: string | null;
}

export interface ConversationDto {
  id: string;
  connectorId: string;
  network: Network;
  kind: "channel" | "direct";
  peer: string;
  title: string | null;
  last: MessageDto | null;
  unread: number;
}

export interface SendResponse {
  ok: boolean;
  error: string | null;
  conversationId: string | null;
  message: MessageDto | null;
}

export class BackendError extends Error {}

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new BackendError(`Backend returned ${response.status}`);
  }
  return (await response.json()) as T;
}

export function createClient(api: ZeusPluginApi) {
  return {
    status: async () => json<{ nodes: NodeDto[] }>(await api.callBackend("GET", "/status")),
    conversations: async () => json<ConversationDto[]>(await api.callBackend("GET", "/conversations")),
    messages: async (conversationId: string) =>
      json<MessageDto[]>(
        await api.callBackend("GET", `/conversations/${encodeURIComponent(conversationId)}/messages`),
      ),
    // A reply carries only the conversation id. The backend picks the node from it.
    reply: async (conversationId: string, text: string) =>
      json<SendResponse>(
        await api.callBackend("POST", `/conversations/${encodeURIComponent(conversationId)}/reply`, { text }),
      ),
    start: async (connectorId: string, kind: "channel" | "direct", peer: string, text: string) =>
      json<SendResponse>(await api.callBackend("POST", "/conversations", { connectorId, kind, peer, text })),
  };
}

export type Client = ReturnType<typeof createClient>;

export const networkLabel: Record<Network, string> = {
  meshtastic: "Meshtastic",
  meshcore: "MeshCore",
};

export const networkShort: Record<Network, string> = {
  meshtastic: "MT",
  meshcore: "MC",
};

export function textLimit(node: NodeDto, kind: "channel" | "direct"): number {
  return kind === "channel" ? node.maxChannelTextBytes : node.maxDirectTextBytes;
}

export function utf8Length(text: string): number {
  return new TextEncoder().encode(text).length;
}
