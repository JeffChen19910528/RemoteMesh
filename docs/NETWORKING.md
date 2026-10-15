# 網路

## 1. 連線模型

```
Device ID → 信令 → 認證 → NAT 穿透 → 直連 → Relay 備援 → 加密的遠端工作階段
```

使用者不選擇連線方式:`TransportManager` 先嘗試直連(ICE),失敗、逾時或直連傳輸無法建立,都自動改走 Relay。

**現況**

| 項目 | 狀態 |
|---|---|
| 直連(TCP) | **已實作**:被控端監聽一個 TCP 埠,經信令把可到達的位址(區網位址、`DirectAdvertisedAddresses`)告訴控制端;控制端逐一連線,成功就用直連。直連之上跑的是與 Relay 路徑完全相同的 TLS(雙向驗證釘選的裝置金鑰、前向保密)與端到端 AES-GCM,所以埠被陌生人連上也拿不到任何東西 |
| 回退 Relay | 已實作:對方沒開直連、位址連不上、握手失敗、信令訊息遺失,都改走 Relay,每次嘗試最多多花幾秒;斷線重連時同樣先試直連 |
| 候選位址收集(host / server-reflexive,STUN,SIPSorcery) | 已實作(`SipSorceryNatTraversal`),但目前直連路徑不依賴它 |
| NAT 打洞 / TURN | 不提供:兩台電腦都在不同 NAT 後面時,直連連不上,自動使用 Relay。Relay 是應用層的 WebSocket 中繼,不是 TURN |

什麼時候會是直連:兩台電腦在同一個區網、其中一台有公開位址、或被控端把 `DirectPort` 轉發出來並用 `DirectAdvertisedAddresses` 宣告。其他情況走 Relay(頻寬與容量規劃請以此為準)。連線模式(`Direct` / `Relay`)會在 `ViewerSession.ConnectionMode` / `HostSession.ConnectionMode` 顯示,並回報給伺服器。

信令:直連位址交換使用既有的 `SendIceCandidate` hub 方法(一則 JSON:`offer` 帶位址、`relay` 表示改走 Relay,都標明金鑰紀元);伺服器只轉送,每個 Session 每分鐘最多 200 則。

## 2. NAT 驗收測試

驗收涵蓋 Open NAT、Full Cone、Restricted、Symmetric、Relay Fallback。`NatScenarioTests` 與 `NatTraversalTests` 以 `LoopbackNatTraversal`(可設定直連是否成功、是否逾時)**模擬**每種結果,驗證 `TransportManager` 在各情境選對傳輸模式。

**這些是邏輯模擬,不是對真實 NAT 的驗證。** 它們證明選擇與備援邏輯正確,不證明候選收集、打洞或實際傳輸在真實 NAT 後面可行。實體 NAT 實驗室(多種路由器/CGNAT)測試仍未做。

## 3. Relay

- 端點 `/ws/relay`(WebSocket),協定細節見 [PROTOCOL.md](PROTOCOL.md) §2。
- Relay 只轉送二進位訊框,不解析、不解密、不記錄內容;授權依 Session 狀態、設備角色與單次使用 token。
- 容量與限制:每方向 25 MB/s、單訊息 1 MB、閒置 5 分鐘、等待對端 30 秒、同時配對上限 10,000、每來源位址每分鐘 60 次連線(皆可設定,見 [CONFIGURATION.md](CONFIGURATION.md))。
- 一方離開,整個 Relay 就結束(另一方也被關閉)。重新連線要先以 `RequestRelayToken` 取得新的單次使用 token。

## 4. 連接埠與防火牆

| 方向 | 連接埠 | 用途 |
|---|---|---|
| 客戶端 → 伺服器 | TCP 443(或自訂 HTTPS 連接埠) | 信令 hub(WebSocket/long polling)、Relay WebSocket、`/health` |
| 客戶端 → STUN | UDP 19302(預設 `stun.l.google.com`) | 候選位址收集 |
| 反向代理 → 伺服器 | TCP 5222(預設,可設定) | 代理後端 |

客戶端不需要開放任何入站連接埠。企業網路若只允許 HTTP/HTTPS,Relay 路徑仍可運作。

## 5. 重連與網路變更

- **心跳**:Control 通道上每 5 秒 ping,15 秒無任何流量判定中斷(`KeepAliveOptions`)。
- **本機網路變更**(`NetworkChangeDetector`):比對網路介面與 IP 快照,區分 IP 變更、介面變更(Wi-Fi ↔ 乙太網路)、斷網、復原。介面/IP 變更時 `ConnectionSupervisor` 立即重試(連線遷移);網路復原時喚醒退避等待。
- **退避**:1 秒起、倍數 2、上限 30 秒、±20% 抖動;最多 10 次、總共 2 分鐘(`ReconnectPolicy`)。
- **伺服器端恢復**:對端斷線時 Session 轉為 `Paused`,60 秒內可用單次使用的恢復 token 回復並進入新的金鑰紀元;逾時則 `Expired`。
- **信令客戶端**:SignalR 自動重連後會重新註冊(挑戰簽章)並嘗試恢復持有恢復 token 的 Session。

目前這些元件各自完成並有測試,**但還沒有 Agent/應用程式把它們串成完整的自動重連流程**;驗收測試以手動方式串接並驗證整條路徑可行。

## 6. 時間與時脈

- 輸入事件的時間戳與被控端時鐘相差超過 30 秒會被拒絕(`Stale`);兩端時鐘嚴重不同步會導致輸入全部被丟棄。請確保 NTP。
- 伺服器端的逾時以伺服器自己的 UTC 時鐘計算(`TimeProvider`,測試可替換),與客戶端時鐘無關。這是牆上時鐘而非單調時鐘:伺服器系統時間被大幅調整時,到期判斷會跟著變;少數地方(例如 OTP 有效期、`SessionInfo.IsExpired`)仍直接讀系統時間。生產環境請讓伺服器時間同步且不要手動大幅調整。

## 7. 反向代理

見 [DEPLOYMENT.md](DEPLOYMENT.md) §5 與 `deploy/nginx.conf.example`。重點:WebSocket 升級標頭、關閉緩衝、長逾時、覆寫 `X-Forwarded-For`、設定 `RemoteMesh:ForwardedHeaders`。
