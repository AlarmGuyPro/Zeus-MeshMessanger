// SPDX-License-Identifier: GPL-3.0-or-later
// Typed wrappers over the host's callBackend. Paths are relative to
// /api/plugins/io.github.alarmguypro.meshmessenger/ — never call Zeus routes directly.

export interface ZeusPluginApi {
  registerPanel(spec: { id: string; component: () => unknown }): void;
  callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

export type Network = "meshtastic" | "meshcore";
export type NodeState = "disconnected" | "connecting" | "connected" | "searching" | "error" | "disabled";
export type Kind = "channel" | "direct" | "room";

export interface SelfDto {
  identity: string;
  name: string;
  shortName?: string;
  firmware?: string;
  model?: string;
  radio?: string;
}

export interface ChannelDto {
  index: number;
  name: string;
  kind: "public" | "hashtag" | "private";
  primary: boolean;
}

export interface NodeDto {
  id: string;
  network: Network;
  name: string;
  host: string;
  port: number;
  enabled: boolean;
  state: NodeState;
  detail?: string;
  identity?: string;
  findIfAddressChanges: boolean;
  pendingMove?: string;
  self?: SelfDto;
  maxDirectTextBytes: number;
  maxChannelTextBytes: number;
  channels: ChannelDto[];
  peerCount: number;
}

export interface StatusDto {
  nodes: NodeDto[];
  scanRanges: string[];
  historyPerConversation: number;
  version: number;
}

export interface PeerDto {
  id: string;
  name: string;
  shortName?: string;
  kind: "chat" | "repeater" | "room" | "sensor" | "unknown";
  lastHeard?: string;
  snr?: number;
  hops?: number;
  batteryPercent?: number;
  latitude?: number;
  longitude?: number;
  favorite: boolean;
}

export interface ReceptionDto {
  snr?: number;
  rssi?: number;
  hops?: number;
  direct: boolean;
  viaMqtt: boolean;
}

export type Status = "received" | "sending" | "sent" | "delivered" | "notconfirmed" | "failed";

export interface MessageDto {
  id: string;
  direction: "inbound" | "outbound";
  fromId: string;
  fromName?: string;
  text: string;
  timestamp: string;
  status: Status;
  error?: string;
  reception?: ReceptionDto;
  queuedWhileAway: boolean;
  roundTripMs?: number;
  flood: boolean;
}

export interface ConversationDto {
  id: string;
  connectorId: string;
  network: Network;
  kind: Kind;
  peer: string;
  title?: string;
  last?: MessageDto;
  unread: number;
  muted: boolean;
}

export interface SendResponse {
  ok: boolean;
  error?: string;
  conversationId?: string;
  message?: MessageDto;
}

export interface FoundDto {
  network: Network;
  host: string;
  port: number;
  identity: string;
  name: string;
  shortName?: string;
  firmware?: string;
  model?: string;
  foundBy: string[];
  addedAs?: string;
}

export interface ScanDto {
  running: boolean;
  phase: string;
  probed: number;
  total: number;
  networks: string[];
  quietNetworks: string[];
  usedMdns: boolean;
  found: FoundDto[];
  otherAnswers: number;
  startedAt?: string;
  finishedAt?: string;
  cancelled: boolean;
  error?: string;
}

export interface DiscoveryDto {
  scan: ScanDto;
  defaultNetworks: string[];
}

export class BackendError extends Error {}

async function json<T>(response: Response): Promise<T> {
  const text = await response.text();
  let body: unknown = undefined;
  try {
    body = text ? JSON.parse(text) : undefined;
  } catch {
    body = undefined;
  }
  if (!response.ok) {
    const message =
      body && typeof body === "object" && "error" in body && typeof (body as { error: unknown }).error === "string"
        ? (body as { error: string }).error
        : `Backend returned ${response.status}`;
    throw new BackendError(message);
  }
  return body as T;
}

const enc = encodeURIComponent;

export function createClient(api: ZeusPluginApi) {
  const call = async <T>(method: string, path: string, body?: unknown) => json<T>(await api.callBackend(method, path, body));
  return {
    status: () => call<StatusDto>("GET", "/status"),
    peers: (nodeId: string) => call<PeerDto[]>("GET", `/nodes/${enc(nodeId)}/peers`),
    addNode: (body: { network: Network; host: string; port?: number; name?: string; identity?: string }) =>
      call<{ id: string }>("POST", "/nodes", body),
    updateNode: (
      id: string,
      body: { name?: string; host?: string; port?: number; enabled?: boolean; findIfAddressChanges?: boolean },
    ) => call<StatusDto>("PATCH", `/nodes/${enc(id)}`, body),
    removeNode: (id: string) => call<StatusDto>("DELETE", `/nodes/${enc(id)}`),
    repairNode: (id: string) => call<StatusDto>("POST", `/nodes/${enc(id)}/repair`),
    acceptMove: (id: string) => call<StatusDto>("POST", `/nodes/${enc(id)}/move`),
    ignoreMove: (id: string) => call<StatusDto>("DELETE", `/nodes/${enc(id)}/move`),
    saveSettings: (body: { scanRanges?: string[]; historyPerConversation?: number }) =>
      call<StatusDto>("PUT", "/settings", body),
    discovery: () => call<DiscoveryDto>("GET", "/discovery"),
    startScan: (ranges: string[], mdns: boolean) => call<DiscoveryDto>("POST", "/discovery/scan", { ranges, mdns }),
    stopScan: () => call<DiscoveryDto>("DELETE", "/discovery/scan"),
    conversations: () => call<ConversationDto[]>("GET", "/conversations"),
    messages: (conversationId: string) => call<MessageDto[]>("GET", `/conversations/${enc(conversationId)}/messages`),
    mute: (conversationId: string, muted: boolean) =>
      api.callBackend("POST", `/conversations/${enc(conversationId)}/mute`, { muted }),
    // A reply carries only the conversation id. The backend picks the node from it.
    reply: (conversationId: string, text: string) =>
      call<SendResponse>("POST", `/conversations/${enc(conversationId)}/reply`, { text }),
    start: (connectorId: string, kind: Kind, peer: string, text: string) =>
      call<SendResponse>("POST", "/conversations", { connectorId, kind, peer, text }),
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

export function textLimit(node: NodeDto, kind: Kind): number {
  return kind === "channel" ? node.maxChannelTextBytes : node.maxDirectTextBytes;
}

export function utf8Length(text: string): number {
  return new TextEncoder().encode(text).length;
}

export function errorText(err: unknown): string {
  return err instanceof Error ? err.message : "Something went wrong.";
}

export function ago(iso?: string): string {
  if (!iso) return "—";
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 90) return "just now";
  if (s < 3600) return `${Math.round(s / 60)} min`;
  if (s < 86400) return `${Math.round(s / 3600)} h`;
  return `${Math.round(s / 86400)} d`;
}
