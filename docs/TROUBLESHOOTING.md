# 疑難排解

錯誤訊息刻意籠統(避免成為「某設備/某 Session 是否存在」的探測管道)。**詳細原因只在稽核紀錄裡**,而稽核目前只在伺服器記憶體中、沒有對外的查詢介面,所以排查時請同時看下面各項可能原因。

## 1. 伺服器無法啟動

| 症狀 | 原因與處置 |
|---|---|
| `Invalid configuration: Session:MaxTotalActiveSessions must be at least 1.` 之類 | 設定值不合法;訊息會一次列出所有問題。修正 `appsettings` 或環境變數(`RemoteMesh__...`)。規則見 [CONFIGURATION.md](CONFIGURATION.md) |
| `ForwardedHeaders:Enabled requires at least one entry in KnownProxies or KnownNetworks` | 啟用轉送標頭卻沒列出代理。列出代理位址/網段,或關閉 `Enabled` |
| `... is not an IP address` / `is not CIDR` | `KnownProxies` 必須是 IP,`KnownNetworks` 必須是 `位址/前綴` |
| Windows 服務啟動後立刻停止 | 檢查「事件檢視器 → Windows 記錄 → 應用程式」;常見原因:憑證的私鑰沒有授權給 `NT SERVICE\RemoteMeshServer`、`ASPNETCORE_URLS` 的連接埠被佔用 |
| Linux 服務啟動失敗 | `systemctl status remotemesh-server`、`journalctl -u remotemesh-server -e`;檢查 `/etc/remotemesh/server.env` 與檔案權限 |

## 2. 連線端點(HTTP / WebSocket)

| 症狀 | 原因與處置 |
|---|---|
| 任何端點(`/health` 除外)回 **400 `TLS required.`** | 非開發環境要求 TLS:請求不是 HTTPS(信令 hub 與 Relay 都一樣)。在反向代理後面請確認代理送 `X-Forwarded-Proto: https` 且伺服器設定了 `ForwardedHeaders`(伺服器不會相信沒有列入 `KnownProxies` 的來源所送的標頭)。僅在 TLS 由同一台主機上的代理終止、且不使用 `ForwardedHeaders` 時才考慮 `Security__AllowInsecureTransport=true` |
| Relay 回 **400**(不是上面那種) | 缺少 `sessionId`/`deviceId`/token、格式錯誤、或不是 WebSocket 升級 |
| 任何端點 **429** | 來源位址被限流或鎖定(Relay 連線太頻繁,或 10 分鐘內未授權嘗試過多)。等待,或調整 `Relay:ConnectionsPerMinutePerAddress`/`UnauthorizedAttemptsPerAddress`。**所有使用者同時被鎖** = 位址其實是代理的位址:設定 `ForwardedHeaders` |
| `http://` 沒有自動導向 `https://` | 伺服器只在知道自己的 HTTPS 連接埠時才導向;反向代理部署由代理負責導向(見 nginx 範例的 80 → 443) |
| HTTP 能通但 SignalR 連不上 | 代理沒有轉送 WebSocket 升級標頭,或緩衝/逾時過短。參考 `deploy/nginx.conf.example`;SignalR 會退回 long polling,但延遲較高 |

## 3. 設備註冊

| 訊息 | 可能原因 |
|---|---|
| `Device authentication failed.` | 簽章錯誤(用錯金鑰、挑戰過期超過 30 秒、簽章綁定了不同的連線 ID)、挑戰已用過、**此 DeviceId 已用另一把金鑰註冊過**(公鑰被釘住)。目前沒有設備撤銷/金鑰輪替:重新產生金鑰的設備無法再用原 DeviceId 註冊;伺服器重啟會清空登錄,屆時會重新釘住 |
| 連線被中斷 | 同一連線連續失敗 3 次(`RegistrationFailuresPerConnection`)。重新連線並重新取得挑戰 |
| `Too many failed attempts. Try again later.` | 該來源位址 10 分鐘內失敗 10 次被鎖定 |
| `Invalid device information.` / `Invalid data.` | 欄位格式:DeviceId 必須是 GUID、名稱 ≤ 64 且無控制字元、平台 ≤ 32、公鑰必須是格式正確的 P-256 SubjectPublicKeyInfo、簽章是 base64 |
| `Device is not registered on this connection...` | 呼叫其他方法前沒有完成 `BeginRegistration` + `RegisterDevice`(SignalR 自動重連後需要重新註冊——官方客戶端會自動做) |

## 4. 建立 Session

| 訊息 | 可能原因 |
|---|---|
| `Device is not available.` | **以下任一**:設備 ID 不存在、設備離線、格式錯誤、是自己、目標待處理請求已滿(`MaxPendingRequestsPerTarget`)。刻意不區分 |
| `Too many failed lookups. Try again later.` | 10 分鐘內查詢了太多不存在的設備 |
| `Too many requests. Try again later.` | 超過每分鐘連線請求數 |
| `Too many active sessions.` / `Server is busy. Try again later.` | 每設備或全伺服器的 Session 上限 |
| `Request expired.` | 目標使用者超過 `ApprovalTimeout`(預設 2 分鐘)沒有回應 |
| `Session is no longer pending.` | 已被回應過、已關閉或已逾時 |
| `Session not found.` | Session 不存在,**或你不是它的參與者/目標** |
| `Invalid permissions.` | 權限含未定義位元;信任設備的預設權限含重啟/關機 |
| `Authentication failed.`(OTP) | 代碼錯誤、已用過、已過期(5 分鐘)、目標沒有顯示代碼、錯誤超過 5 次(此時連正確代碼也會被拒,需目標重新產生)。**一律同一個訊息** |
| `Too many authentication attempts. Try again later.` | 該請求方或來源位址的 OTP 嘗試過多 |

## 5. 傳輸與 Relay

| 症狀 | 原因與處置 |
|---|---|
| Relay 關閉 `Unauthorized`(1008) | 任何授權失敗:token 錯誤/**已用過**(單次使用)/**已過期**(預設 2 分鐘)、token 屬於另一台設備、Session 尚未核准或已結束、該角色已有連線。取得新 token(`RequestRelayToken`)再連 |
| Relay 關閉 `Server busy`(1011) | Relay 達到 `MaxConcurrentPairings`;token 未被消耗,稍後重試 |
| Relay 關閉 `Peer timeout` | 30 秒內另一方沒有連上 |
| Relay 一方離開後另一方也斷了 | 設計如此:一方離開 Relay 即結束,雙方都需用新 token 重連(同時進入新的金鑰紀元,若 Session 曾被暫停) |
| 通道上沒有資料、`DroppedFrames` 持續增加 | 金鑰不一致(雙方的紀元或設備金鑰不同)、重放/竄改的封包、或雙方的「發起方」旗標設定相同。請確認雙方使用相同的 SessionId 與紀元,且一方是發起方、另一方不是 |
| 重連後收不到任何資料 | 重連後必須**雙方**都導出新紀元的金鑰並建立新的 `SecureTransport`(見 PROTOCOL.md §3) |
| 一直都是 Relay、沒有直連 | 預期行為:P2P 直連尚未實作(見 NETWORKING.md §1) |
| `ResumeSession` 失敗:`Cannot resume this session.` | token 錯誤/已用過(恢復 token 單次使用,回覆會附上下一個)、你不是參與者、Session 不在 `Paused`。`Recovery window expired.` = 超過 60 秒 |

## 6. 各通道的拒絕原因

**輸入**(`InputDecision`,由 `onDecision` 回報;不記錄內容):`PermissionDenied`(沒有滑鼠/鍵盤權限)、`SessionNotActive`(Session 非 `Connected` 或已到期)、`Stale`(時間戳與被控端相差 > 30 秒:**檢查雙方時鐘**)、`Replay`、`RateLimited`、`InvalidEvent`(座標/按鍵/滾輪超出範圍)、`Malformed`。

**剪貼簿**(`ClipboardResult`):`PolicyDenied` = 被控端本機策略不允許(預設 `Disabled`,需被控端使用者開啟);`PermissionDenied` = 此 Session 沒有 `ClipboardRead`/`ClipboardWrite`;兩者要同時允許才會成功。`Empty`、`TooLarge`、`RateLimited`。

**檔案**(`FileTransferResult`):`InvalidName`(檔名含路徑/保留字/結尾點…)、`Exists`(不覆蓋)、`TooLarge`、`NotFound`、`ChecksumMismatch`(內容損毀,暫存已丟棄,重試會從頭來)、`Busy`(同時傳輸已滿或 transferId 重複)、`PermissionDenied`(需要 `FileUpload`/`FileDownload`)。中斷的傳輸會保留暫存檔,下次相同內容的上傳/下載會自動續傳;明確取消則丟棄。

## 7. 畫面

| 症狀 | 原因與處置 |
|---|---|
| 畫面凍結後恢復 | 有幀遺失(`DroppedFrames`);差分編碼會在下一個週期關鍵幀(預設 2 秒)恢復,或呼叫 `DesktopStreamSender.RequestKeyframe()`(接收端 `KeyframeNeeded` 事件) |
| `FramesAwaitingKeyframe` 增加 | 差分幀無法套用(中途加入或遺失)。送出關鍵幀即可 |
| `FramesCorrupt` 增加 | 資料毀損或編碼器/解碼器類型不符(`Codec` 位元組必須與解碼器一致) |
| 頻寬/CPU 異常高 | 參考編碼器是無損的;全螢幕影片等內容會非常大。需要真實的有損影片編碼器(見 PERFORMANCE.md §3.1) |
| 自訂繪製器顯示亂碼 | `IFrameRenderer.RenderAsync` 收到的像素是租用的,只在呼叫期間有效;要保留請複製 |

## 8. 其他

| 症狀 | 說明 |
|---|---|
| Session 在 8 小時後被結束 | Session 預設有效 8 小時(`ExpiresAt`),到期由背景清掃強制結束 |
| 伺服器重啟後所有 Session 與設備都消失 | 預期:狀態都在記憶體(尚無持久化)。客戶端需要重新註冊 |
| 想查稽核紀錄 | 目前沒有對外的稽核查詢介面,稽核只在記憶體(上限 100,000 筆) |
| 測試偶發失敗 | 先看 [TESTING.md](TESTING.md) §4;效能測試請用 Release 組態,並避免同時執行其他重負載 |

## 9. 回報問題時請附上

版本(`VERSION` 檔)、作業系統、部署方式(直接/代理/容器)、相關設定(**移除祕密**)、完整錯誤訊息與時間點、重現步驟。不要附上私鑰、token 或 OTP。
