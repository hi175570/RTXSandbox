# RTX Sandbox Android V1

Android 版本是同一個遊戲的行動 Host，不是固定畫面的獨立 Demo。它直接編入桌面版既有的 World、Camera、PlayerController、Block／Item Registry、CreativeHotbar、PlayerInventory、WorldSaveService 與 Mod API，並以 Android 平台層處理 Activity、儲存、觸控與 GLES。

目前可用：

- 建立 Natural 世界，非同步排入附近 Chunk，並讀寫與桌面相同 schema 的 `level.json`、Region 與 player 資料。
- 行走、跳躍、蹲下、視角、碰撞、破壞、放置、物品欄選擇、建造工具與精密模式狀態。
- 可編輯八方向鍵或搖桿、按鈕、右側視角區；配置以 normalized JSON 儲存。
- 外接鍵盤、滑鼠與手把映射到同一套 `GameAction`。鍵盤預設可用 WASD／方向鍵、Space、Shift 蹲下、Ctrl＋前進或連按兩次前進衝刺、E、V、B、Escape；遊戲內按鍵頁可捕捉新的實體鍵、重設並跨重啟保存。
- 與桌面直接共用 `SharedImGuiScreens` 的 ESC 暫停選單、成就列表與繁中／簡中／英文頁；相同 `RTX Sandbox Editor`、`Blocks / Tools`、九格快捷欄、搜尋及材質卡片排列；相同三頁籤設定骨架。觸控配置是 Android 額外區塊。
- Classic GLES 與 PBR Forward GLES；GLES 3 裝置另有真正的 MRT G-buffer Deferred 與使用同一 Geometry pass 的 Hybrid PBR；GLES 3.1 裝置可使用 Compute voxel ray tracing V1。五者讀取同一世界與共用材質資料，不以 Classic 畫面冒充其他 Renderer。
- 共用有限體積流水、實際液面高度與透明水面；共用 Micro Voxel 精密放置／刪除；共用 BEPUphysics 2 Physics Grid 模擬、顯示與互動。
- 世界建立、載入、同 Seed 重建、重新命名與刪除；測試用世界已實際建立、切回舊世界並刪除。
- App 背景化會開啟暫停並排入合併式背景保存；離開世界會完成保存後返回主選單。
- per-Chunk VBO／dirty cache、精確邊界失效、背景 CPU meshing與不透明 voxel greedy meshing；同一測試世界的 raster 頂點由 `306,360` 降至 `75,240`，局部修改不再重建周圍 25 個 Chunk。

能力回退是明示的：Deferred／Hybrid 需要 GLES 3，Compute Path Tracing V1 需要 GLES 3.1；Hardware DXR 僅支援 Windows D3D12。Android 選單會顯示裝置能力與原因，不會用 Classic 畫面冒充高階 Renderer。

尚未完成：Android Mod runtime、桌面程序材質縮圖在 GLES ImGui 中的 texture binding、全部桌面 Renderer 進階設定，以及實體 ARM64 手機的 3D 效能和多點觸控／手把驗證。Compute V1 目前追蹤主要 voxel 世界、方向光陰影、金屬反射與一次 diffuse bounce；尚未包含 Micro Voxel／Physics Grid 的解析幾何及完整透明／折射語意。Hardware DXR 不屬於 Android 可完成能力。流水、Micro Voxel、Physics Grid、世界管理與核心共用 UI 已接入，不再列為完全未完成。

## 已驗證

- Android x64 Debug、x64 Release、arm64 Release：`0 warnings / 0 errors`。
- x64 APK 以 `adb install -r` 覆蓋安裝於既有 Android 11／API 30 模擬器成功，沒有卸載或清除資料。
- 日誌確認讀取舊世界；host-GPU GLES 3.1 測試 VM 中 Classic 約 59–60 FPS、Compute V1 約 58–60 FPS。Deferred 修正每幀重建 G-buffer 後由約 19 FPS 提升到穩態 58–60 FPS；切換後僅首次配置有約 55 ms 尖峰，後續量測多為 5–10 ms。
- Debug Physics Grid 診斷在同一 VM 實際執行 1 個 BEPU 動態剛體與每幀 2 個碰撞步驟；縮減不必要的玩家遠距 collider 預熱後，靜態碰撞體 `2,409 → 755`，穩態約 `22–26 → 38–39 FPS`。
- arm64 Release 已安裝至 Samsung A32（Android 13／API 33）；手機受安全鎖保護，本輪沒有繞過，因此不得宣稱真機畫面／觸控已驗證。
- 共用暫停、PC 版式設定、`RTX Sandbox Editor`、成就四列、繁中／簡中／英文切換、觸控編輯入口及中央 `SHF` 已在 API 30 x64 VM 逐頁驗證；新中文 glyph 正常，沒有 `AndroidRuntime` fatal 或 ImGui 載入失敗。
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
