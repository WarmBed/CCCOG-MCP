# CCCG

[English](README.md) | 繁體中文

CCCG(Claude / Codex / Grok)是 Windows 上的本地多 Agent 互通工具組。核心是
**Dispatch MCP**:讓 Claude(Desktop 或 Code)擔任總協調,把工作派給
Grok、Codex 或安全模式的 Claude session——包括人類自己開過的歷史 session——
具備持久化 job、真實送達回執、每次派工可指定模型、以及可熱更新的 worker。

CCCG 的定位是受控測試,不是隱藏「到底是誰回答的」。每個產生的回答都會標注
真實的 provider 與後端模型。CCCG 不修補 Claude 執行檔、不挪用 Claude 憑證、
不繞過計費或認證、不阻擋產品更新、也不會把第三方模型宣稱成 Anthropic 模型。

## 架構

```text
 Claude Desktop / Claude Code(MCP client,總協調)
        |
        |  cccg_list_peers . cccg_inspect_peer . cccg_watch_peers
        |  cccg_dispatch / cccg_dispatch_wait(model?, reasoningEffort?)
        |  cccg_job_status / cccg_job_collect . cccg_inbox_post/list/ack
        v
 +---------------------------+
 | cccg-dispatch.exe         |  MCP Host——持有固定的工具契約
 |  (穩定層,重連才換 schema)|  (改 schema 需要一次 MCP reconnect)
 +---------------------------+
        | 每次工具呼叫:讀 worker-current.json、驗 SHA-256、啟動
        v
 +---------------------------+     %LOCALAPPDATA%\CCCG\dispatch\
 | cccg-dispatch-worker.exe  |     +-- workers\<version>\   (不可變版本)
 |  (版本化,可熱換)          |     +-- worker-current.json  (原子切換)
 +---------------------------+     +-- jobs\<jobId>\        (狀態、prompt、
        |                          |                         stdout、回執)
        |                          +-- bindings\ leases\ owners\ inbox.jsonl
        |
        +--> Peer 目錄:枚舉 Grok / Codex / Claude 的 session 存放區
        |      列表、檢視、watch + 差異比對(found / status / pid)
        |
        +--> Job 存放 + FIFO 租約:依 provider|cwd 跨行程序列化;
        |      背景 job 由 detached worker 執行,MCP Host 死了也活著;
        |      worker PID 死亡 → job 標 failed,不會永久卡住
        |
        +--> Resume / Create 路徑(每輪一次性 CLI)
        |      grok  --model X --reasoning-effort Y  -r <sessionId>
        |      codex exec --json --model X -c model_reasoning_effort=Y resume <id>
        |      claude -p --safe-mode(純文字子 session,無工具/MCP/hooks)
        |
        +--> PATH A Deliver 路徑(CCCG 代管的常駐 live session)
               owner registry(DeleteOnClose 租約 = 崩潰即失效,防殭屍)
                    |
                    v
             run-owner 常駐程序 ---- spool:incoming\ -> processing\ -> receipts\
                    |               回執「只在」provider 該輪跑完後才寫
                    v               (真實送達語意,不是假成功)
             codex app-server(stdin 保持開啟、每輪可帶 model/effort、
                               kill-on-close job object、transport 自動重建)

 --------------------------------------------------------------------------
 分離的唯讀觀測平面(絕不啟動或修改 Claude):

 Claude Desktop 檔案/行程中繼資料
        |  唯讀
        v
 cccg-monitor worker  <-- ready-before-stop 交接 -->  下一版 worker
        ^
        |  cccg-host 監督器(SHA-256 暫存、不可變版本)
```

## Dispatch MCP

| 工具 | 用途 |
|---|---|
| `cccg_list_peers` | 列出 Grok / Codex / Claude session 與綁定 |
| `cccg_inspect_peer` | 檢視單一 session 的標題、模型、cwd、writer 狀態 |
| `cccg_watch_peers` | 對指定 session id 清單拍快照,與上次快照比對差異 |
| `cccg_dispatch` | 排入背景 job,立即回傳 `jobId` |
| `cccg_dispatch_wait` | 保持呼叫開啟,peer 答完自動回傳 |
| `cccg_job_status` / `cccg_job_collect` | 查詢狀態 / 收取正規化回覆 |
| `cccg_inbox_post` / `list` / `ack` | 跨行程共享信箱 |
| `cccg_runtime_status` | 顯示目前生效的 worker 版本與熱更新模式 |

### 每次派工指定模型

`cccg_dispatch` 與 `cccg_dispatch_wait` 接受選配的 `model` 與
`reasoningEffort` 字串,只作用於該輪:

```text
cccg_dispatch(provider="codex", model="gpt-5.6-luna", reasoningEffort="xhigh", ...)
cccg_dispatch(provider="grok",  model="grok-4.6",     reasoningEffort="high",  ...)
```

值會原樣透傳給 provider CLI(`--model`、`-c model_reasoning_effort=` /
`--reasoning-effort`),owner 路徑則帶進該輪的 app-server 參數。不帶 = 用
provider 預設。CCCG 不做別名對映:CLI 接受什麼就填什麼。`provider=claude`
帶任一參數會直接失敗(fail-closed)。詳見 [dispatch](docs/dispatch.md)。

### 送達模型

| Peer 狀態 | 行為 |
|---|---|
| CCCG 代管的 live session(PATH A) | 寫入 owner spool;provider 該輪跑完才有回執 |
| 已關閉 / 可恢復 | 用既有 session id 啟動 provider CLI;Grok resume 以 `num_messages` 回讀驗證 |
| 非 CCCG 代管的 live session | 直接失敗並提示改走代管啟動(鍵盤注入已廢棄) |
| 開啟中的 Claude Desktop session | 走 Desktop 自己的 `send_message`;絕不 CLI-resume |

### 熱更新

Host 持有 MCP 契約;每次工具呼叫都會透過 `worker-current.json` 重新解析
Worker(SHA-256 驗證、不可變版本目錄):

```powershell
.\scripts\install-dispatch-worker.ps1 -Version 0.6.0
```

Worker-only 的變更下一次呼叫就生效,不用重啟。新增或修改 MCP 工具/參數屬於
Host schema,需要一次 MCP reconnect——安裝順序:先 Worker、再 Host、最後
reconnect。

## 建置與測試

```powershell
dotnet build .\src\CCCG.Dispatch.Worker\CCCG.Dispatch.Worker.csproj -c Release
dotnet build .\src\CCCG.Dispatch\CCCG.Dispatch.csproj -c Release
dotnet run --project .\tests\CCCG.Tests\CCCG.Tests.csproj -c Release   # 85 個測試
```

(`CCCG.sln` 連著 experiments 目錄;dispatch 開發請用逐專案建置。)

## 其他平面

- **Monitor**——唯讀 tail Claude Desktop 生命週期/session 中繼資料,內容擷取
  預設關閉,由 `cccg-host` 監督熱交接。見 [monitor](docs/monitor.md) 與
  [hot-update](docs/hot-update.md)。
- **Router(Luna 實驗)**——把一個 Claude 模型別名對映到 Codex app-server 的
  確定性垂直切片,完整揭露 provider;已打包、可回復、不自動安裝。見
  [Desktop Luna experiment](docs/desktop-luna-experiment.md) 與
  [provider adapters](docs/provider-adapters.md)。
- **Bridge shim**——Desktop issue `#86012` 的可回復本地 workaround,維護於
  [experiments/claude-desktop-bridge-shim](experiments/claude-desktop-bridge-shim/README.md)。

## 文件

[dispatch](docs/dispatch.md) ·
[PATH A owned sessions](docs/path-a-owned-sessions.md) ·
[dispatch validation](docs/dispatch-validation.md) ·
[architecture](docs/architecture.md) ·
[monitor](docs/monitor.md) ·
[test plan](docs/test-plan.md) ·
[safety boundaries](docs/safety.md)

## 已知限制

- PATH A owner transport 目前只實作 Codex(app-server);Grok 的 owner
  transport 是 fail-closed 的 stub,待接 ACP 契約;Grok 的 model/effort 只
  作用於 resume/create CLI 路徑。
- Luna 路徑支援文字輪次、中斷/錯誤生命週期與標注;不複製 Claude 的工具、
  hooks、網頁搜尋或 session resume 語意。
- 人類在 CCCG 之外自己開的 live session,在改走代管啟動前收不到派工;
  信箱是後備管道。
