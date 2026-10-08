# 出貨準備度

> ## 結論:**NOT RELEASE READY(尚未達到可出貨)**
>
> 通訊、安全與工作階段的**基礎層**已完成、經過安全審查與完整測試(557 個測試全部通過)。
> 但產品本身還不能交給使用者:**沒有使用者介面、Agent 只是空殼、沒有真實的螢幕擷取/輸入注入/剪貼簿後端、沒有真實的影片編碼器、P2P 直連未實作、沒有資料庫持久化。**
> 另有一個已知的 P1 安全風險(對方公鑰由伺服器提供,沒有帶外驗證)。
>
> 只有出貨完成定義(§2)的全部條件成立才能標記 RELEASE READY,目前**不成立**,所以不標記。

狀態圖例:✅ 完成並有證據 · 🟡 部分完成(說明缺什麼) · ❌ 未完成

## 1. 發行準備確認項目

| 項目 | 狀態 | 證據與說明 |
|---|---|---|
| Build PASS | ✅ | `dotnet build RemoteDesktop.slnx`:0 警告、0 錯誤;Release 組態同樣通過 |
| Unit Tests PASS | ✅ | 見 §4 |
| Integration Tests PASS | ✅ | `Integration.Tests` 206 個,含伺服器、Relay、設定、Session 恢復 |
| Security Tests PASS | ✅ | 63 個攻擊情境 + 15 個強化單元測試 + 加密通道/金鑰紀元/關鍵幀等;含變異檢查 |
| Acceptance Tests PASS | 🟡 | 7 個驗收測試通過(真實伺服器 + Relay + 加密 + 五個通道 + 權限 + 恢復 + 稽核)。**NAT 驗收只是邏輯模擬,沒有真實 NAT**;沒有真實 OS 後端、沒有真實網路 |
| Documentation Complete | ✅ | README、ARCHITECTURE、SECURITY、PROTOCOL、NETWORKING、DEPLOYMENT、TESTING、TROUBLESHOOTING、THREAT_MODEL + CONFIGURATION、PERFORMANCE、本文件。文件內容已對照程式碼與實測核對 |
| Installer Complete | 🟡 | 伺服器:發布腳本、Windows 服務與 Linux systemd 安裝腳本、Dockerfile/compose。**沒有圖形安裝程式(MSI/EXE)、沒有客戶端/Agent 安裝程式、套件未簽章**;安裝腳本只做了乾跑驗證,Docker 未實際建置(見 DEPLOYMENT.md「驗證狀態」) |
| Configuration Documentation Complete | ✅ | CONFIGURATION.md;預設值與 `appsettings.json` 一致有測試;不合法設定使伺服器啟動失敗有測試。**客戶端沒有設定檔**(Agent 未實作),規劃中的 yaml 設定範例尚未提供 |

## 2. 出貨完成定義(22 項)

| # | 項目 | 狀態 | 說明 |
|---|---|---|---|
| 1 | Device ID system | ✅ | `DeviceId`、身分服務、金鑰儲存(Windows DPAPI;Unix 檔案權限) |
| 2 | Device authentication | ✅ | 註冊時私鑰簽章挑戰、公鑰釘住;**但無撤銷與輪替**(見 §3 P1) |
| 3 | Signaling | 🟡 | 伺服器端完整。**客戶端 `ISignalingClient` 只涵蓋部分操作**(註冊、請求、回應、恢復、心跳);OTP、信任設備、ICE 候選、回報連線模式、要求 Relay token 尚未包進客戶端類別(hub 端都有且有測試) |
| 4 | NAT traversal | ❌ | 只有候選位址收集。**ICE 連線檢查與 SDP 交換未實作**,`TryEstablishDirectAsync` 永遠回傳 `false`;沒有 TURN;沒有真實 NAT 測試 |
| 5 | Relay fallback | ✅ | 伺服器與客戶端傳輸完整,有端到端測試。**目前是唯一的傳輸路徑** |
| 6 | End-to-end encryption | 🟡 | `SecureChannel` 完整(AES-256-GCM、序號、AAD、重放視窗、金鑰紀元)。**缺:前向保密、對方公鑰的帶外驗證;也沒有 Session 協調器強制所有通道都經過它** |
| 7 | Remote screen | 🟡 | 串流管線、差分編碼、適應性畫質控制器完整並有測試。**沒有真實擷取後端,沒有有損影片編碼器**;適應性畫質控制器沒有接上實際的網路量測 |
| 8 | Mouse control | 🟡 | 協定、驗證、權限、重放、限流完整。**沒有 OS 注入實作** |
| 9 | Keyboard control | 🟡 | 同上 |
| 10 | Clipboard | 🟡 | 協定、策略、權限完整。**沒有 OS 剪貼簿實作**;沒有檔案型剪貼簿 |
| 11 | File transfer | ✅ | 分塊串流、續傳、校驗、取消、沙盒、權限、資源上限;256 MB 實測 182–290 MB/s。(沒有 UI) |
| 12 | Permission model | ✅ | 九個權限位元,逐訊息強制,撤銷即時生效 |
| 13 | Audit logging | 🟡 | 事件齊全(含授予的權限)。**只在記憶體、不持久、不防竄改,沒有查詢介面** |
| 14 | Rate limiting | ✅ | 註冊、OTP、連線請求、未知查詢、Relay、恢復等都有限流與鎖定。(單行程記憶體;反向代理需正確設定 `ForwardedHeaders`) |
| 15 | Reconnect | 🟡 | 心跳、退避、網路變更偵測、`ConnectionSupervisor`、伺服器端 Session 恢復、客戶端自動重新註冊與恢復都有測試;驗收測試手動串接並驗證整條路徑。**沒有 Agent 把它們串成自動流程** |
| 16 | Security review | ✅ | Phase 12 內部審查,9 項漏洞已修正,Phase 13/14 又修正多項(見 CHANGELOG)。**沒有外部滲透測試或第三方稽核** |
| 17 | NAT acceptance test | 🟡 | 五種情境以邏輯模擬通過;**未在真實 NAT 後驗證** |
| 18 | Performance test | ✅ | 行程內量測完整(PERFORMANCE.md);**沒有真實網路、真實擷取/編碼、長時間 soak** |
| 19 | Installer | 🟡 | 見 §1 |
| 20 | Documentation | ✅ | 見 §1 |
| 21 | All automated tests passing | ✅ | 557/557;完整執行連續 3 次全過(重構後曾有 1 次整套並行時單一 Integration 測試失敗,未能重現,原因未確認) |
| 22 | No known P0/P1 security issue | ❌ | 有已知 P1:**對方公鑰由伺服器提供,惡意或被入侵的伺服器可做中間人**(§3);另有 P2 項目 |

統計:✅ 10 · 🟡 10 · ❌ 2(另有 Phase 14 確認項目中 Installer、Acceptance 為 🟡)。

## 3. 阻擋項目(依優先順序)

### P0 — 產品無法運作
1. **使用者介面**:`RemoteDesktop.UI` 專案不存在。需要連線畫面、核准對話框(顯示「誰要連線」與要授予的權限)、OTP 顯示、設定、傳輸進度。
2. **Agent 與應用程式組裝**:`AgentWorker` 是 `Task.Delay(Timeout.Infinite)`。需要把註冊、接受請求、建立 Relay、導出金鑰、開啟五個通道、啟動各主機元件、重連與優雅斷線串成完整流程(驗收測試證明這條路徑可行,但那是測試程式碼)。**組裝時必須強制所有通道經過 `SecureTransport`,且每次恢復使用新的金鑰紀元。**
3. **OS 後端**:螢幕擷取(Windows Desktop Duplication/WGC;Linux PipeWire/X11)、輸入注入(`SendInput`;uinput/XTest)、剪貼簿。
4. **真實影片編碼器**:H.264/VP9/AV1(原生函式庫或硬體編碼)。目前無損參考編碼器在一般操作下約 7 Mbit/s(720p@20),全螢幕影片 600+ Mbit/s,遠超各檔位預算。
5. **P2P 直連**:ICE 連線檢查、SDP 交換、`IDirectTransportFactory` 實作;否則所有流量都經過 Relay(成本與擴展性)。
6. **持久化**:`Server.Persistence`(PostgreSQL)為空。目前伺服器只能單實例、重啟遺失所有 Session、稽核不持久。多實例還需要共享的限流/Token/在線狀態。

### P1 — 安全
7. **對方公鑰缺乏帶外驗證**:被入侵的伺服器可替換公鑰而做中間人。需要在核准畫面顯示金鑰指紋,或配對時帶外交換。
8. **設備撤銷與金鑰輪替**:首次註冊釘住公鑰後,換機或私鑰外洩無法重新註冊同一個 DeviceId。
9. **前向保密**:改用成熟協定的臨時金鑰(Noise 或 TLS 1.3 雙向認證),**不要自行設計**。
10. **外部安全審查**:第三方程式碼稽核與滲透測試。

### P2 — 發行品質
11. Unix 私鑰只靠 0600 檔案權限(未整合 libsecret / kernel keyring)。
12. 套件未簽章;Docker 與 systemd 未實際驗證;無 MSI/EXE。
13. 客戶端設定檔、客戶端 `ISignalingClient` 補齊其餘操作。
14. 真實網路與真實 NAT 環境的測試;長時間 soak;fuzzing。
15. 適應性畫質控制器接上實際網路量測;關鍵幀要求接上 Control 通道訊息。

## 4. 測試統計(最近一次完整執行,連續 3 次一致)

| 專案 | 通過 | | 專案 | 通過 |
|---|---|---|---|---|
| Integration | 209 | | Performance | 23 |
| Transport | 77 | | Core | 21 |
| FileTransfer | 68 | | Nat | 10 |
| Security | 45 | | Protocol | 2 |
| Streaming | 44 | | Signaling | 2 |
| Input | 26 | | Clipboard | 25 |
| Server.Application | 5 | | | |
| **合計** | **557** | | **失敗** | **0** |

- 完整執行約 65–70 秒(含效能測試約 55 秒)。
- 建置:0 警告、0 錯誤。
- 已知的測試涵蓋缺口見 [TESTING.md](TESTING.md) §4。

## 5. 已確認可靠的部分

- 授權模型:沒有經過目標核准不可能進入 `Connected`;所有授權決定是原子的;已結束的 Session 不可復活。
- 內容保護:Relay 只看到密文;重放、竄改、反射、跨通道搬移都被擋下;重連後舊流量不可重放。
- 資源:所有接收不可信輸入的元件都有上限;Session 到期由伺服器強制;記憶體不會無限增長。
- 設定:不合法的設定使伺服器啟動失敗;正式環境全站強制 TLS。
- 效能:軟體本身的處理成本很低(微秒到個位數毫秒),大檔案是真正的串流。

## 6. 建議的下一步

1. 決定 P1 安全設計(金鑰指紋驗證、設備撤銷),因為它們會改變註冊與核准的協定,越早決定越便宜。
2. 實作 Agent 組裝與 UI 的最小可用版本(Windows 優先),把驗收測試的流程搬進產品。
3. 接上 Windows 的螢幕擷取、輸入注入、剪貼簿後端。
4. 整合真實影片編碼器。
5. PostgreSQL 持久化與稽核。
6. ICE 直連。
7. 外部安全審查,然後才重新評估是否 RELEASE READY。
