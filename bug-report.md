# Eye Guard 提交后正确性检查报告

**检查范围**：仓库 HEAD（提交 `d5b0455`，"Initial release: Eye Guard 20-20-20 DSH plugin"）。该仓库只有这一次提交，提交时间为 2026-10-31，严格意义上不在最近 24 小时窗口内；因此本次检查针对该提交引入的完整代码（当前工作区未做任何修改）。

**结论**：发现 3 个关键缺陷，均可通过具体路径复现或由代码执行路径确定。以下仅描述问题与触发场景，不包含修复。

---

## 1. [高] 异步 spawn 失败后 helper 永久不再重启，核心功能彻底失效

**位置**
- [index.js](file:///workspace/index.js#L225-L283)（`spawnHelper`）
- [index.js](file:///workspace/index.js#L270-L282)（`error` / `exit` 处理）

**问题**

`spawnHelper()` 用 `if (this.stopped || this.child) return;` 做去重，而只有 `exit` 回调会执行 `this.child = undefined` 和 `scheduleRestart()`：

```js
this.child = child;                       // L256：先赋值
...
child.once('error', (error) => {          // L270：只打日志
  console.error('[eye-guard] helper error:', error);
});
child.once('exit', (code, signal) => {    // L273：唯一的清理 + 重启入口
  if (this.child !== child) return;
  this.child = undefined;
  ...
  this.scheduleRestart();
});
```

但 Node 在 `spawn` 本身失败时（`CreateProcess` 失败，如 `EACCES`/`ENOMEM`/`UNKNOWN`）只会触发 `error` 和 `close`，**不会触发 `exit`**。实测（Node 24，等价代码）：

```
error event: EACCES
close event -13 null
{"errored":true,"exited":false,"closed":true,"hasChildPid":false}
```

于是 `this.child` 永远停留在那个从未启动成功的 `ChildProcess` 上，`spawnHelper()` 之后每次都直接 return，`scheduleRestart()` 也永远不会被调用。

**触发场景**

企业环境常见：`powershell.exe` 文件存在（`existsSync` 通过），但被 AppLocker/WDAC、杀毒软件或主机安全策略禁止启动。首次加载插件时 `spawn()` 异步失败：

- 控制台只有一行 `[eye-guard] helper error: ...`；
- 此后 `helperConnected` 恒为 `false`，设置页显示 "Helper offline"，"Start a break now" 返回 503；
- 即使安全策略随后被放开，或只是瞬时资源不足（`ENOMEM`），也不会再重试，**必须重启整个 DSH 进程**才能恢复。

这与代码中 `restartDelay` 指数退避（2s → 30s）的设计意图直接矛盾：真正需要重试的启动失败路径恰好没有重试。

**影响**：核心功能（护眼休息）永久不可用，且无自动恢复；属于高影响可用性缺陷。

---

## 2. [高] 并发持久化共用同一个临时文件名，可把 `stats.json` / `settings.json` 整个删掉

**位置**
- [index.js](file:///workspace/index.js#L137-L146)（`writeJson`）
- 调用点：[index.js](file:///workspace/index.js#L390-L396)（`persistStats`）、[index.js](file:///workspace/index.js#L382-L388)（`persistConfig`）
- 并发来源：[index.js](file:///workspace/index.js#L357)（`completeBreak` 中的 `void this.persistStats()`）、[index.js](file:///workspace/index.js#L365)（`tickDay`）、[index.js](file:///workspace/index.js#L441)（`/config`）、[index.js](file:///workspace/index.js#L457)（`/stats/reset`）、[index.js](file:///workspace/index.js#L332)（`settings-changed`）

**问题**

```js
async function writeJson(file, value) {
  const temporary = `${file}.${process.pid}.tmp`;   // 同一目标文件只有一个临时名
  await writeFile(temporary, ...);
  try {
    await rename(temporary, file);
  } catch {
    await rm(file, { force: true });                // 失败时先删除正式文件
    await rename(temporary, file);                  // 此时临时文件已不存在
  }
}
```

同进程内对同一目标文件的两次并发写入会共用同一个 `.tmp`：

1. A、B 都完成 `writeFile(temporary)`（B 覆盖 A 的内容）；
2. A 的 `rename` 成功，`temporary` 消失；
3. B 的 `rename` 抛 `ENOENT` → 进入 catch → `rm(file)` **删除刚刚写好的正式文件**；
4. B 再次 `rename` 依旧 `ENOENT`，文件就此消失，只留下一条 "could not save ..." 日志。

用与 `index.js` 完全相同的实现做 50 轮并发写入复现，结果 50/50 次目标文件被删除（`ENOENT`），而非内容损坏：

```
{ ok: 0, missing: 50, corrupt: 0, "existsCheck": "ENOENT" }
```

**触发场景**

`persistStats()` / `persistConfig()` 都是 fire-and-forget 的异步调用，存在自然重叠点：

- 一次休息刚好结束时用户点击设置页的 **Reset statistics**（`completeBreak` 的 `void this.persistStats()` 与 `/stats/reset` 的 `await this.persistStats()` 重叠）；
- 跨零点时 `tickDay()` 与一次休息完成同时发生（两个 `persistStats`）；
- 设置页保存（`/config` 的 `await this.persistConfig()`）与托盘菜单改设置（`settings-changed` 的 `void this.persistConfig()`）同时发生。

**影响**：`stats.json` 或 `settings.json` 被删除。下次启动时 `readJson` 得到 `ENOENT` → 回落到默认值，**今天的休息次数、连续天数（streak）或用户的全部设置静默丢失**，且没有任何用户可见的错误提示（只写 console）。属于数据完整性问题。

---

## 3. [中高] 全屏视频/会议期间，倒数提醒窗口以 5Hz 反复创建销毁（闪烁 + 窗口抖动）

**位置**
- [EyeGuard.cs](file:///workspace/helper/EyeGuard.cs#L392-L403)（`OnTick` 的 warning 分支）
- [EyeGuard.cs](file:///workspace/helper/EyeGuard.cs#L543-L562)（`ShowWarning` / `HideWarning`）

**问题**

```csharp
if (warningActive && fullscreen)
{
    HideWarning();                 // warningActive = false，warning = null
}
else if (config.WarningSeconds > 0 && remaining.TotalSeconds <= config.WarningSeconds)
{
    ShowWarning(remaining);        // 没有 fullscreen 判断，会重新 new WarningWindow()
}
else if (warningActive)
{
    HideWarning();
}
```

两个分支在"全屏 + 进入提醒倒计时"时交替成立，且 `ShowWarning` 在 `warning == null` 时**每次都会新建一个 WPF 窗口并 `Show()`**：

- Tick N：`warningActive=false`，`remaining ≤ warningSeconds` → 新建并显示提醒窗，`warningActive=true`；
- Tick N+1：`warningActive && fullscreen` → 关闭该窗口，`warningActive=false`；
- Tick N+2：再次新建并显示……

`OnTick` 的定时器间隔是 100ms（[EyeGuard.cs](file:///workspace/helper/EyeGuard.cs#L157)），因此在整个 `warningSeconds`（默认 30 秒）内，会以约 5 次/秒的频率创建、显示、关闭一个 `Topmost` 的提醒窗口，共约 300 个窗口。

**触发场景**

完全符合 README 宣称的主场景：用户用浏览器全屏看视频 / 开视频会议（非 `WorkProcesses` 中的应用，`IsFullscreenWindow()` 返回 `true`），在下次休息前 30 秒内。此时左上角提醒框每 100ms 闪现一次，形成明显闪烁；同时反复创建/销毁 WPF 窗口会造成 UI 线程抖动和可感知的卡顿。设计意图本应是"全屏时隐藏提醒、等全屏结束后再休息"，实际行为却是提醒窗在视频上方高频闪烁。

**影响**：用户可感知的严重显示异常（高频闪烁、窗口抖动、CPU 抖动），发生在插件主打的"全屏感知"场景；并伴随持续的窗口创建/销毁资源开销。

---

## 附：检查过但未达到报告门槛的点

- `stop()` 中先把 `this.child` 置空再调用 `writeHelper({type:'shutdown'})`，导致该消息实际不会发出；但紧接着 `child.stdin.end()` 会通过 EOF 触发 helper 的 `Shutdown()`，功能上无影响。
- `break-skipped` 消息被直接赋给 `this.status`，缺少 `phase`/`nextBreakAt`，`snapshot()` 会短暂返回 `phase: 'offline'`；随后 helper 的 `state` 消息会立即覆盖，自愈且不可稳定观测。
- `originAllowed()` 在请求不带 `Origin` 时放行，且仅比较 `Origin.host` 与 `Host`，理论上可被 DNS rebinding 绕过；但该路由由 DSH webServer 提供，是否真正只监听回环地址不在本仓库内，按"具体且可信"的门槛未作为确认缺陷上报。
- 设置页首次拉取失败时停留在 "Loading the eye-break engine…"，错误信息因处于提前 return 的加载分支而不会渲染；属于体验问题，非关键缺陷。
