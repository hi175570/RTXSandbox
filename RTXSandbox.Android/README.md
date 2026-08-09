# RTX Sandbox Android V1

Android 版本是同一個遊戲的行動 Host，不是固定畫面的獨立 Demo。它直接編入桌面版既有的 World、Camera、PlayerController、Block／Item Registry、CreativeHotbar、PlayerInventory、WorldSaveService 與 Mod API，並以 Android 平台層處理 Activity、儲存、觸控與 GLES。

目前可用：

- 建立 Natural 世界，非同步排入附近 Chunk，並讀寫與桌面相同 schema 的 `level.json`、Region 與 player 資料。
- 行走、跳躍、蹲下、視角、碰撞、破壞、放置、物品欄選擇、建造工具與精密模式狀態。
- 可編輯八方向鍵或搖桿、按鈕、右側視角區；配置以 normalized JSON 儲存。按鈕可選完整標準 104 鍵名稱；右側空白視角區可長按破壞，獨立 LMB／RMB 仍保留。
- 外接鍵盤、滑鼠與手把映射到同一套 `GameAction`。鍵盤預設可用 WASD／方向鍵、Space、Shift 蹲下、Ctrl＋前進或連按兩次前進衝刺、E、V、B、Escape；遊戲內按鍵頁可捕捉新的實體鍵、重設並跨重啟保存。
- 與桌面直接共用 `SharedImGuiScreens` 的 ESC 暫停選單、成就列表與繁中／簡中／英文頁；語言清單固定顯示 `繁體中文／简体中文／English`；相同 `RTX Sandbox Editor`、`Blocks / Tools`、九格快捷欄、搜尋及材質卡片排列；相同三頁籤設定骨架。觸控配置是 Android 額外區塊。
- Classic GLES 與 PBR Forward GLES；GLES 3 裝置另有真正的 MRT G-buffer Deferred 與 Hybrid PBR；GLES 3.1 裝置可使用 Compute voxel ray tracing V1。四條 Raster 管線共用 color/detail atlas、材質面、粗糙度與 emission，但分別執行 Lambert、GGX Forward、G-buffer lighting 與加強環境反射，不以同一張 Classic 畫面套濾鏡冒充其他 Renderer。
- 共用有限體積流水、實際液面高度與透明水面；共用 Micro Voxel 精密放置／刪除；共用 BEPUphysics 2 Physics Grid 模擬、顯示與互動。
- 世界建立、載入、同 Seed 重建、重新命名與刪除；測試用世界已實際建立、切回舊世界並刪除。
- App 背景化會開啟暫停並排入合併式背景保存；離開世界會完成保存後返回主選單。
- per-Chunk VBO／dirty cache、精確邊界失效、背景 CPU meshing與不透明 voxel greedy meshing；同一測試世界的 raster 頂點由 `306,360` 降至 `75,240`，局部修改不再重建周圍 25 個 Chunk。
- BepInEx 6 CoreCLR 與 Android managed-plugin adapter；授予一次模組資料夾權限後，玩家可直接把模組資料夾、插件 DLL 與相依 DLL 放入公開的 `Documents/RTXSandbox/Mods/`，重新啟動後載入，原始檔不會被搬移或刪除。系統檔案選擇器只保留為可選匯入入口；Release x64 已從這個公開目錄實際執行 Example Mod、註冊方塊並收到 `WorldReady`。

能力回退是明示的：Deferred／Hybrid 需要 GLES 3，Compute Path Tracing V1 需要 GLES 3.1；Hardware DXR 僅支援 Windows D3D12。Android 選單會顯示裝置能力與原因，不會用 Classic 畫面冒充高階 Renderer。

尚未完成：Android Mod runtime 與桌面 BepInEx 的完整等價（依賴拓撲、隔離式載入、Hot Reload、RuntimeDetour）、全部桌面 Renderer 進階設定，以及實體 ARM64 手機的 3D 效能和多點觸控／手把驗證。Compute V1 目前追蹤主要 voxel 世界、方向光陰影、金屬反射與一次 diffuse bounce；尚未包含 Micro Voxel／Physics Grid 的解析幾何及完整透明／折射語意。Hardware DXR 不屬於 Android 可完成能力。流水、Micro Voxel、Physics Grid、世界管理、共用材質 UI 與核心共用 UI 已接入，不再列為完全未完成。

## 已驗證

- Android x64 Debug／Release 與 x86 Release：`0 warnings / 0 errors`。ARM64／ARMv7 Release 均成功產生 signed APK，但各保留 1 個 MonoMod／RuntimeDetour 不支援該 ABI 的警告；基本 BasePlugin 可載入不代表 Runtime patch 類 Mod 相容。
- x64 APK 以 `adb install -r` 覆蓋安裝於既有 Android 11／API 30 模擬器成功，沒有卸載或清除資料。
- 日誌確認讀取舊世界；host-GPU GLES 3.1 測試 VM 中 Classic 約 59–60 FPS、Compute V1 約 58–60 FPS。Deferred 修正每幀重建 G-buffer 後由約 19 FPS 提升到穩態 58–60 FPS；切換後僅首次配置有約 55 ms 尖峰，後續量測多為 5–10 ms。
- Debug Physics Grid 診斷在同一 VM 實際執行 1 個 BEPU 動態剛體與每幀 2 個碰撞步驟；縮減不必要的玩家遠距 collider 預熱後，靜態碰撞體 `2,409 → 755`，穩態約 `22–26 → 38–39 FPS`。
- arm64 Release 已安裝至 Samsung A32（Android 13／API 33）；手機受安全鎖保護，本輪沒有繞過，因此不得宣稱真機畫面／觸控已驗證。
- 共用暫停、PC 版式設定、`RTX Sandbox Editor`、成就四列、繁中／簡中／英文切換、觸控編輯入口及中央 `SHF` 已在 API 30 x64 VM 逐頁驗證；新中文 glyph 正常，沒有 `AndroidRuntime` fatal 或 ImGui 載入失敗。
- 設定頁已恢復 PC 的「畫面／時間與光照／控制與介面」三個核心頁籤並另加「觸控」；VM 已實際操作日夜時間、日長、FOV、顯示距離、靈敏度、建造距離、飛行／穿牆、物理開關與保存。時間亮度 uniform 已在 Classic、PBR、Deferred 與 Compute 實際建 Shader／出圖，Hybrid 共用已驗證的 Deferred lighting path。
- 語言頁在 VM 實際以三種語言自稱顯示；同一視角長按前後畫面確認草方塊被破壞，且 LMB／RMB 按鈕未移除；可綁定鍵表程式檢查為 104 項。
- Release x64 以 BepInEx managed-plugin adapter 從 `/storage/emulated/0/Documents/RTXSandbox/Mods/ExampleMod/` 直接載入 Example Mod，日誌包含 `Registered examplemod:signal_block` 與 `WorldReady`，不是只把 ModAPI 打包進 APK。
- 外接鍵盤前進鍵由 W 改綁為 Q 後 UI 即時更新，SharedPreferences 寫入 `Keyboard:W -> Q`，強制停止並重新啟動 App 後仍保留。
- 背景保存以舊世界檔案時間戳實測更新，程序保持存活且沒有 ANR；世界管理建立的臨時世界在驗證載入／切換後已刪除。
- x64／ARM64 `libcimgui.so` 的 ELF `LOAD` alignment 均為 `0x4000`，且靜態連結 C++ runtime，不依賴 APK 外的 `libc++_shared.so`。
- arm64 APK 目前只完成建置，未冒充真機啟動結果。

## 建置

```powershell
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
$jdk = "C:\Program Files\Android\Android Studio\jbr"

dotnet build RTXSandbox.Android/RTXSandbox.Android.csproj -c Release `
  -p:RuntimeIdentifier=android-x64 `
  -p:AndroidSdkDirectory="$sdk" `
  -p:JavaSdkDirectory="$jdk"
```

以 `adb install -r <signed-apk>` 安裝輸出的 APK。
