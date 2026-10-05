// SPDX-License-Identifier: GPL-2.0-or-later
// Zeus loads this ESM module and calls its default export with the public
// plugin API. The panel id must match ui.panels[].id in plugin.json.
import { createClient, type ZeusPluginApi } from "./api";
import { Panel } from "./Panel";
import { injectStyles } from "./styles";

export const PANEL_ID = "meshmessenger";

export default function register(api: ZeusPluginApi): void {
  injectStyles();
  const client = createClient(api);
  const MeshMessengerPanel = () => <Panel client={client} />;
  api.registerPanel({ id: PANEL_ID, component: MeshMessengerPanel });
}
