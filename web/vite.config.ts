// SPDX-License-Identifier: GPL-2.0-or-later
// Builds one ESM module for Zeus. React is provided by the host, so it is
// external (CONTRIBUTING.md: externalize react and react/jsx-runtime).
import { defineConfig } from "vite";

export default defineConfig({
  build: {
    outDir: "dist",
    emptyOutDir: true,
    minify: false,
    sourcemap: false,
    lib: {
      entry: "src/index.tsx",
      formats: ["es"],
      fileName: () => "meshmessenger.js",
    },
    rollupOptions: {
      external: ["react", "react/jsx-runtime"],
    },
  },
});
