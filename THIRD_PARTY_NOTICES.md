# Third-party notices

Mesh Messenger is licensed under GPL-3.0-or-later (see `LICENSE`). It contains
no copied third-party source files. The items below are what it is derived
from or built against.

## Zeus plugin contracts

- `sdk/Zeussdr.Zeus.Plugins.Contracts/` is an unmodified vendored copy of the
  public Zeus SDK contracts from
  <https://github.com/Zeus-SDR/zeus-community-features> (commit `453f360`),
  licensed GPL-2.0-or-later. Used here under GPL-3.0, as that licence permits.
  The contracts assembly is provided by Zeus at runtime and is not shipped in
  the feature package.

## Meshtastic protocol

- The Meshtastic connector's hand-written protobuf encoder/decoder implements
  message and field definitions from
  <https://github.com/meshtastic/protobufs> (commit `95c5f8c`), licensed
  GPL-3.0. No generated code or `.proto` files are included; the field numbers
  and wire framing were transcribed into our own code.
- Connection handling was written after reading the reference client
  <https://github.com/meshtastic/python> (commit `0a18357`), licensed GPL-3.0.
  No code was copied.

## MeshCore protocol

- The MeshCore connector implements the companion-radio protocol documented in
  and implemented by <https://github.com/meshcore-dev/meshcore>
  (commit `a366955`), licensed MIT, Copyright (c) 2025 Scott Powell /
  rippleradios.com. No code was copied.
- Command usage was cross-checked against
  <https://github.com/meshcore-dev/meshcore-cli> (commit `a43041f`), licensed
  MIT, Copyright (c) 2025 fdlamotte. No code was copied.

## Build tools (not shipped)

The panel is built with Vite and TypeScript (MIT / Apache-2.0); see
`web/package-lock.json`. React is supplied by Zeus at runtime and is not
bundled.
