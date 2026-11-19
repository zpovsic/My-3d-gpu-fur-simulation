<!--VITE PLUS START-->

# Using Vite+, the Unified Toolchain for the Web

This project is using Vite+, a unified toolchain built on top of Vite, Rolldown, Vitest, tsdown, Oxlint, Oxfmt, and Vite Task. Vite+ wraps runtime management, package management, and frontend tooling in a single global CLI called `vp`. Vite+ is distinct from Vite, and it invokes Vite through `vp dev` and `vp build`. Run `vp help` to print a list of commands and `vp <command> --help` for information about a specific command.

Docs are local at `node_modules/vite-plus/docs` or online at https://viteplus.dev/guide/.

## Built-in Commands vs Scripts

`vp <name>` runs a built-in command. `vp run <name>` runs a `package.json` script or a `vite.config.ts` task. Scripts cannot overwrite built-ins, so `vp dev` and `vp run dev` may do different things. Check `package.json` and `vite.config.ts` first, and run `vp run <name>` when the project defines a script or task with that name.

## Running Scripts

Run TypeScript and JavaScript files with `vpx <file>`, for example `vpx ./scripts/seed.ts --dry-run`. It handles enums, decorators, JSX, and tsconfig `paths`, so do not add `tsx` or `ts-node` to run scripts. `vpx --watch <file>` reruns the file when a file it uses changes, in place of `tsx watch`. `vp node <file>` runs plain Node.js.

## Tool Versions

Run `vp toolchain` to show versions and relationships in the active Vite+
release. Add a tool name to select part of the graph. For example, run
`vp toolchain vite`. Use `--global` to ignore the local `vite-plus` package. Use
`vp why <package>` to show the package-manager dependency graph.

## Review Checklist

- [ ] Run `vp install` after pulling remote changes and before getting started.
- [ ] Run `vp check` and `vp test` to format, lint, type check and test changes.
- [ ] Check if there are `vite.config.ts` tasks or `package.json` scripts necessary for validation, run via `vp run <script>`.
- [ ] If setup, runtime, or package-manager behavior looks wrong, run `vp env doctor` and include its output when asking for help.

<!--VITE PLUS END-->

## Learned User Preferences

- Keep HTML free of inline `style` attributes; place styling rules in `style.css`.
- Default the Mochi-Puff desktop pet to stationary placement where dragged (roaming off by default, toggleable from the context menu), with interactive monster sound effects enabled and auto-start/desktop shortcut support.
- Keep `package.json` scripts and hooks compatible with Windows PowerShell (avoid bash-only syntax such as `[ -d ... ]`).

## Learned Workspace Facts

- Run `vp build` before `vp run start` (`node server.js` on port 3000 serves `dist/` when present); use `vp run dev` for hot-reload development.
- `index.html` intentionally hides the grass-painter navigation pill via `style.css` (`#page-nav a[href*="grass.html"]`) and the `hidden` attribute; the grass scene remains accessible directly at `/grass.html`.
- The native Windows desktop pet wrapper lives in `desktop/` (.NET 10 WinForms + WebView2 with DWM per-pixel alpha transparency), loads the built web app in `?desktop=1` mode, and stores settings in `%LOCALAPPDATA%\DesktopMochi\settings.json`.
- Build the desktop pet with `vp run desktop:build` (`vp build && dotnet build desktop/DesktopMochi.csproj -c Release`) and launch it with `vp run desktop`; stop any running `DesktopMochi` process before rebuilding the executable.
- Interactive creature sound effects are procedurally synthesized with the Web Audio API in `src/monsterAudio.js` without external audio assets, and WebView2 initializes with `--autoplay-policy=no-user-gesture-required`.
- Environment secrets such as `GEMINI_API_KEY` belong in `.env.local` (excluded by `.gitignore`).
