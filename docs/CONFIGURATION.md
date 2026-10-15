# 設定

伺服器的限制值都可以由設定檔或環境變數覆寫。**預設值就是安全的預設值**;設定值不合法時伺服器**啟動失敗**並列出所有問題,而不是悄悄使用不安全的值。

隨附的 `appsettings.json` 列出所有預設值,並有測試確認它與程式碼預設值完全一致(文件所寫即實際行為);另有測試確認設定檔不含任何憑證類欄位。

## 1. 設定來源與優先順序

由低到高:`appsettings.json` → `appsettings.{環境}.json` → 環境變數 → 命令列。

環境變數以 `__` 表示巢狀,例如:

```
RemoteMesh__Session__MaxTotalActiveSessions=5000
RemoteMesh__Relay__MaxBytesPerSecond=13107200
RemoteMesh__ForwardedHeaders__KnownProxies__0=10.0.0.5
```

TimeSpan 以 `d.hh:mm:ss` 表示(`"00:02:00"` = 2 分鐘)。範例見 `deploy/appsettings.Production.example.json`。

**祕密(憑證密碼、連線字串…)不要放進 `appsettings*.json`。** 用憑證存放區(以 Subject 指定,不需要密碼)、環境變數或祕密管理服務。

## 2. `RemoteMesh:Session` — Session 與濫用防護

| 鍵 | 預設 | 說明 |
|---|---|---|
| `ApprovalTimeout` | `00:02:00` | 連線請求等待目標使用者核准的時間;逾時後不能再接受 |
| `MaxPendingRequestsPerTarget` | 5 | 同一目標同時待處理的請求上限(避免被請求淹沒) |
| `MaxActiveSessionsPerDevice` | 8 | 每台設備同時進行的 Session 上限 |
| `MaxTotalActiveSessions` | 10000 | 伺服器全域 Session 上限(Maximum Sessions) |
| `FinishedSessionRetention` | `01:00:00` | 已結束的 Session 在記憶體保留多久 |
| `SessionRequestsPerMinute` | 20 | 每位請求方每分鐘的連線請求數 |
| `UnknownDeviceLookupsPer10Min` | 10 | 每位請求方 10 分鐘內查詢不存在設備的次數;超過後暫時拒絕所有查詢(防列舉) |
| `RegistrationFailuresPerIpPer10Min` | 10 | 同一來源位址 10 分鐘內註冊(簽章)失敗上限,超過則鎖定 |
| `RegistrationFailuresPerConnection` | 3 | 同一連線註冊失敗上限,超過則中斷連線 |
| `RelayTokenLifetime` | `00:02:00` | Relay token 有效期(必須 ≤ 1 小時) |
| `RelayTokenRequestsPerMinute` | 10 | 每個 Session+設備每分鐘可要求新 Relay token 的次數 |
| `MaxCandidateJsonBytes` | 4096 | ICE 候選訊息大小上限 |
| `MaxDeviceNameLength` | 64 | 設備名稱長度上限 |
| `MaxTrustedDevicesPerOwner` | 200 | 每台設備的信任清單上限 |

所有整數項目必須 ≥ 1,所有時間必須為正。

## 3. `RemoteMesh:Recovery` — Session 恢復

| 鍵 | 預設 | 說明 |
|---|---|---|
| `Window` | `00:01:00` | 對端斷線後,Session 保持可恢復的時間 |
| `SweepInterval` | `00:00:05` | 背景清掃頻率(逾時請求、暫停逾時、到期、記憶體回收) |
| `MaxResumeAttemptsPerMinute` | 10 | 每個 Session+設備每分鐘的恢復嘗試(含錯誤 token) |

## 4. `RemoteMesh:Relay` — Relay

| 鍵 | 預設 | 說明 |
|---|---|---|
| `PeerWaitTimeout` | `00:00:30` | 等待另一方連上的時間 |
| `IdleTimeout` | `00:05:00` | 沒有流量多久後關閉 |
| `MaxMessageBytes` | 1048576 | 單一訊息(重組後)上限(≥ 1024) |
| `MaxBytesPerSecond` | 26214400 | 每方向頻寬上限(25 MB/s);`0` = 不限 |
| `ConnectionsPerMinutePerAddress` | 60 | 每個來源位址每分鐘的 Relay 連線數。多人共用一個位址(NAT)的場所請調高 |
| `UnauthorizedAttemptsPerAddress` | 10 | 每個來源位址 10 分鐘內未授權嘗試上限,超過鎖定 |
| `MaxConcurrentPairings` | 10000 | 同時進行的 Relay 配對上限 |

## 4b. `RemoteMesh:Database` — PostgreSQL

沒有設定連線字串時,伺服器把一切放在記憶體(單實例,重啟即忘)。設定後,**設備與公鑰、短 ID 歸屬、信任設備、稽核紀錄、Session、恢復 token(只存雜湊)**存進 PostgreSQL;限流、鎖定計數、OTP、在線狀態仍在記憶體。啟動時用 EF 遷移建立或升級資料表(新增欄位/資料表的版本更新會自動套用;改結構時在 `src/server/RemoteDesktop.Server.Persistence` 執行 `dotnet ef migrations add <名稱>`,測試會檢查模型與遷移是否同步)並載回上次的 Session:連線中的 Session 以「已暫停」回來(恢復時限從重啟起算),客戶端會自動接回;資料庫連不上就拒絕啟動。只支援單一伺服器實例。

| 鍵 | 預設 | 說明 |
|---|---|---|
| `ConnectionString` | 空(= 記憶體) | Npgsql 連線字串。**含密碼,只用環境變數 `RemoteMesh__Database__ConnectionString` 提供**,不要寫進會提交的檔案;格式錯誤時啟動失敗,訊息不重複字串內容 |
| `AuditRetentionDays` | `90` | 每天刪除超過這個天數的稽核紀錄;`0` 永久保留;範圍 0–3650 |

資料庫帳號只需要對自己的資料庫建立/讀寫資料表。`deploy/docker-compose.yml` 已附 PostgreSQL 服務(密碼由 `POSTGRES_PASSWORD` 提供,資料庫不對外開放連接埠)。

## 5. `RemoteMesh:ForwardedHeaders` — 反向代理

預設**關閉**。啟用後只相信列出的代理送來的 `X-Forwarded-For` / `X-Forwarded-Proto`;沒有列出任何代理時伺服器拒絕啟動(否則任何用戶端都能偽造位址,繞過所有按位址的限流與鎖定)。

| 鍵 | 預設 | 說明 |
|---|---|---|
| `Enabled` | `false` | |
| `KnownProxies` | `[]` | 代理的確切 IP,例如 `["10.0.0.5"]` |
| `KnownNetworks` | `[]` | 代理所在網段(CIDR),例如 `["10.0.0.0/24"]` |

啟用後 `X-Forwarded-Proto: https` 也會被採信,因此在 TLS 終止於代理時,伺服器的「必須 TLS」檢查可以通過。

## 6. 其他

| 鍵 | 預設 | 說明 |
|---|---|---|
| `Security:AllowInsecureTransport` | `false` | 非開發環境預設強制 HTTPS(Relay 拒絕非 TLS、其餘導向 HTTPS 並送 HSTS)。只有在 TLS 由同一台主機上的代理終止、且沒有使用 `ForwardedHeaders` 時才考慮開啟 |
| `ASPNETCORE_URLS` | `http://localhost:5000` | 監聽位址 |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` 允許純 HTTP 並啟用 OpenAPI |
| `Kestrel__Certificates__Default__*` | — | Kestrel 的 TLS 憑證(`Subject`+`Store`+`Location` 從憑證存放區取用,不需密碼;或 `Path`/`KeyPath`) |

## 7. 客戶端選項(程式碼層級)

客戶端函式庫的行為由 .NET 選項物件在程式碼中設定(`ClientOptions`)。無介面的 **Agent** 從 `appsettings.json` / 環境變數 `Agent__*` 讀取下表設定,啟動時驗證,不合法就拒絕啟動並列出所有問題。

| `Agent:` 設定 | 預設 | 說明 |
|---|---|---|
| `ServerUrl` | (必填) | 伺服器網址;非 localhost 必須是 https |
| `DeviceName` | 機器名稱 | 1–64 字元 |
| `DataDirectory` | 使用者預設位置 | 設備身分與私鑰的位置 |
| `FileRoot` | 空(關閉) | 遠端檔案傳輸唯一可觸及的資料夾,必須是絕對路徑 |
| `AllowClipboard` | `false` | 允許檢視端使用本機剪貼簿(仍受每個 Session 授予的權限限制) |
| `DirectConnection` | `true` | 連線前先嘗試與對方電腦直連,失敗自動改走 Relay(見 NETWORKING.md) |
| `DirectPort` | `0` | 直連時監聽的 TCP 連接埠;0 = 隨機。要讓外網直接連進來時固定一個埠並轉發 |
| `TrustedPeers` | 空 | 不經人工核准即可連線的設備清單,每筆列出 `DeviceId` 與 `Permissions`(例如 `ViewOnly`、`ViewScreen, ControlMouse`)。**不在清單內的一律拒絕**;不可包含重啟/關機 |

Agent 必須在**登入使用者的工作階段**中執行(例如開機自動啟動的一般程式):Windows 服務跑在 session 0,看不到使用者的螢幕。

桌面程式的 `settings.json` 有對應的 `DirectConnection`、`DirectPort`。程式碼層級另有 `ClientOptions.DirectAdvertisedAddresses`(額外對外宣告的 `host:port`,例如轉發後的公開位址)與 `DirectConnectTimeout`(預設 2 秒)。

| 選項類別 | 重點預設 |
|---|---|
| `KeepAliveOptions` | 間隔 5 秒、逾時 15 秒 |
| `ClientOptions.ReconnectTimeout` | 連線中斷後最長嘗試接回的時間,預設 2 分鐘;超過就結束 Session(原因 `could not reconnect`)。伺服器端的恢復視窗預設 1 分鐘 |
| `ClientOptions.ReconnectGrace` | 只有 Relay 連線斷了、伺服器連線正常時,先等對方自己重建這麼久(預設 2 秒;被控端等兩倍),避免兩端同時換金鑰 |
| 信令連線重試 | 立即、1 秒、2 秒,之後每 2–5 秒(隨機)一直試到客戶端關閉為止 |
| `ReconnectPolicy` | 初始 1 秒、上限 30 秒、倍數 2、抖動 ±20%、最多 10 次、總時限 2 分鐘 |
| `InputGateOptions` | 座標 ≤ 32767、滾輪 ≤ 1200、按鍵 ≤ 4、時鐘偏差 30 秒、重放視窗 4096、1000 事件/秒 |
| `ClipboardHostOptions` | 每 10 秒 20 次請求;剪貼簿策略預設 `Disabled` |
| `FileTransferHostOptions` | 單檔 1 GB、區塊 64 KiB、同時 4 個、下載速率可設 |
| `FileTransferClientOptions` | 未確認區塊 16、回覆逾時 30 秒 |
| `VideoQuality` | 四個檔位(960×540@15、1280×720@20、1600×900@30、1920×1080@60) |
| `NatTraversalConfig` | STUN `stun:stun.l.google.com:19302`、直連逾時 5 秒、候選收集逾時 10 秒 |
