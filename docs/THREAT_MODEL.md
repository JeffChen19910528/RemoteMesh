# RemoteMesh 威胁模型與安全審查(Phase 12)

本文件分析 15 類威脅,並記錄 Phase 12 安全審查發現的漏洞、已修正項目與殘留風險。
每項防護都標出實作位置與對應的攻擊測試(`SecurityAttackTests` 等),測試以「扮演攻擊者」的方式驗證攻擊失敗且受害者狀態未被改動。

## 1. 範圍與資產

| 資產 | 說明 | 保護方式 |
|---|---|---|
| 裝置私鑰 | 證明「我是這個 DeviceId」 | 只在簽章器(`IDeviceSigner`)內使用,不上傳、不記錄 |
| Session 與 token | 授權一次遠端連線 | 短效、綁定 Session 與設備、單次使用、可撤銷 |
| 螢幕、輸入、剪貼簿、檔案 | 遠端工作階段內容 | 端到端 AEAD 加密(`SecureChannel`),Relay 只見密文 |
| 使用者同意 | 目標端使用者是否允許連線 | 狀態機 compare-and-set,逾時失效 |
| 伺服器可用性 | 簽發、配對、轉送 | 速率限制、數量上限、記憶體上限 |

信任邊界:**裝置 ↔ 信令伺服器 ↔ Relay ↔ 裝置**。伺服器與 Relay 視為「誠實但可能被入侵」;網路視為完全不可信。

## 2. 威脅分析

### 2.1 Device Impersonation(冒用設備)
- **攻擊**:知道或猜到某 DeviceId 後,用自己的連線註冊同一個 ID,接收原設備的連線請求,或冒用信任設備的自動核准。
- **審查發現**:原本 `RegisterDevice` 不驗證任何憑證,任何人都能覆寫設備的公鑰與連線映射。**(嚴重,已修正)**
- **防護**:`BeginRegistration` 發出隨機 32 位元組挑戰,設備以私鑰簽署「標籤 + 挑戰 + DeviceId + 連線 ID」(`RegistrationProof`),伺服器以公鑰驗證。挑戰單次使用、30 秒失效、綁定連線。首次註冊釘住公鑰(TOFU),之後必須同一把金鑰簽章(`DeviceAuthenticator`)。公鑰必須是格式正確的 P-256。
- **測試**:`Impersonation_*`、`Replay_*`、`Registration_*`、`TrustedDevice_Impostor_*`。
- **殘留**:見 §4(金鑰輪替、首次註冊信任)。

### 2.2 Session Hijacking(劫持 Session)
審查發現並修正:
1. `RespondToSession` 不檢查狀態:已關閉的 Session 可被目標端「接受」而復活並簽發新 token。**(高,已修正)**
2. `ReportConnectionMode` 不檢查狀態:請求方可在目標未核准前自行把 Session 設為 Connected,並(Phase 11 起)取得恢復 token,繞過連線核准。**(嚴重,已修正)**
3. `CloseSession` 不檢查是否為參與者:任何已註冊設備都能撤銷並終止別人的 Session。**(高,已修正)**
4. `SendIceCandidate` 對非參與者仍會把候選送給請求方。**(中,已修正)**
5. 信令把「請求方的 token」同時發給目標端;Relay 靠到達順序配對,第三個連線會被配對到第一個。**(高,已修正)**

- **防護**:所有授權決定用 `TryTransitionAsync`(compare-and-set),只有一個請求能成功,已結束的 Session 不可復活;每個操作先確認呼叫者是參與者,「不存在」與「不是你的」回覆相同錯誤;核准逾時(預設 2 分鐘)後無法再接受,並由背景清掃失效;每一方各拿自己綁定設備的 token;Relay 依角色(請求方/目標)配對,每個角色只能附著一次。
- **測試**:`Hijack_*`、`Relay_*`。

### 2.3 MITM(中間人)
- **防護**:傳輸層必須 TLS(非開發環境所有端點除 `/health` 外都強制 HTTPS,並送 HSTS;信令與 Relay 皆然);工作階段內容另有端到端加密,Relay 與任何路徑上的第三方只看到密文(`SecureChannel`,AES-256-GCM,金鑰由雙方設備金鑰 ECDH + HKDF 導出)。
- **審查發現**:`ISessionCrypto` 原本未接到任何通道,Relay 實際轉送明文。**(高,已修正:新增 `SecureChannel`/`SecureTransport`)**
- **測試**:`SecureChannelTests`、`EndToEnd_ThroughRelay_*`。
- **殘留**:見 §4(公鑰來源與指紋驗證)。

### 2.4 Replay Attack(重放)
- **註冊**:挑戰單次使用、綁定連線與時間。
- **通道內容**:每個方向有遞增序號,序號、通道類型、方向都納入 AEAD 關聯資料;64 項滑動視窗拒絕重複與過舊的封包;驗證通過後才記錄序號(偽造封包不能消耗真實序號);方向位元防止反射;通道類型綁定防止把封包移到別的通道。
- **Token**:單次使用(`TryConsume`),恢復 token 在每次恢復時輪替。
- **輸入事件**:`InputGate` 的事件 ID 重放視窗與時間戳偏差檢查(Phase 8)。
- **測試**:`SecureChannelTests`(重放、亂序、過舊、反射、跨通道)、`Replay_*`、`Hijack_RecoveryToken_IsSingleUse_AndRotatesOnResume`、`Token_ConsumeUnderRaces_HasExactlyOneWinner`。

### 2.5 Brute Force(暴力破解)
- **OTP**:6 位數、5 分鐘失效、單次使用;每個目標最多 5 次嘗試(換新裝置 ID 也無法繞過);每個請求方與每個來源位址各有額外限制;比較使用常數時間;錯誤訊息一律為「Authentication failed.」,不洩漏是否有 OTP、是否過期。
- **註冊**:同一連線連續失敗 3 次即中斷;同一來源位址 10 分鐘內失敗 10 次即鎖定(Failed Login Lockout)。
- **Token 猜測**:32 位元組隨機,不可猜;Relay 另對來源位址的失敗次數鎖定(10 次/10 分鐘 → HTTP 429)。
- **測試**:`Otp_*`、`BruteForce_*`、`Relay_BadRequests_*`。
- **殘留**:OTP 5 次失敗後即鎖定直到目標重新產生,對方可藉此故意讓正常使用者的 OTP 失效(可用性換安全性)。

### 2.6 Device Enumeration(列舉設備 ID)
- **防護**:未知、離線、格式錯誤、自己、待處理上限已滿,全部回覆同一個錯誤「Device is not available.」;對未知 ID 的查詢計入額度(10 次/10 分鐘,超過後連有效目標也暫時拒絕);每位請求方每分鐘最多 20 次連線請求。
- **測試**:`Enumeration_*`、`Flooding_*`。

### 2.7 Relay Abuse(濫用 Relay)
- **防護**:連線需要有效 token(單次使用、2 分鐘失效、綁定 Session 與設備);Session 必須存在、已核准且仍有效(`EstablishingTransport`/`Connected`);設備必須是請求方或目標,每個角色一個連線;等不到對端 30 秒逾時;閒置 5 分鐘關閉;單一訊息上限 1 MB;每方向 25 MB/s 頻寬上限;只接受二進位訊框;Session 關閉或過期時立即斷開 Relay;來源位址連線速率 60/分鐘;Relay 開始與結束寫入稽核。
- **測試**:`Relay_*`。

### 2.8 DoS
- **防護**:SignalR 訊息大小上限 64 KB;每位請求方的待處理與進行中 Session 上限;速率限制器、稽核日誌、token 表都有記憶體上限或過期清理(攻擊者選擇的鍵不能讓記憶體無限成長);Relay 接收緩衝區有上限(背壓);大小與欄位長度驗證。
- **殘留**:沒有全域連線數上限與網路層(L3/L4)防護,需由部署環境(反向代理、WAF、DDoS 服務)提供。

### 2.9 Malicious Client(惡意用戶端)
- **防護**:所有輸入視為不可信並驗證(名稱長度與控制字元、公鑰格式、base64、權限位元、候選長度、訊息大小);權限只能授予已定義的位元,長期信任不得含重啟/關機;輸入事件、剪貼簿、檔案傳輸各有權限與速率檢查(Phase 8–10)。
- **測試**:`Registration_RejectsMalformedInput_*`、`Permissions_*`、`TrustedDevices_*`。

### 2.10 Compromised Client(被入侵的用戶端)
- **防護**:最小權限(預設僅檢視)、目標端逐次核准、權限可隨時撤銷且立即生效、檔案限制在沙盒目錄、剪貼簿預設關閉。
- **殘留**:被入侵的設備私鑰可冒充該設備;目前沒有設備撤銷機制(見 §4)。

### 2.11 Malicious Server(惡意或被入侵的伺服器)
- **防護**:內容端到端加密,伺服器與 Relay 看不到螢幕、輸入、剪貼簿與檔案內容;日誌不記錄祕密。
- **殘留**:**伺服器可以替換設備公鑰而發動中間人攻擊**,因為雙方的公鑰是從伺服器取得的,且尚無帶外指紋驗證(見 §4)。伺服器也可以偽造「核准」訊息。

### 2.12 Credential Theft(憑證竊取)
- **防護**:私鑰不離開簽章器、不記錄、不傳送;token 不放在 URL(以 `X-Relay-Token` 標頭傳遞,查詢字串僅作相容後備);token 短效且單次使用,外洩的價值有限;稽核與錯誤訊息只含類型,不含祕密。

### 2.13 Unauthorized Control(未授權控制)
- **防護**:必須通過設備認證、Session 核准(或信任設備/OTP)、Session 為 `Connected`、權限包含對應項目,才可注入輸入(`InputGate`);每次事件都重新讀取權限與狀態。

### 2.14 File Transfer Abuse
- **防護**(Phase 10):檔名嚴格驗證、限制在根目錄、不覆蓋既有檔案、整檔 SHA-256 驗證、大小與同時傳輸數上限、速率上限、權限逐訊息檢查。

### 2.15 Clipboard Data Leakage
- **防護**(Phase 9):預設 Disabled;遠端能做的事 = 主機本地策略 ∩ Session 權限;不自動同步;大小與速率限制;不記錄內容。

## 3. 其他審查項目

| 項目 | 結果 |
|---|---|
| 日誌 | 伺服器與用戶端程式碼中只有 Agent 的啟動/停止兩行日誌;沒有任何位置記錄私鑰、token、OTP、剪貼簿或檔案內容。稽核 `Reason` 除 `CloseSession` 外都是固定字串或列舉值;`CloseSession` 的原因是使用者提供的自由文字(長度上限 256),不應放入祕密 |
| 稽核完整性 | 新增 Paused/Resumed/Expired 事件;記憶體內、有上限、**非防竄改** |
| 錯誤訊息 | 認證與授權失敗一律使用相同訊息,細節只進稽核 |
| 時序 | OTP 與公鑰比較使用 `CryptographicOperations.FixedTimeEquals` |
| 密碼學 | 全部使用 BCL 的成熟元件(ECDSA P-256、ECDH、HKDF、AES-GCM);沒有自創演算法、金鑰交換或 MAC |

## 4. 殘留風險與後續工作

| # | 風險 | 影響 | 建議 |
|---|---|---|---|
| 1 | 雙方公鑰由伺服器提供,沒有帶外驗證 | 惡意伺服器可 MITM | 在核准畫面顯示對方金鑰指紋,或配對時帶外交換 |
| 2 | 工作階段金鑰為「靜態 ECDH」,無前向保密 | 設備私鑰外洩後可解密已錄下的舊流量 | 改用成熟協定的臨時金鑰(例如 Noise 或 TLS 1.3 + 雙向認證),不要自行設計 |
| 3 | 首次註冊釘住公鑰(TOFU),沒有金鑰輪替/設備撤銷 | 私鑰外洩或換機後無法重新註冊同一 ID | 增加由使用者操作的撤銷與輪替流程 |
| 4 | 速率限制以 `RemoteIpAddress` 為鍵 | 在反向代理後所有人會共用代理位址而被一起鎖定 | 部署時務必設定 `ForwardedHeaders`,並只信任已知代理 |
| 5 | 狀態全在記憶體(Session、token、稽核、限流) | 重啟後全部消失,稽核不可防竄改 | PostgreSQL 持久化與外送稽核 |
| 6 | 等待對端的 Relay 連線不讀取對方的 Close 訊框 | 對端失蹤時,連線最多佔用角色槽 30 秒(實際斷線由 `RequestAborted` 處理) | 可在等待期間加入有上限的緩衝讀取 |
| 7 | `SecureTransport` 的使用靠慣例,尚無 Session 協調器強制 | 若上層直接使用裸通道就沒有加密 | Phase 14 整合時由 Agent 統一建立通道,並加整合測試 |
| 8 | Relay token 的查詢字串後備仍存在 | 可能出現在代理或存取日誌 | 正式環境關閉後備 |
| 9 | 設備註冊、OTP 鎖定可被用於騷擾式 DoS | 可用性影響 | 以來源位址與信譽進一步限制 |
| 10 | 沒有全域連線/頻寬上限與網路層防護 | 大量流量 DoS | 部署層(代理、WAF、DDoS 防護) |
