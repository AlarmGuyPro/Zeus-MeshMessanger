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
  /** MeshCore: how many contacts the node can hold. A full list can't learn new stations. */
  peerCapacity?: number;
  /** MeshCore: whether the node recycles its oldest contact when the list is full. */
  recycleOldestWhenFull?: boolean;
  canAnnounce?: boolean;
  /** True when channels can be joined and left on this node from Zeus. */
  canEditChannels: boolean;
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
  /** The feature's own version, as Zeus loaded it from the manifest. */
  pluginVersion?: string;
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
  /** MeshCore: false when the node heard this station but hasn't saved it as a contact. */
  savedOnNode: boolean;
  /** When our own node last updated this record. More trustworthy than the advert's own timestamp. */
  lastUpdated?: string;
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
  /**
   * Peers a direct conversation could be opened with for this message's sender.
   * Only present on inbound messages in a channel or room thread. Empty means
   * the sender can't be messaged privately — on MeshCore, because a channel
   * message carries no sender key and the name matched no contact.
   */
  senderPeers?: string[];
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

export type LogLevel = "info" | "send" | "receive" | "warning" | "error";

export interface LogEntryDto {
  at: string;
  level: LogLevel;
  text: string;
}

export interface NodeDebugDto {
  id: string;
  name: string;
  network: Network;
  state: NodeState;
  detail?: string;
  host: string;
  port: number;
  identity?: string;
  self?: SelfDto;
  contacts: number;
  contactsHeardOnly: number;
  contactCapacity?: number;
  recycleOldestWhenFull?: boolean;
  channels: number;
  canAnnounce: boolean;
  activity: LogEntryDto[];
  /** How many stations Zeus itself remembers for this node, when it caps them. */
  rememberedPeerLimit?: number | null;
}

export interface HistoryDto {
  conversations: number;
  bytes: number;
  budget: number;
}

export interface DebugDto {
  at: string;
  pluginVersion: string;
  nodes: NodeDebugDto[];
  history?: HistoryDto | null;
}

export interface ActionResultDto {
  ok: boolean;
  error?: string;
  count: number;
}

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
    deleteConversation: (conversationId: string) =>
      api.callBackend("DELETE", `/conversations/${enc(conversationId)}`),
    clearFailed: (conversationId: string) =>
      call<ActionResultDto>("POST", `/conversations/${enc(conversationId)}/clear-failed`),
    retry: (conversationId: string, messageId: string) =>
      call<SendResponse>("POST", `/conversations/${enc(conversationId)}/messages/${enc(messageId)}/retry`),
    refreshPeers: (nodeId: string) => call<ActionResultDto>("POST", `/nodes/${enc(nodeId)}/refresh-peers`),
    // Transmits. Only ever called from an explicit button press.
    announce: (nodeId: string, flood: boolean) =>
      call<ActionResultDto>("POST", `/nodes/${enc(nodeId)}/announce`, { flood }),
    removePeer: (nodeId: string, peerId: string) =>
      call<ActionResultDto>("DELETE", `/nodes/${enc(nodeId)}/peers/${enc(peerId)}`),
    setFavourite: (nodeId: string, peerId: string, favourite: boolean) =>
      call<ActionResultDto>("POST", `/nodes/${enc(nodeId)}/peers/${enc(peerId)}/favourite`, { favourite }),
    /** Clears contacts off the node. `preview` counts them instead, so nothing happens without a confirmation. */
    cleanupPeers: (nodeId: string, what: { repeaters?: boolean; notHeardDays?: number }, preview: boolean) =>
      call<ActionResultDto>("POST", `/nodes/${enc(nodeId)}/peers/cleanup`, { ...what, preview }),
    joinChannel: (nodeId: string, name: string, secret?: string) =>
      call<ActionResultDto>("POST", `/nodes/${enc(nodeId)}/channels`, { name, secret }),
    leaveChannel: (nodeId: string, index: number) =>
      call<ActionResultDto>("DELETE", `/nodes/${enc(nodeId)}/channels/${index}`),
    setRecycleOldest: (nodeId: string, enabled: boolean) =>
      call<ActionResultDto>("POST", `/nodes/${enc(nodeId)}/recycle-oldest`, { enabled }),
    debug: () => call<DebugDto>("GET", "/debug"),
  };
}

export type Client = ReturnType<typeof createClient>;

export const networkLabel: Record<Network, string> = {
  meshtastic: "Meshtastic",
  meshcore: "MeshCore",
};

/** Display names for the kinds the backend sends in lower case. */
export const peerKindLabel: Record<string, string> = {
  chat: "Station",
  repeater: "Repeater",
  room: "Room",
  sensor: "Sensor",
  unknown: "Unknown",
};

export const channelKindLabel: Record<string, string> = {
  public: "Public",
  hashtag: "Hashtag",
  private: "Private",
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
