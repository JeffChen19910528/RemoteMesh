# 平台支援:Windows、Linux、macOS

同一份程式碼、同樣的功能:查看畫面、滑鼠鍵盤控制、剪貼簿、檔案傳輸、斷線自動重連、金鑰核對與輪替。每個作業系統只有「螢幕擷取、輸入注入、剪貼簿」三個後端與桌面程式外殼不同。

| | Windows | Linux | macOS |
|---|---|---|---|
| 桌面程式 | WPF(`RemoteMesh.exe`)或 Avalonia | Avalonia(`RemoteMesh`) | Avalonia(`RemoteMesh.app`) |
| 無介面 Agent | ✅ | ✅ | ✅ |
| 螢幕擷取 | GDI `BitBlt` | X11 `XGetImage` | CoreGraphics `CGDisplayCreateImage` |
| 輸入注入 | `SendInput` | XTEST | `CGEvent` |
| 剪貼簿(文字) | Win32 | `xclip` / `xsel` / `wl-clipboard` | `pbcopy` / `pbpaste` |
| 有損畫面(JPEG) | GDI+ | Skia | Skia |
| 驗證程度 | 真實進程與真實介面驗證 | 真實 X 伺服器(Xvfb)驗證,見下 | **只有邏輯測試;沒有在真實 Mac 上跑過** |

## 程式碼結構

```
RemoteDesktop.Platform          選擇本機的後端(PlatformHost);Agent 與桌面程式都只呼叫它
├── RemoteDesktop.Platform.Windows   GDI、SendInput、Win32 剪貼簿、GDI+ JPEG
├── RemoteDesktop.Platform.Linux     X11 擷取、XTEST、剪貼簿工具
├── RemoteDesktop.Platform.MacOS     CoreGraphics、CGEvent、pbcopy/pbpaste、權限檢查
├── RemoteDesktop.Platform.Skia      跨平台 JPEG 編解碼(Linux、macOS)
└── RemoteDesktop.Platform.Common    以外部工具實作的剪貼簿、指令執行器
RemoteDesktop.UI.Core           View-model、設定、核准邏輯(與視窗工具箱無關)
RemoteDesktop.UI                Windows 的 WPF 視窗
RemoteDesktop.UI.Avalonia       跨平台視窗(Windows、Linux、macOS)
```

線上協定不分平台:按鍵用 Windows 虛擬鍵碼傳送,各平台的後端再轉成自己的鍵(X11 keysym、macOS 鍵碼);滑鼠座標是 0–32767 的相對位置;畫面是 BGRA。所以 Windows 可以控制 Mac,Mac 可以控制 Linux,任意組合。

## Linux

- **需要 X11 工作階段**(Xorg;登入畫面選「Ubuntu on Xorg」之類)。**純 Wayland 工作階段目前不能被擷取或控制**:Wayland 刻意不讓程式擷取別人的視窗、注入輸入;正確的做法是 xdg-desktop-portal 的 ScreenCast + PipeWire(擷取)與 libei(輸入),**尚未實作**。XWayland 下 `XGetImage` 讀不到整個螢幕(實測回 `BadMatch`),程式會告知並讓這台電腦仍可當檢視端。
- 剪貼簿需要 `xclip` 或 `xsel`(X11),或 `wl-clipboard`(Wayland)。沒有就不能共用剪貼簿,其他功能不受影響,並提示要安裝什麼。
- 介面是中文:需要一套 CJK 字型(例如 `fonts-noto-cjk`),否則看到方框。
- 鍵盤:標點符號鍵以美式鍵盤命名(送出的是「鍵上印的字」);其他鍵盤配置的標點可能對不上。
- 擷取走 `XGetImage`(沒有 XShm),全螢幕大約幾十毫秒;高解析度桌面的畫面更新率會比 Windows 低。
- 滑鼠游標不在畫面內(檢視端自己畫游標)。
- 私鑰以 0600 檔案權限保存(尚未接 libsecret / kernel keyring)。
- 依賴:`libX11.so.6`、`libXtst.so.6`(桌面系統都有);桌面程式另需 `libfontconfig`(Avalonia 的需求)。

## macOS

- **第一次啟動會被 Gatekeeper 擋**(沒有簽章與公證):對 `RemoteMesh.app` 按右鍵 → 打開,或 `xattr -dr com.apple.quarantine RemoteMesh.app`。
- 必須在「系統設定 → 隱私權與安全性」授權兩項,**授權後要重新啟動程式**:
  - **螢幕錄製**:沒有的話擷取到的只有桌布。
  - **輔助使用**:沒有的話滑鼠鍵盤事件被系統丟棄。
  程式啟動時檢查並顯示缺哪一項。
- Retina 螢幕以完整像素擷取後,寬度超過 2560 的整數倍縮小(每個輸出像素是區塊平均),避免即時編碼負擔。
- 鍵盤:Windows 的 Ctrl 當成 Control、Windows 鍵當成 Command(`CGEventInputController.CtrlActsAsCommand` 可改成 Ctrl→Command,讓 Ctrl+C 變成複製);Windows 的 Insert 對應到 Help(Mac 沒有 Insert)。
- `CGDisplayCreateImage` 在新版 macOS 被標為過時(建議改用 ScreenCaptureKit),目前仍可呼叫;ScreenCaptureKit 後端尚未實作。
- 目前只擷取主螢幕。
- 連線的 TLS 層由 .NET 在 macOS 上提供;macOS 當 TLS 伺服器(被控端)的行為需要在實機確認,不行的話握手會失敗並明確報錯,不會退回不加密。
- **這個後端沒有在真實 Mac 上執行過**(沒有 Mac 可用)。能不依賴 Mac 驗證的部分(鍵碼表、座標與縮放數學、剪貼簿指令、權限在其他系統上的行為)有測試;擷取、事件注入、權限提示、`.app` 能否開啟必須在 Mac 上驗證。

## 怎麼驗證的(Linux)

在 Windows 上建置、複製到 WSL(Ubuntu 24.04),用 Xvfb(虛擬 X 伺服器)跑:

- 單元與整合測試 150 個,Linux 上 0 略過:真實視窗被擷取、顏色正確(紅色視窗經 JPEG 管線後仍是紅的)、滑鼠移到正規化座標對應的像素、按下的鍵被 X 伺服器看見、剪貼簿以 UTF-8 來回、錯誤與非法輸入。
- 真實進程:真實伺服器 + 打包後的 Linux Agent(自含,不需安裝 .NET)+ 檢視端:看到對方畫的紅色視窗、滑鼠到達 (320, 600)、A 鍵被按下、雙向剪貼簿(含引號、換行、中文)。
- 桌面程式在 Xvfb 上啟動、建立視窗、註冊並取得短 ID。

沒有在實體 Linux 桌面(GNOME/KDE 的 Xorg 工作階段)上驗證過;Xvfb 沒有視窗管理員、沒有真實顯示卡。

## 打包

```powershell
./scripts/publish-desktop.ps1 -Runtime linux-x64     # 或 linux-arm64、osx-arm64、osx-x64
```

在任何有 .NET 10 SDK 的系統都能為任何目標建置(Windows 上也能做出 Linux 與 macOS 的套件)。輸出 `artifacts/remotemesh-<版本>-<平台>.tar.gz` 與 `SHA256SUMS-desktop.txt`:自含(不需安裝 .NET),可執行檔權限由套件工具明確設定;macOS 是 `RemoteMesh.app` + `agent/`,Linux 是 `RemoteMesh/` + `agent/`。**沒有簽章與公證,沒有安裝程式**(沒有 deb/rpm/dmg)。
