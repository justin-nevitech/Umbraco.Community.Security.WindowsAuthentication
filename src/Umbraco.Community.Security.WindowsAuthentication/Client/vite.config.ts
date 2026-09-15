import { defineConfig } from "vite";

export default defineConfig({
  build: {
    lib: {
      entry: "src/windows-authentication.ts",
      formats: ["es"],
    },
    outDir: "../wwwroot/App_Plugins/WindowsAuthentication",
    emptyOutDir: true,
    sourcemap: true,
    rolldownOptions: {
      output: {
        // Content-hash entry point filename for cache busting
        entryFileNames: "windows-authentication-[hash].js",
      },
    },
  },
});
