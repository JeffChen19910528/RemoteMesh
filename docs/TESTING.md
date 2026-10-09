# 測試

## 1. 執行

```bash
dotnet test RemoteDesktop.slnx                                   # 全部(約 70 秒)
dotnet test tests/RemoteDesktop.Core.Tests                       # 單一專案
dotnet test tests/RemoteDesktop.Core.Tests --filter "FullyQualifiedName~SessionIdTests"   # 單一測試
dotnet test tests/RemoteDesktop.Performance.Tests -c Release     # 效能數字要用 Release
```

資料庫測試(`Server.Persistence.Tests`,含重啟後還原 Session 與 token)需要一個測試用的 PostgreSQL:把 `RM_TEST_PG` 設成它的連線字串(例如 `Host=localhost;Port=5432;Username=postgres;Password=...`),每個測試會建立並刪除自己的資料庫;沒設定時這些測試會被略過(不算通過)。起一個拋棄式的:`docker run -d --name rm-pg -e POSTGRES_PASSWORD=<密碼> -p 5432:5432 postgres:17-alpine`。

效能測試專案的匯出選項:`RM_PERF_OUT=<檔案>` 把量測結果寫成表格;`RM_PERF_FILE_MB=<大小>` 調整大檔案測試(預設 256)。

## 2. 策略與專案

七類測試對應如下:

| 類型 | 專案 | 內容 |
|---|---|---|
| Unit | `Core.Tests`、`Protocol.Tests`、`Security.Tests`、`Input.Tests`、`Clipboard.Tests`、`Signaling.Tests` | `DeviceId`/`SessionId`/`SessionInfo`/`PermissionSet`/稽核事件、協定版本、金鑰/簽章/AES-GCM/AAD/OTP/Token、輸入驗證與權限、剪貼簿策略與權限 |
| Protocol | `Streaming.Tests`、`FileTransfer.Tests`、`Input.Tests`、`Clipboard.Tests` | 封包化/重組/重放、各通道訊息的解析與拒絕畸形輸入、差分編碼的關鍵幀與丟幀恢復、檔案分塊與校驗 |
| Network | `Transport.Tests`、`Nat.Tests` | 加密通道(重放、竄改、反射、跨通道)、心跳、重連退避、網路變更偵測、連線狀態機、傳輸抽象 |
| Integration | `Integration.Tests` | 真實伺服器(`WebApplicationFactory`):信令、認證與授權、Session 恢復、Relay、設定、資源上限 |
| Security | `Integration.Tests`(`SecurityAttackTests`、`HardeningUnitTests`、`SessionRecoveryTests`…)、`Security.Tests`、`Transport.Tests` | 攻擊情境:冒用、重放、劫持、列舉、暴力破解、洪水、Relay 濫用、畸形輸入 |
| Performance | `Performance.Tests` | 延遲、FPS、CPU、記憶體、頻寬、大檔案、多 Session、伺服器負載(門檻寬鬆,抓退化) |
| Acceptance | `Integration.Tests`(`AcceptanceTests`) | 完整產品路徑:設備認證 → 核准/OTP/信任設備 → Relay → 加密 → 五個通道 → 權限 → 恢復 → 稽核 |

測試專案共 18 個(含 `Server.Persistence.Tests`)。最近一次完整執行:**全部通過、0 失敗**(見 RELEASE_READINESS.md 的數字)。

## 3. 重點涵蓋

- **狀態機**:`ConnectionStateMachine` 的轉換表逐一核對全部 (來源, 目標) 組合;Session 狀態以 compare-and-set 轉換,有並發競態測試。
- **攻擊測試是「扮演攻擊者」**:每個測試都斷言攻擊失敗**且受害者狀態未被改動**。
- **變異檢查**:Phase 12 時,把三個關鍵防護(狀態檢查、參與者檢查、公鑰釘住)各自暫時拿掉,確認對應的攻擊測試會失敗,之後還原。新增防護時建議做同樣的事,證明測試真的在守護它。
- **重放與金鑰紀元**:有「對照實驗」測試,證明沒有紀元時重連後的重放會成功,有紀元則被拒絕。
- **設定**:測試確認 `appsettings.json` 與程式預設一致、不合法的值使伺服器啟動失敗、隨附設定檔不含憑證類欄位。
- **資源上限**:重組器大小與總量、Relay 容量、全域 Session 上限、到期與記憶體回收。
- **端到端**:驗收測試包含「像素完全一致的畫面經 Relay 加密傳輸並解碼」。

## 4. 限制(請務必了解)

1. **NAT 測試是邏輯模擬**:`LoopbackNatTraversal` 可設定直連成功/失敗/逾時,驗證選擇與備援邏輯;**沒有真實 NAT 後面的測試**。見 [NETWORKING.md](NETWORKING.md) §2。
2. **全部在單一行程內以記憶體傳輸執行**:沒有真實網路的延遲、封包遺失與重新排序;沒有 TLS。
3. **OS 後端沒有被測試**:螢幕擷取、輸入注入、剪貼簿只有介面與測試用實作。
4. **Unix 專屬行為**:私鑰檔案權限的測試只在 Unix 上做斷言(開發機是 Windows,該測試在 Windows 上直接通過而不斷言);請在 Linux CI 上確認。
5. **Linux 與 macOS 後端**:`Platform.Tests` 裡「需要真實 X 伺服器」的測試在 Windows 上略過,在 Linux 上以 Xvfb 執行(`xvfb-run dotnet test tests/RemoteDesktop.Platform.Tests`,0 略過);已在 WSL(Ubuntu 24.04)上跑過。**macOS 的後端只有邏輯測試(鍵碼表、座標與縮放、剪貼簿指令),擷取與事件注入沒有在 Mac 上執行過。**
5. **效能測試的絕對數字隨機器而異**,門檻只用來抓明顯退化。
6. **沒有長時間 soak 測試、沒有模糊測試(fuzzing)、沒有外部滲透測試。** 畸形輸入有針對性的案例測試,但沒有系統性的 fuzz。
7. 部分 Windows 專屬的 TestServer WebSocket 拆除時序曾造成偶發失敗(已修正並加測試);如再出現與 `TestWebSocket` 釋放有關的偶發失敗,請先檢查拆除順序。

## 5. 新增測試的慣例

- 每個新功能都要有測試;安全相關變更要有「攻擊者視角」的測試與變異檢查。
- 需要時間的邏輯注入 `TimeProvider`,不要用 `Task.Delay` 等待真實時間(確定性)。
- 不要把效能門檻設得貼近目前數字;以抓退化為目的。
- 不要刪除或弱化既有測試來讓建置通過。
