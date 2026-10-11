// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;

// The test runner flips HostValidator.AllowLoopback to reach simulated nodes.
[assembly: InternalsVisibleTo("MeshMessenger.Tests")]
