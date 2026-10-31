# Eye Guard

A strict 20-20-20 eye-break plugin for the DeepSeek Harness desktop app.

Eye Guard stays above normal Windows applications, dims every monitor at break time, and keeps a tray countdown. It is deliberately strict: there is no snooze, no pause, and no close button during a break. Hold **Esc for 3 seconds** for the emergency exit.

## What it does

- **20-minute focus rhythm** by default, configurable from 1 to 120 minutes.
- **20-second full-screen break** by default, configurable from 5 to 300 seconds.
- **30-second corner countdown** before the full-screen overlay (configurable, or set to 0). It appears in the top-left corner and does not take keyboard focus.
- **Topmost overlay on every monitor**, with a dark frosted card and a soft glow.
- **Full-screen aware**: a break is postponed while a video, meeting, game, or other non-work full-screen window is in front, then taken once when it ends. Common editors and IDEs are excluded so coding full-screen does not suppress breaks.
- **Idle aware**: five minutes without input silently restarts the focus clock (configurable).
- **Tray countdown** with the next-break time, today’s break count, streak, quick interval choices, a **Start a break now** action, and high-contrast/reduced-motion switches.
- **Local statistics**: today’s completed breaks and a day streak, stored under `%USERPROFILE%\.dsh\eye-guard\`. The settings page also offers **Reset statistics**.
- **DSH settings page**: Settings → **Eye Guard** edits the rhythm and comfort options and shows live status.
- **No network, no camera, no telemetry.**

## Install

The package is a local DSH bundle. Clone or download it, then install it through Plugin Manager with the absolute directory path:

```text
<path-to-dsh-eye-guard>
```

or ask the agent to run `plugin_manager` with `install_bundle` for that path. Disabling the **eye-guard** row in DSH stops the helper; re-enabling it starts a new one. The package name intentionally stays `@local/dsh-eye-guard` because it is installed as a local bundle; fork and rename it if you want to publish it to npm.

## Use

- The tray icon tooltip shows the next break (`Eye Guard — next break 19:42`).
- Double-click the tray icon to start a break immediately.
- The tray menu carries the common settings; the full form is in DSH Settings → Eye Guard.
- During a break, the overlay cannot be closed with the mouse, Alt+F4, or Alt+Tab. Hold Esc for three seconds to end it early. An emergency exit is not counted as a completed break.
- At the start of a break one soft “ding” plays. There is no sound at the end.

## Data and privacy

`%USERPROFILE%\.dsh\eye-guard\settings.json` stores the rhythm. `stats.json` stores the local date, today’s count, and the streak. Both are plain local files; nothing is uploaded. The helper reads only the Windows last-input time and the foreground window geometry; it does not inspect window contents, capture the screen, or access the camera.

## Windows helper design

The native layer is a WPF/WinForms helper hosted by the signed Windows PowerShell process. `helper/EyeGuard.ps1` compiles `helper/EyeGuard.cs` in memory with `Add-Type` and runs it. This avoids shipping an unsigned executable, which Windows code-integrity policies can block, and needs no administrator rights.

The helper is a child of the DSH Host. It reads JSON lines on stdin (`init`, `config`, `stats`, `break-now`, `shutdown`) and writes state, completion, and settings-change events on stdout. The Host exposes a loopback-only `/eye-guard/api` route for the settings page.

## Limitations

- Windows 10/11 only. The desktop app must be running; Eye Guard does not install a Windows service or a login autostart entry.
- The overlay is above normal windows. An exclusive-mode DirectX game or a protected full-screen surface may cover it.
- Full-screen detection is heuristic: it uses the foreground window’s bounds and process name. Browser-based meetings and video are postponed; common editors/IDEs are treated as work and still get the break.
- The tray icon can live in the Windows notification overflow area if the taskbar is full.

## Files

```text
package.json          DSH bundle manifest and client declaration
cordis.patch.yml      inserts the eye-guard row
index.js              Host half: helper lifecycle, local API, stats
client.js             DSH settings page
locale/en.json        Plugin Manager title and description
icon.svg              Original Eye Guard icon
helper/EyeGuard.cs    WPF/WinForms helper source
helper/EyeGuard.ps1   Signed-PowerShell host for the helper
README.md             This file
```

## License

MIT. See [LICENSE](LICENSE).
