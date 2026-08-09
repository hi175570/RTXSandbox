# RTX Sandbox Roadmap

## 本輪：Android 虛擬機最終回歸（2026-08-09）

> 本輪所有 ADB 安裝、啟動、輸入、截圖與 logcat 都明確指定 `emulator-5554`（Android 11／API 30；依 APK 驗證 x86_64 與 x86）；即使裝置清單曾出現實體手機，也沒有對手機序號發送任何操作。本節優先於下方較早的 Android 狀態。

| 項目 | 狀態 | 驗證證據／限制 |
|---|---|---|
| Android 物品欄材質 | ✅ **【已完成並在 VM 顯示】** | Android UI 已使用共用 `ProceduralMaterialAtlas` 的 GLES texture，以與桌面相同的等角方塊卡顯示 top／side 材質；草、土、石、發光、金屬、玻璃、沙、雪與木材均已在 `build/validation/emulator-inventory-textured.png` 實際顯示。物品欄可用 `E` 開啟與關閉，快捷欄位置已下移。 |
| Android Raster 陰影 | ✅ **【方向光陰影 V1 已完成】** | Classic、PBR Forward、Deferred 與 Hybrid 共用快取式 1024×1024 方向光 shadow map，3×3 PCF；Deferred／Hybrid 透過第三張 G-buffer attachment 傳遞 shadow visibility。四條管線均在同一 VM 世界實際出圖，沒有 shader／FBO／AndroidRuntime error。點光、聚光與完整桌面反射仍未完成。 |
| 桌面 PBR Forward 回退 | ✅ **【本輪發現並修復】** | 六管線回歸時確認 `forward_post.frag.glsl` 的鄰域 sharpen 取樣括號錯誤，PBR shader 編譯失敗後曾安全回退 Classic。已改為單一正確的 tone-map 取樣函式；RTX 5050 High 與 Ultra 均真正以 `PBR Forward Raster` 通過，Ultra feature mask 2047、4096² shadow、framebuffer 94–255，沒有再回退。 |
| Android Compute Path Tracing | ✅ **【黑畫面問題目前不存在／流體高度已接入】** | 在 GLES 3.1 VM 實際切換並持續出圖，`build/validation/emulator-compute-fluid-level-final.png` 證明目前 compute shader、世界 volume 與 ImGui 均正常執行；新增 R8UI fluid-level volume，部分水格不再被當作完整立方體。Micro Voxel／Physics Grid 精確求交與完整透明折射仍是後續限制。 |
| 已有水格再次倒水 | ✅ **【已完成並自測】** | 桌面、Build Tool 與 Android 都允許以水對準已有水格再次倒入；每桶增加 16 單位，滿格溢出會交給既有守恆流水向鄰格分配。91/91 self-tests 驗證第二桶可接受且總量 32 不消失。 |
| Physics 模擬開關 | ✅ **【已完成並操作驗證】** | 桌面與 Android 均可暫停／恢復物理模擬；關閉只凍結既有 Physics Grid，不刪除或重設位置。91/91 self-tests 覆蓋停用後位置保持與恢復後繼續下落；VM 設定頁已實際切換並恢復預設開啟。 |
| Android 中文字形 | ✅ **【已完成並在 VM 顯示】** | 設定頁使用的繁中／簡中完整句子與標點已納入 glyph corpus；`build/validation/emulator-gameplay-settings-final2.png` 未再出現缺字 `?`。 |
| 語言名稱自稱 | ✅ **【已完成並在 VM 顯示】** | 共用語言頁固定以該語言自己的文字顯示 `繁體中文`、`简体中文`、`English`，不受目前介面語言影響；`build/validation/android-language-autonyms.png` 已在 API 30 VM 顯示三項。 |
| Android 104 鍵與長按破壞 | ✅ **【已完成－VM 操作驗證】** | 觸控按鈕可綁定標準 104 鍵 PC 鍵盤命名空間；右側空白視角區停留 360 ms 會開始 LMB 長按破壞，移動超過 28 px 則取消以保留視角拖曳。原 LMB／RMB 按鈕沒有移除；同視角 ADB 長按前後截圖確認準星前草方塊被移除。 |
| Android Mod runtime V1 | 🟡 **【正式可執行／非桌面完全等價】** | APK 內啟動 BepInEx 6 CoreCLR；因 BepInEx Cecil 掃描無法解析 APK 組件，Android managed-plugin adapter 以 `BasePlugin`／`BepInPlugin` 正式建立插件、Log、Config 與 Harmony。Android 主要插件根目錄改為公開的 `Documents/RTXSandbox/Mods/`；玩家授予一次資料夾權限後可直接放入模組資料夾及相依 DLL，遊戲不搬移或刪除原始檔，SAF 單 DLL 匯入僅是可選入口。Release x64 已從該公開目錄實際載入 `RTXSandbox.ExampleMod.dll`，註冊 `examplemod:signal_block` 並收到 `WorldReady`。依賴拓撲排序、隔離式 AssemblyLoadContext、Hot Reload 與 RuntimeDetour 相容性仍未完成。 |
| Android 共用材質／Renderer 區分 | ✅ **【V1 已在同一 VM 世界逐條出圖】** | Classic、PBR Forward、Deferred、Hybrid 都讀取共用 color/detail atlas、top／side／bottom face 與 emission。Classic 是貼圖 Lambert；PBR Forward 是 GGX；Deferred 建 MRT G-buffer 後做深度 AO／PBR lighting；Hybrid 加強環境反射；Compute PT 仍走 GLES 3.1 compute voxel ray tracing。`android-deferred-final.png`、`android-hybrid-final.png`、`android-compute-final.png` 均無 shader／FBO／FATAL。完整桌面效果仍見下方限制。 |
| Android 設定核心頁籤 | ✅ **【已恢復並在 VM 操作】** | Android 設定改回與 PC 同名的「畫面／時間與光照／控制與介面」，另保留平台專用「觸控」。顯示距離、FOV、日夜循環、日長、視角靈敏度、建造距離、飛行／穿牆、物理開關、按鍵入口與立即保存均接到實際狀態並持久化適用項目；時間亦會影響 Classic／PBR／Deferred／Hybrid／Compute 的亮度並寫入世界存檔。VM 截圖 `android-settings-tabs.png` 與 `android-settings-time-lighting-final2.png` 證明頁籤和內容實際顯示；完整桌面 Renderer 進階設定仍是後續項。 |
| Android 局部更新延遲 | 🟡 **【已改善／未達效能目標】** | 互動 mesh 更新由固定 80 ms 節流改為 60 Hz 排程；直接放置／挖除可立即要求更新，初次可見 Chunk 也改為最近一個同步、其餘逐個背景建立。VM 1920×1080 實測約 46–57 FPS，仍未達 100／120 FPS，不能標示為效能完成。 |
| Android 四 ABI | ✅ **【封裝完成／x86 實際執行】** | `arm64-v8a`、`armeabi-v7a`、`x86_64`、`x86` 均有同版本 `libcimgui.so`，ELF LOAD alignment 為 0x4000 且不依賴 `libc++_shared.so`。已修正 32 位 `ImTextureID` ABI 錯位；x86 signed APK 在 `emulator-5554` 實際載入共用 ImGui、世界與 Classic，穩態約 57–61 FPS且無 FATAL／OverflowException。ARMv7 只標記建置與封裝驗證。 |
| 建置與執行 | ✅ **【本輪已驗證】** | Android x64 Debug／Release 及 x86／ARM64／ARMv7 Release 均產生 signed APK；x64 signed APK 已用 `adb -s emulator-5554 install -r` 覆蓋安裝並啟動。x64／x86 為 0 warnings／0 errors；ARM64／ARMv7 各保留 1 個真實 MonoMod／RuntimeDetour 不支援警告而非隱藏。Release UI、Mod、長按破壞及五條非 DXR Renderer 已在 VM 操作。 |
| 完整 Android／桌面畫質一致 | 🟡 **【部分完成】** | 主選單、暫停、設定、物品欄、世界與輸入使用共用資料與同款 ImGui；Hardware DXR 維持 Windows-only。Android 尚缺桌面完整點／聚光陰影、反射、體積霧／煙霧、完整水材質與全部 Renderer 高階設定，不能宣稱 100% 一致。 |
| 遠端 GitHub Actions | 🟡 **【已取得新失敗證據並續修】** | Run 31294267313 的 Ubuntu／macOS build 成功，但 `dotnet run --no-build` 立即失敗；WSL 重現證明專案把跨平台 `RunCommand` 硬編成 `RTXSandbox.exe`。現已依 Windows／Unix 選擇根目錄 apphost，並保留 .NET 9＋6、Node 24 官方 Actions、Android SDK 明確失敗與四 ABI publish；最終狀態仍以修正 push 後的新 run 為準。 |
| DLSS／FSR 補幀 | ⛔ **【未假實作】** | 目前最終呈現仍是 OpenGL；DXR 的 D3D12 結果會 readback 再上傳 OpenGL。正式 DLSS Frame Generation／FSR Frame Generation 需要 D3D12 或 Vulkan presentation、motion vectors、depth 與 UI-less color 等輸入，不能用一個 OpenGL 設定開關冒充。需先建立共用 D3D12／Vulkan present 路徑，本輪只記錄阻塞，不標完成。 |

## 本輪：桌面／Android 共用 ImGui 介面（2026-08-08）

> 本節是目前 Android 狀態；後文較早的「尚未移植」紀錄只保留歷史，不應覆蓋本節。

| 項目 | 狀態 | 驗證證據／限制 |
|---|---|---|
| 共用 UI 畫面層 | ✅ **【已完成－第一階段】** | 新增 `SharedImGuiScreens`；Windows 與 Android 的暫停選單、成就列表、語言頁直接呼叫同一份 ImGui 程式碼，不再各維護一份相似畫面。平台 Host 只處理 callback、保存與輸入。 |
| Android 暫停選單 | ✅ **【已完成並實際顯示】** | 與 PC 共用 8 鍵排列、尺寸、色彩與返回流程；Android 只在獨立分隔區增加「編輯觸控」。Mod 按鈕已啟用並開啟實際 BepInEx 插件頁；API 30 x64 VM 截圖確認繁中正常且無 FATAL。 |
| Android 物品欄／建造工具 | ✅ **【可運作版】** | 改為 PC 同款 `RTX Sandbox Editor`、`Blocks / Tools` 頁籤、九格快捷欄、搜尋、排序後材質卡片與精密格線選項；ADB `E` 實際開啟並顯示 199 個材質。卡片與遊戲場景均已綁定共用程序材質 atlas；更複雜的桌面後處理縮圖仍未逐效果對等。 |
| Android 設定 | 🟡 **【版面一致／Renderer V1 已接入】** | 使用 PC 的 `RTX Sandbox 設定` 視窗、三頁籤及同一藍色 ImGui style。Classic／PBR 可用；Deferred／Hybrid 是 GLES 3 MRT／multi-pass；Compute 是 GLES 3.1 compute voxel ray tracing。Hardware DXR 明確維持 Windows-only。Android 系統 Surface 控制的解析度／VSync與全部桌面高階細項尚未接入。 |
| 主選單與世界管理 | ✅ **【可運作版並實際驗證】** | Android 已採 PC 的置中主選單，並接入世界建立、載入、同 Seed 重建、重新命名、刪除及設定／語言入口。API 30 VM 實際建立 `New World`、切回 `world1`、刪除測試世界；最後只剩原 `world1`，沒有留下垃圾世界。 |
| 外接鍵盤改鍵 | ✅ **【已完成並實際重啟驗證】** | Android 按鍵頁可點選動作後捕捉下一個實體鍵，支援重設與持久化。VM 把前進從 W 改成 Q，UI 即時顯示 Q，SharedPreferences 為 `Keyboard:W -> Q`；強制停止／重啟後資料仍保留。 |
| 背景／離開保存 | ✅ **【已完成並實際驗證】** | `OnPause` 不再在 Activity 主執行緒同步寫 Region；改為可合併的背景保存工作，避免已復現的 5 秒 ANR。舊世界 `level.json` 時間戳由 `1786154324 → 1786154582`，程序存活且無 ANR／FATAL。離開世界仍在切回主選單前完成保存。 |
| 建置與回歸 | ✅ **【已完成／ARM 警告已揭露】** | Windows Debug／Release與 Android x64 Debug／Release 均 `0 warnings / 0 errors`；Windows 91/91 self-tests 通過。Android ARM64／ARMv7 Release 可產生 signed APK，但加入 BepInEx 後各有 1 個 MonoMod／RuntimeDetour 平台不支援警告，不能再寫成零警告。x64 APK 覆蓋安裝保留資料，共用 UI／Mod／世界管理／五條 Android Renderer 無 `AndroidRuntime` FATAL。 |

## 本輪：Android 共用世界內容、物理與局部串流（2026-08-08）

| 項目 | 狀態 | 驗證證據／限制 |
|---|---|---|
| Android 有限水量與流水 | ✅ **【已完成－V1】** | Android 直接使用共用 `WaterFlowSimulation` 與 1～16 單位流量資料；GLES 依實際液面高度建立透明頂面與高度差側面。API 30 x86_64 模擬器實際放置水後，頂點數 `306,360 → 306,552` 並可見水向周圍逐格擴散。 |
| Android Micro Voxel | ✅ **【已完成－V1】** | V 鍵使用共用 `MicroBuildSystem`；放置／刪除、1.1 格距離限制、玩家重疊限制與存檔資料均共用。模擬器實際放置 1/16 微方塊後頂點數增加 36，畫面與狀態顯示 `V 精密`。 |
| Android Physics Grid | ✅ **【已完成－V1】** | 已接入共用 `VoxelPhysicsSimulator`／BEPUphysics 2、動態 Grid 顯示、普通／精密 Grid 放置與刪除。Debug 診斷場景在 API 30 x86_64 實際為 `1 bodies`、每幀 `2 collision steps`；方塊落地後可用一般 LMB 刪除，Grid 數由 1 回到 0。診斷 session 不寫回玩家存檔。 |
| Android 局部 Mesh 更新 | ✅ **【已完成－本輪優化】** | 原本每次局部修改重建並上傳周圍 25 Chunk，流水壓測會連續出現 `1–2 FPS`。現改為 per-Chunk VBO／版本化 dirty cache、邊界精確失效、每次最多提交一個 Chunk，CPU meshing 在背景執行；同流程基線約 `51–57 FPS`，最慢記錄 `48 FPS`，未降低畫質或距離。 |
| Android greedy meshing | ✅ **【已完成－本輪優化】** | 不透明 voxel 面在 Chunk 內合併，水／玻璃／Micro Voxel／Physics Grid 保持原幾何語意。同一舊世界頂點數 `306,360 → 75,240`（減少 75.4%）；host-GPU VM 畫面無缺面，Classic 穩態 59–60 FPS。 |
| Android Deferred G-buffer 壽命 | ✅ **【已完成－實際瓶頸修正】** | 確認 Deferred 原本每幀刪除並重建 framebuffer／三張 attachment；現在只在首次建立或 surface 尺寸改變時重建。同一 GLES 3.1 VM、75,240 vertices 由約 19 FPS 改善為 58–60 FPS，切換首次約 55 ms，後續多為 5–10 ms，沒有 G-buffer／shader error。 |
| Physics collider 駐留 | ✅ **【已完成－本輪優化】** | 玩家碰撞仍由 `PlayerController` 精確處理；BEPU 的玩家預熱半徑由 3 Chunk 縮為 1，動態 Grid／球體仍按自身完整 bounds 保留碰撞。模擬器同一 Physics Grid 診斷由 `2,409 bodies / 22–26 FPS` 改善至穩態 `755 bodies / 38–39 FPS`。 |
| Android x64 實際執行 | ✅ **【已完成】** | Debug 與 Release 均安裝至既有 Android 11／API 30 x86_64 VM；Release 讀取既有 `level.json`、Activity resumed、無 FATAL，穩態 Classic 約 `54–60 FPS`。 |
| Android ARM64 實機 | 🟡 **【部分完成】** | arm64 Release `0 warnings / 0 errors`，已 `adb install: Success` 至 Samsung A32（ARM64／Android 13）；手機有安全鎖且螢幕保持鎖定，無權繞過，因此本輪不能宣稱實機 3D 畫面與觸控已驗證。 |
| Android Renderer／Mod parity | 🟡 **【五條 Renderer V1 可用／仍非 100%】** | Classic／PBR、GLES 3 Deferred、GLES 3 Hybrid、GLES 3.1 Compute PT 均為實際獨立後端並在同一世界出圖；Compute 尚缺 Micro Voxel／Physics Grid 解析求交與完整透明折射。Hardware DXR 為 Windows-only，不假移植；BepInEx Mod runtime、完整桌面設定與材質縮圖仍未完成。 |

## 本輪：輸入、三語 UI、Physics Grid 微方塊與 Android ImGui（2026-08-08）

> 本表是目前 Repository 的最新狀態，優先於下方 2026-08-07 的歷史表；已正常工作的舊功能沒有重做。

| 項目 | 狀態 | 驗證證據／限制 |
|---|---|---|
| Shift 蹲下與可編輯按鍵 | ✅ **【已完成－本輪】** | 桌面預設改為 WASD、Space 跳躍、Shift 蹲下、Ctrl＋前進或 0.30 秒內連按兩次前進衝刺；移動、跳躍、蹲下、衝刺、物品欄、建造、精密與暫停都可在按鍵頁修改並保存。設定 round-trip 與預設回歸測試通過。 |
| 衝刺視覺 | ✅ **【已完成－本輪】** | 衝刺時 FOV 以指數平滑方式最多增加 7 度，停止後平滑恢復；基礎 FOV 與暫時動畫值分離，不會把衝刺值寫回設定，Path Tracing accumulation 會在 FOV 改變時正確重置。 |
| Windows IME／遊戲英文輸入 | 🟡 **【程式完成－人工 IME 待複驗】** | 保留既有 ImGui composition／候選窗定位；滑鼠捕捉且沒有 UI 時啟用英文鍵盤配置並關閉 IME，使 Shift 保留給蹲下。英文 HKL 只載入一次，避免每幀重載。不同 Windows 輸入法的實際組字行為仍需使用者人工確認。 |
| 三語 UI | 🟡 **【核心導航完成】** | 已加入繁體中文、簡體中文、英文及持久化設定；暫停、成就、語言、按鍵與 Android 核心頁面已接入。舊有 Mod／世界管理／全部 Renderer 細項尚未逐字遷移，不能標成全 UI 100%。 |
| 成就列表 V1 | ✅ **【版面完成／功能預留】** | 桌面與 Android 均有可滾動、近全螢幕的四列範例；每列左 1/3 名稱、右 2/3 最多三行介紹。實際成就追蹤與解鎖尚未要求，因此未假裝完成。 |
| Physics Grid 精密放置 | ✅ **【已完成－本輪】** | 原路徑確實把微方塊呼叫成 full cell；現在依 divisor 建立精確 local bounds，支援在 Grid 上單獨選取／移除最小命中 element，不會連帶刪除鄰接整格。自測驗證 1/16 尺寸與獨立移除。 |
| Android 核心 ImGui | ✅ **【V1 已實機驗證】** | 原生 AlertDialog 已退出正常路徑；API 30 x86_64 VM 實際顯示 ImGui 暫停、成就、語言與觸控編輯頁。成就四列、三語即時切換、英文重啟保存、返回層級、暫停存檔與中央 `SHF` 均逐頁驗證；觸控編輯入口不再常駐遮擋遊戲。 |
| Android native 相容 | ✅ **【已完成－本輪】** | x64／ARM64 `libcimgui.so` 以靜態 C++ runtime 重新連結；ELF 三個 `LOAD` segment 均為 `0x4000` alignment，沒有缺少 `libc++_shared.so`。Android x64 Debug／Release、ARM64 Release 皆 `0 warnings / 0 errors`。 |
| Android VM smoke | ✅ **【已完成－本輪】** | `adb install -r` 覆蓋既有安裝且保留舊 `level.json`；x86_64 SwiftShader Classic 本輪約 33–49 FPS，程序持續出幀，無 `FATAL EXCEPTION`、`UnsatisfiedLinkError` 或 ImGui 初始化失敗。ARM64 仍只有建置證據，沒有冒充手機實測。 |
| Windows 回歸 | ✅ **【已完成－本輪】** | Debug／Release `0 warnings / 0 errors`，91/91 self-tests 通過，包含輸入／語言設定與 Physics Grid 微方塊回歸。 |
| macOS VM 捷徑 | ⛔ **【未執行－資源不存在】** | 已檢查 VirtualBox 註冊項與 `C:\Users\USER`、`D:\` 的 `.vbox`／`.vmx`／`.vmwarevm`／`.qcow2`／`.vmdk`；只有 Android AVD，沒有可啟動的 macOS VM。未建立會誤導使用者的假捷徑；取得真實 VM 路徑後才能加入開始功能表。 |

## 本輪：跨平台共用遊戲基礎（2026-08-07）

> 狀態以本輪實際 Repository、建置與 VM 驗證為準；下方較早的 Android V1 紀錄僅保留歷史，不應覆蓋本表。

| 項目 | 狀態 | 驗證證據／限制 |
|---|---|---|
| 共用核心與平台邊界 | **【已完成－基礎】** | 新增平台能力、儲存介面與共用 `GameAction`；Android 直接編入既有 World、Camera、PlayerController、Block／Item Registry、CreativeHotbar、PlayerInventory、WorldSaveService 與 Mod API，不維護另一套假世界。 |
| Android 世界與存檔 | **【已完成－可運作】** | 已移除固定展示世界；首次建立 Natural 世界，後續讀取同一 `level.json`／Region／player 格式。API 30 x86_64 Release 實測日誌為「已載入共用世界：level.json」。具非同步 Chunk queue，每幀限制提交。 |
| Android 遊戲輸入與 UI | **【已完成－V1】** | 觸控配置、八方向鍵／搖桿、外接鍵盤、滑鼠與手把均映射至共用動作；ESC 暫停、E 物品欄、B 建造工具、V 精密狀態、原生暫停／渲染器選單可使用。實體多點觸控與實體手把仍待真機複驗。 |
| Android Renderer | **【部分完成】** | Classic 與真正的 GLES PBR Forward 可切換並渲染同一世界；本輪 Release x64 模擬器 Classic 穩態約 54–60 FPS。Deferred／Hybrid／Compute PT／DXR 會顯示明確不支援原因，沒有用 Classic 冒充。 |
| Android 建置與啟動 | **【已完成】** | `net9.0-android` Debug x64、Release x64、Release arm64 均 `0 warnings / 0 errors`；Release x64 APK 在既有 Android 11／API 30 VM `adb install -r: Success`，讀取舊世界並持續出幀，無 `AndroidRuntime` fatal。arm64 為建置驗證，尚未在實體 ARM64 手機執行。 |
| Windows x64 | **【已完成並實測】** | Debug／Release `0 warnings / 0 errors`；IDE 輸出與發佈包均 87/87 self-tests 通過。DXR／六 Renderer 的既有 Windows 驗證未被重做或替換。 |
| Linux x64 | **【已完成－基礎與 Raster／Compute】** | Ubuntu WSL VM 中 self-contained 發佈包 87/87 self-tests；Mesa/Xvfb 實際建立 GL 4.3 context，Classic、PBR、Deferred、Hybrid、Compute PT smoke 通過。BepInEx 6 CoreCLR 已修正 Unix console 啟動並由 `--mod-diagnostics` 證明 `Started: True`。 |
| Linux ARM64 | **【核心已完成／GPU 與 Mod 部分完成】** | self-contained ARM64 包在 Ubuntu VM 的 QEMU AArch64＋ARM64 sysroot 中實際啟動，87/87 self-tests 通過；尚未驗證 ARM64 GPU 視窗。MonoMod／RuntimeDetour 套件明確警告不支援 ARM64，因此 Mod hook 仍不可宣稱完成。 |
| macOS x64／ARM64 | **【部分完成】** | 兩個 RID 均成功發佈且版面完整；新增 macOS OpenGL 4.1／GLSL 410 fallback，shader-only 相容路徑在 Mesa 編譯通過。沒有可用 Apple VM／Mac，本輪未實際啟動；Compute PT（需 GL 4.3）會被能力檢測停用，DXR 為 Windows-only。 |
| Windows ARM64 | **【部分完成】** | RID 發佈與版面成功；尚未在 Windows ARM64 VM 啟動，且 MonoMod／RuntimeDetour 套件警告不支援該架構。 |
| 跨平台發佈結構 | **【已完成】** | `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 均輸出根目錄原生 apphost，DLL／runtime 位於 `Managed`，資料位於根目錄 `Content`／`ContentPacks`／`Shaders`；重複發佈會先覆寫 apphost，避免執行舊 DLL。 |
| CI 與建置腳本 | **【已完成】** | 新增 PowerShell／shell build matrix 與 Windows、Ubuntu、macOS、Android GitHub workflow。CI 設定存在不等於各平台實機結果；狀態仍以上述實測為準。 |
| 完整全平台一致 | **【尚未完成】** | Android 的水、Micro Voxel、Physics Grid 顯示與模擬已接入；仍缺桌面完整 ImGui、Mod runtime 與四條高階 Renderer。macOS 尚缺實機；Android ARM64 已安裝到 Redmi Note 7，但受安全鎖阻擋，3D 畫面與觸控仍待使用者解鎖後複驗。這些不得標示為 100%。 |

## 本輪：Android 外接輸入（2026-08-07）

| 項目 | 狀態 | 驗證證據／限制 |
|---|---|---|
| 外接鍵盤控制 | **【已完成－模擬器實測】** | Activity 直接接收硬體鍵盤事件並送入既有虛擬輸入與共用 `PlayerController`，不必先點觸控按鈕。API 30 x86_64 模擬器已驗證 `W` 會實際移動玩家，`Space`、`Ctrl`、D-pad Right 均收到正確 Down／Up。 |
| 鍵盤映射 | **【已完成】** | `WASD`／方向鍵移動、`Space` 跳躍、`Shift`／`Ctrl` 蹲下，另保留 `E`、`V`、`B`、`Escape`；重複按鍵與 Pause 釋放有防卡鍵處理。 |
| 外接滑鼠 | **【部分完成－待實體裝置驗證】** | 已接入滑鼠移動、左／右／中鍵及滾輪，並避免被當成螢幕觸控。ADB 的合成 mouse source 未送達 Activity，故目前不得標記為實體驗證完成。 |
| 外接手把 | **【部分完成－待實體裝置驗證】** | 已接入左右搖桿、D-pad、A／B／X／Y、L1 與 Start，包含搖桿 dead zone；編譯通過，但本輪沒有實體手把可做輸入驗證。 |
| 建置與啟動回歸 | **【已完成】** | Android x86_64 Debug／Release 與 Windows Debug／Release 均 `0 warnings / 0 errors`；Release APK 安裝啟動後仍可接收外接 `W`，Activity 維持 resumed 且無 FATAL。 |

## 本輪：Android 真正遊戲接入 V1（2026-08-07）

| 項目 | 狀態 | 驗證證據／限制 |
|---|---|---|
| 共用遊戲核心 | **已完成（V1）** | Android 專案直接編入既有 `VoxelWorld`、`BlockRegistry`、`PlayerController`、`Camera`、Voxel Raycast 與 Mod API；不是另一套假世界。Base Content JSON 已確認存在 APK assets 並解壓到 app data。 |
| 行動 Renderer | **已完成（基礎版）** | 新增真正 GLES 2.0 VBO／shader；使用共用材質 Albedo、環境光與方向光，固定場景為 14,988 vertices。第一輪矩陣錯誤曾產生拉伸三角形，確認後已修正，現為正常方塊幾何。 |
| 玩家操作 | **已完成（V1）** | API 30 x86_64 模擬器實測八方向移動會改變攝影機位置、右側滑動會旋轉 Camera；同一 `PlayerController` 提供碰撞、跳躍與蹲下。 |
| 方塊互動 | **已完成（V1）** | 新增準星；LMB 短按使用保留的 Press Edge，實測移除中央 Glow 方塊；RMB 實測新增 Stone，畫面與 vertex count 從 14,988 變為 15,012。 |
| Debug／Release | **已完成（可執行）** | Android x86_64 Debug／Release 與 Windows Debug／Release 均 `0 warnings / 0 errors`。Release APK 安裝成功、`mResumedActivity` 正確、無 FATAL；API 30 首次可見遊戲畫面約需 18 秒。 |
| 記憶體 | **已完成（V1 量測）** | Debug 實測約 `104,879 KB PSS / 214,464 KB RSS`；Release 穩定後約 `64,593 KB PSS / 185,544 KB RSS`，低於 1 GB 目標。 |
| 效能 | **部分完成－未達標** | SwiftShader 虛擬機 Debug 約 20–37 FPS，Release 穩定後約 11 FPS；本輪目標是先真正跑起遊戲，100 FPS 基礎光照尚未達成，且尚未在實體 ARM64 手機量測。 |
| 完整 Android 遊戲 | **尚未完成** | 目前為固定低負載測試世界；Chunk streaming、存檔、材質貼圖、水、完整物品欄、Mod runtime、六條桌面 Renderer 與 Hardware RT 尚未接入。 |

## 本輪：統一蹲下與飛行移動修正（2026-08-07）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| PC 蹲下 | **已完成** | 捕捉滑鼠時按 Ctrl 進入蹲下；玩家高度與視點下降，地面速度由 `5.2` 降至 `2.1`。頭頂空間不足時不會強制站起。 |
| 方塊邊緣保護 | **已完成** | 蹲下且接地時，水平移動會確認腳下仍有實體支撐；87/87 self-tests 中的固定平台測試確認持續前進 120 幀仍不會掉落。 |
| Android 蹲下 | **已完成（已接遊戲核心）** | 八方向鍵中央 `CTL` 會送出 `GameAction:Crouch`，並由 Android GLES 遊戲迴圈傳入共用 `PlayerController`。 |
| 飛行移動方向 | **已完成** | 飛行／noclip 的前後左右改用固定世界軸；兩名玩家分別朝東與朝上輸入相同前進操作後，位置完全一致。 |
| 回歸驗證 | **已完成** | Windows Debug／Release 與 Android x86_64 Debug／Release 均 `0 warnings / 0 errors`，全套 `87/87` self-tests 通過；Release APK 完成安裝／啟動／`CTL` 觸控輸入 smoke。 |

## 本輪：Android 可編輯觸控按鍵 V1（2026-08-07）

| 項目 | 狀態 | 驗證證據／限制 |
|---|---|---|
| 短按鍵名稱 | **已完成** | 預設顯示 `SPC`、`LMB`、`RMB`、`E`、`V`、`B`；標籤輸入與載入皆限制最多 3 個字元。移動區不再顯示 `WASD`。 |
| 八方向移動鍵 | **已完成** | 預設移動控制改為八方向 D-pad，包含四個正向與四個斜向；仍可在屬性中切換成 Joystick。模擬器實際畫面已確認八個獨立方向區域。 |
| 可編輯控制 | **已完成** | 支援編輯模式、拖曳、縮放、位置／大小／透明度／Label／Binding／Normal 或 Toggle／顯示／Z-order，以及新增、複製、刪除、恢復預設。 |
| 儲存與舊設定遷移 | **已完成** | 設定以正規化座標寫入 `files/touch-controls.json`；冷啟動後確認舊 `WASD`／長名稱自動遷移成 D-pad 與短名稱，且保留使用者移動過的 V 按鍵位置。 |
| 多點觸控與虛擬輸入 | **已完成（裝置複驗待辦）** | 實作按 pointer ID 獨立擷取、按鍵引用計數、Toggle、移動與右側視角區隔。啟動 self-test 驗證多指引用計數與斜向輸入；API 30 模擬器只可做單指 ADB 驗證，真實多指手勢仍待實體 Android 裝置複驗。 |
| 按鍵輸入 smoke | **已完成** | API 30 模擬器實際按下 `SPC`、`LMB`、`RMB`，Logcat 依序收到 `Keyboard:Space`、`Mouse:Left`、`Mouse:Right` 的 Down／Up。 |
| Android 遊戲接入 | **部分完成－後續狀態已更新** | 原先「完全未接入」的問題已不存在：現有 GLES2 固定世界可移動、轉向、破壞與放置；Chunk streaming、存檔及完整 Renderer 仍未完成。 |
| 建置 | **已完成** | `net9.0-android` x86_64 Debug／Release 均 `0 warnings / 0 errors`；Debug APK 已在既有 API 30 模擬器重新安裝並冷啟動成功。 |

## 本輪：Android Host V1 安裝里程碑（2026-08-07）

| 項目 | 狀態 | Repository／驗證證據 |
|---|---|---|
| .NET Android 建置環境 | **已完成** | 使用既有 Android SDK／NDK/JBR；新增 .NET 9 `android` workload 與缺少的 API 35 platform，未刪除既有 API 30、36.1 或 NDK 27.3。 |
| Android APK | **已完成（可安裝）** | 新增獨立 `RTXSandbox.Android`、package `com.rtxsandbox.game`，最低 API 26、目標 API 35；Debug／Release 皆 `0 warnings / 0 errors`。Release signed universal APK 為 `6,815,572` bytes，包含 `arm64-v8a` 與 `x86_64`。 |
| Android 啟動驗證 | **已完成（模擬器實測）** | 既有 `KeyViewer_API30_Min`（Android 11／API 30／x86_64）中，Debug 與 Release 均 `adb install -r: Success`；Release 冷啟動 `Status: ok`、`mResumedActivity` 正確、程序 PID `2473`，無 FATAL EXCEPTION。 |
| Android 遊戲移植 | **歷史狀態已失效－見上方 Android Game V1** | Host V1 當時只有 Activity；目前已接入共用世界、玩家核心與 GLES2 基礎遊戲，完整桌面功能仍未移植。 |
| Windows 回歸 | **已完成** | 主 csproj 排除 Android source glob；Windows Debug／Release 均 `0 warnings / 0 errors`。 |

## 本輪：Compute Path Tracing 球體與一般破壞（2026-08-07）

| 項目 | 狀態 | Repository／驗證證據 |
|---|---|---|
| 路徑追蹤解析球發黑 | **已完成（已實機驗證）** | 確認原 shader 只有漫反射直接光，並以 `(1-metallic)` 完全移除金屬直接光。現已加入有陰影遮擋的正規化金屬鏡面直接光；RTX 5050 Compute Path Tracing smoke 中心解析鐵球 33 幀，48×48 中心區平均亮度 `191.71`，不再是黑色。 |
| 球體一般破壞 | **已完成（已測試）** | `AnalyticSphereWorld.Raycast` 使用精確 ray-sphere 交點並選最近表面；球在方塊後方時不會穿透誤選。任何建造工具模式下，準星對準最近球體直接按左鍵即可移除，不需切換球體專用模式。 |
| 驗證 | **已完成** | Debug／Release `0 warnings / 0 errors`；`87/87` self-tests 全通過（含新解析球選取與刪除測試）；RTX 5050 Compute Path Tracing GPU smoke 通過。 |

## 本輪：Deferred 陰影可見度修正（2026-08-07）

| 項目 | 狀態 | Repository／驗證證據 |
|---|---|---|
| 方向光 Shadow Map | ✅ **【原本已存在－未重做】** | 2048² Balanced Shadow Map 實測深度 `0.750165–1.0`；Shadow Debug 的 342,671 個 receiver pixels 中有 8,302 個陰影像素、6,800 個低於 0.5 visibility，確認不是陰影貼圖缺失。 |
| 陰影在 Final Image 不明顯 | ✅ **【已修正－本輪】** | 已確認預設 Balanced 的 Point Light 全部繞過遮擋，且天空 ambient 完全不受太陽 visibility 影響，會在 HDR 合成後洗亮陰影。Balanced 現在讓最近 2 個 Point Light 做 screen-space shadow，最高品質為 4 個；方向陰影只適度降低 diffuse ambient，不錯誤壓暗鏡面環境反射。 |
| 驗證 | ✅ **【已完成－本輪】** | Debug／Release 均 `0 warnings / 0 errors`、87/87 self-tests；RTX 5050 上 Deferred Balanced Final Image 通過 71 幀，Shadow Debug 通過 70 幀並以 G-buffer receiver mask 驗證實際陰影覆蓋，不再只以 Shadow Map 非空冒充完成。 |

## 本輪：物品欄草方塊預覽、切換名稱與方塊資訊 HUD（2026-08-07）

| 項目 | 狀態 | Repository／驗證證據 |
|---|---|---|
| 世界草方塊材質 | ✅ **【原本已正常－未重複修改】** | 現有 Atlas 回歸驗證已確認側面綠色只在上緣；本輪未修改世界 mesh、材質生成或任何 Renderer shader。 |
| 物品欄草方塊預覽 | ✅ **【已修正－本輪】** | 已確認物品欄手動畫方塊把 Atlas 低 V 貼到側面上緣，造成草側面上下顛倒；現在只將物品欄兩個側面的 V 方向翻正，Top 面保持原規則。 |
| 切換物品名稱 | ✅ **【已完成－本輪】** | 數字鍵、滑鼠滾輪、Q、物品欄快捷格與中鍵取樣切換目前方塊後，會在快捷列上方顯示中文名稱 2.2 秒並淡出。 |
| Jade 類方塊資訊 | ✅ **【已完成－精簡為三行】** | 準星命中世界方塊、微方塊或 Physics Grid 時，頂部中文面板只顯示：方塊名稱、來源／數字 ID／namespaced key、金屬度／粗糙度；移除會遮擋畫面的其餘明細，選單開啟時自動隱藏。 |
| 驗證 | ✅ **【已完成－本輪】** | Debug／Release 均 `0 warnings / 0 errors`，87/87 self-tests 通過（含草側面 Atlas 與物品欄 V 方向回歸）；RTX 5050 上實際開啟物品欄的 Compute PT smoke 通過 33 幀，精簡後三行版 Classic HUD smoke 確認快捷列切換名稱與準星材質資訊皆提交渲染並通過 70 幀。 |

## 本輪：六條 Renderer 獨立光影設定（2026-08-07）

| 項目 | 狀態 | 實作與驗證證據 |
|---|---|---|
| 全域顯示設定 | ✅ **【原本已正確－保留】** | 渲染比例、解析度、VSync、幀率、FOV 與曝光仍位於 Renderer 專屬區塊外；本輪沒有複製六份全域渲染比例。專屬區塊統一命名為「光影設定」，只顯示目前選取管線。 |
| Classic | ✅ **【已完成－由 1 項增為 8 項】** | 光照品質、日光強度、環境亮度、材質飽和度、水面細節、水波強度、透明材質品質、距離霧濃度；全部傳入 `voxel_raster.frag.glsl`，不是只保存 UI 值。 |
| PBR Forward | ✅ **【原本已足夠－已重新驗證】** | 保留品質預設、PBR、陰影、AO、SSR、Bloom、大氣、水、近似 GI、temporal、高光／反射／陰影／水／後製及 Debug 等既有完整控制；沒有重做或塞入重複開關。 |
| Deferred | ✅ **【原本已足夠－已重新驗證】** | 保留 G-buffer、光照、陰影解析度／Bias／距離、高光、反射、AO、水、透明、霧、HDR、Bloom、Tone Mapping 與 Debug 等既有控制。Forward 的高光／反射值不再被 Deferred 透明 pass 讀取。 |
| Hybrid PBR Raster | ✅ **【已完成－由共用改為 10 項獨立設定】** | 移除 UI 的 `goto Deferred`；新增 Hybrid 自己的光照、高光、陰影、陰影解析度、反射、AO、水、透明、霧、Bloom。底層仍重用可靠的 G-buffer 程式碼，但進入 pass 前只映射 Hybrid 自己的值；調 Deferred 不會改 Hybrid。 |
| Compute Path Tracing | ✅ **【已完成－由 3 項增為 9 項】** | 反彈、每幀採樣、累計樣本、追蹤距離、直接光、天空環境、自發光、路徑反射取樣強度、Russian Roulette 起始反彈；修改會清除舊 accumulation，所有參數皆進入 compute shader。 |
| Hardware DXR | ✅ **【已完成－8 個可用控制】** | 品質、真實反射層、DXR 射線距離、陰影強度、發光照明強度、反射強度、Render Style、Infinite Mirror。新增值進入 D3D12 camera constant buffer，實際控制 primary／reflection／refraction／shadow rays 與 closest-hit shading。 |
| 未實作降噪 | ✅ **【假設定已移除】** | Hardware denoiser 尚未存在，因此不再顯示一個永遠停用的 checkbox；介面只留下明確文字，未把 placeholder 算進設定數量。 |
| 設定保存與邊界 | ✅ **【已完成】** | `settings.json` schema 升至 v11；新參數都有預設值、合法範圍 clamp 與 round-trip 回歸驗證。現有舊設定缺少新欄位時使用安全預設，不要求刪除設定檔。 |
| 實機驗證 | ✅ **【已完成】** | Debug／Release `0 warnings / 0 errors`、87/87 self-tests。RTX 5050 上 Classic、Forward Ultra、Deferred Quality、Hybrid、Compute PT、Hardware DXR 六條 smoke 全通過；DXR 為 Tier 1.1、385,074 hit pixels、257,097 reflection rays，沒有 fallback。Hybrid 實際使用自己的 4096² shadow，而 Deferred 既有預設仍是 2048²。 |

### 尚未完成

- ❌ Classic／Hybrid／Compute PT／Hardware DXR 尚未另外建立一鍵品質 preset；本輪目標是先讓每條管線有 5～10 個可用且獨立的細項。
- ❌ Hardware denoiser 仍未實作，沒有以無效設定冒充完成。
- ❌ 全域曝光目前刻意共用；這符合「整體渲染強度放在管線外」的要求，未拆成六份。

## 本輪：PBR Forward 高品質 Shader Pack 視覺升級（2026-08-07）

### 現況審計與完成狀態

| 項目 | 狀態 | Repository／RTX 5050 證據 |
|---|---|---|
| 舊 Forward 骨架 | ✅ **【原本已存在－已重新驗證】** | 原路徑與 Classic 共用單一 `voxel_raster.frag.glsl` 並直接輸出視窗；Forward 已有程序 atlas、簡單方向光、鏡面與透明材質，但沒有 shadow pass、local emissive lighting、HDR composite、AO、SSR、Bloom、霧或 temporal history。Classic、Deferred、Compute PT 與 Hardware DXR 未重新實作。 |
| 真正 Forward 核心 | ✅ **【已完成－本輪】** | opaque voxel 與解析球體仍在 Forward geometry pass 內計算材質、Cook-Torrance 直接光、天空環境光與局部發光方塊光；新增的 normal／roughness attachment 只供後製 AO／SSR／除錯，不建立 Deferred fullscreen lighting pass。 |
| 黑色斑點／數值穩定 | ✅ **【已完成－本輪】** | 已確認舊 shader 對退化的 half vector、tangent 與 view vector 直接 `normalize`，可產生 NaN。現在統一 safe-normalize、GGX／Smith epsilon、roughness clamp 與 finite guard；AO／陰影使用幾何法線，`NanInfinity` Debug View 可直接定位異常。四檔 smoke 讀回 active surface 與 HDR 均為 `0` invalid pixel。未宣稱所有歷史黑點只可能有這一個來源。 |
| 太陽、陰影與局部光 | ✅ **【已完成－本輪】** | 重用既有 static／dynamic directional shadow cache，Forward 支援 1024²～4096²、距離、constant／normal／slope bias 與可調 PCF；最高品質實機 shadow depth `0.698492–1.0`。最多 16 個既有 emissive light proxy 實際進入 Forward PBR 光照。 |
| 材質與環境光 | ✅ **【已完成－本輪】** | 共用 atlas 的 albedo、detail normal、metallic、roughness、AO 與 emission 進入 GGX／Smith／Schlick metallic workflow；加入晝夜方向光、半球天空環境光與明確標示為 approximation 的地面反彈 GI。 |
| HDR 後製鏈 | ✅ **【已完成－本輪】** | 新增 RGBA16F HDR color、RGBA16F geometric normal／roughness、Depth24 及雙 RGBA16F history／resolved buffer；後製依序處理天空／雲、screen AO、SSR、距離／高度霧、光束、Bloom、Reinhard／Filmic／ACES、temporal reprojection 與 sharpen。 |
| 水與透明材質 | ✅ **【已完成－本輪更新】** | 保留排序後的 Forward transparency pass；水增加多層波浪法線、Fresnel、scene refraction、天空反射、深度／距離吸收色與太陽 sparkle。玻璃／水不改走 Deferred lighting，也未動 Compute PT／DXR 的透明射線。 |
| 反射策略 | 🟡 **【部分完成－限制已標示】** | SSR 具 ray march、深度厚度、距離／邊緣／粗糙度 confidence；螢幕外或無命中資料時回退天空環境，不輸出黑色。它不是 ray tracing、沒有離屏 scene probe，也沒有多次反射。 |
| 中文品質設定 | ✅ **【已完成－本輪】** | 提供「低／中／高／極高／自訂」及 PBR、陰影、AO、SSR、HDR Bloom、大氣、進階水、近似 GI、時域抗鋸齒開關；陰影解析度／距離／柔和度與反射、AO、水、大氣、後製、Tone Mapping 均可調。Hardware RT 入口未移除。 |
| Debug／診斷 | ✅ **【已完成－本輪】** | `F4` 與設定頁可看 Final、Albedo、Normal、Linear Depth、Roughness、Metallic、Emission、AO、Shadow、Direct、Ambient、SSR、Approximate GI、NaN/Infinity。F3 顯示 CPU submission、非阻塞 OpenGL GPU timestamp、buffer、light 與 shadow 資料；strict smoke 會讀回 HDR／surface／depth。 |
| 相同世界 A/B 與效能 | 🟡 **【可重複驗證；未達所有效能目標】** | `F6`／`F7` 保留同一世界、相機與時間切 Classic／Forward。固定 adversarial 世界最高設定：Classic 平均 `349.50 FPS`、1% low `131.97`、最慢幀 `19.49 ms`；Forward Ultra 平均 `343.57 FPS`、1% low `115.03`、最慢幀 `16.07 ms`；Compute PT 50 bounce／8 sample 對照為平均 `27.00 FPS`、1% low `0.39`、最慢幀 `5149.40 ms`。Forward 壓力測試 GPU pass 最大值 shadow／main／transparent／post 為 `1.095／1.133／1.120／10.229 ms`；穩態 Release smoke 約 `0.05／0.49／0.04／0.39 ms`。平均 FPS 很高，但 Forward 1% low 未達 150，不能宣稱全程穩定 120 FPS。 |

### 實機驗證

- ✅ Debug／Release：`0 warnings / 0 errors`；87/87 self-tests 通過。
- ✅ RTX 5050：Potato／Medium／High／Ultra 皆執行真正 Forward；Ultra 為 4096² shadow、370,587 active surface pixels，invalid surface／HDR `0／0`，framebuffer 非黑畫面。
- ✅ Normal、SSR、NaN/Infinity Debug View 均在實機跑過；SSR 通道有非零結果，NaN 通道無異常像素。
- ✅ Classic、Deferred Quality、Compute PT、Hardware DXR Release smoke 全通過；DXR 仍回報 Tier 1.1、BLAS／TLAS／Shader Table／DispatchRays，沒有被 Forward 取代或 fallback。
- ✅ 效能測試建立臨時世界並於每輪刪除；測後 benchmark world／IO 目錄皆為 0 個殘留世界。

### 本輪刻意未假完成

- ❌ 尚無 Cascaded Shadow Map；Forward 目前是一組 static／dynamic directional shadow，local emissive proxy 尚無各自 shadow map。
- ❌ SSR 只能反射螢幕內資料；尚無 reflection probe、cubemap capture 或光追式離屏反射。
- ❌ `Approximate GI` 是廉價地面反彈，不是 SSGI、voxel GI 或多重反彈 GI。
- ❌ 程序雲不是 volumetric cloud；光束也不是完整 volumetric scattering。
- ❌ Temporal reprojection 有 history、重投影與 neighborhood clamp，但尚無 jitter、motion vector／reactive mask，因此不是完整 production TAA。
- ❌ 尚無 Parallax／POM、weather wetness、lens flare、DOF、motion blur、SMAA、CSM、獨立 reflection probe editor。
- ❌ 本輪有 deterministic framebuffer／buffer readback、GPU smoke 與同場景快捷切換；尚未自動保存 Basic／Forward A/B 截圖，也未把主觀「像某一特定 Shader Pack」當成已自動驗收。

## 本輪：Deferred Rendering 穩定性與可驗證品質（2026-08-07）

### 現況審計

| 項目 | 狀態 | Repository／實機證據 |
|---|---|---|
| 真正 Deferred 路徑 | ✅ **【原本已存在－已重新驗證】** | OpenGL 4.3 使用 Directional Shadow → opaque Depth Pre-Pass → 5-target G-buffer → fullscreen PBR Lighting → HDR Post → forward 水／玻璃 → UI；不是 Forward Shader 改名。Hardware DXR 仍是獨立 D3D12 路徑，未在本輪修改。 |
| G-buffer | ✅ **【已完成－本輪更新】** | World Position 的 alpha 保存 `Material ID + 1`；Albedo/Roughness、detail Normal/Metallic、Emission/Selection、Motion Vector 保持。Motion attachment 擴為 RGBA16F，BA 以 octahedral 編碼未擾動 geometric normal，AO／shadow bias 不再錯用程序法線紋理。 |
| 黑色斑點原因與修正 | ✅ **【已完成－本輪】** | 已確認舊 Cook-Torrance 對 `view + light == 0` 直接 `normalize`，可產生 NaN；舊 SSAO／shadow receiver offset 又使用擾動 detail normal，會把同一平面的材質顆粒當遮擋／偏移。現已加入 safe-normalize、BRDF epsilon／roughness clamp／finite guard；AO 只使用 geometric normal 並拒絕同平面樣本，移除無 history 時會爬動的 per-pixel noise；shadow bias 也只使用 geometric normal。Debug View 期間另停用 forward 透明覆蓋，確保通道不被水／玻璃污染。 |
| 方向陰影 | ✅ **【已完成－本輪更新】** | 保留 static／dynamic 雙層 shadow cache；新增 1024²／2048²／4096²、48～160 格距離，以及可調 constant bias、normal bias、slope bias、PCF softness。最高品質 smoke 實際產生 4096² 深度範圍 `0.693003–1.0`，不是空圖。 |
| PBR／環境／反射 | ✅ **【已完成－本輪更新】** | GGX／Smith／Schlick 改為數值安全、能量守恆 metallic workflow；天空 IBL fallback 保留。SSR 新增 thickness、4-step refinement、edge／distance／roughness confidence，不命中仍回退環境反射，不輸出黑洞。 |
| AO／大氣／穩定性 | ✅ **【已完成－本輪更新】** | SSAO 改為 geometric-normal hemisphere/range/normal weighting；天空、霧色與前向散射隨晝夜改變；Post Quality 2+ 加入無 history 的 FXAA，避免 TAA 拖影與歷史污染。Bloom／HDR／ACES、發光方塊 point-light proxy、forward 水／玻璃維持可用。 |
| 預設與中文設定 | ✅ **【已完成－本輪】** | 新增「效能／平衡／品質／自訂」；陰影分別為 1024²／2048²／4096²、距離 72／100／140，並同步調整 PBR、AO、SSR、霧、水與後製品質。個別修改會正確切到「自訂」。 |
| Debug View | ✅ **【已完成－本輪】** | 19 種實際通道：Final、Albedo、Normal、Linear Depth、Metallic、Roughness、AO、Emission、Lighting、Motion、World Position、Direct、Indirect、Shadow、SSR、Reflection Confidence、NaN/Infinity、Material ID、Light Count。`F4` 循環，右上角中文顯示目前通道。 |
| 診斷／效能 HUD | 🟡 **【部分完成－本輪有進度】** | Strict smoke 會讀回 Position／Normal／Albedo／Motion／HDR，檢查 finite、法線長度、roughness；F3 顯示 FPS、frame time、各 pass CPU submission、triangle、visible Chunk、light、G-buffer／history memory。❌ 尚未加入非阻塞 GPU timer query、draw-call 精確計數、獨立 SSAO／SSR／Bloom GPU 時間。 |

### 實機驗證

- ✅ Debug／Release Build：`0 warnings / 0 errors`；87/87 self-tests（含 Deferred shadow 參數保存與 preset profile）通過。
- ✅ RTX 5050 Release Quality：16 Point＋1 Spot、4096² shadow、342,671 active G-buffer pixels，invalid G-buffer `0`、invalid HDR `0`、normal length `0.999–1.000`、roughness `0.043–1.000`，framebuffer average `214.93`。
- ✅ Performance／Balanced／Quality 同場景皆通過：陰影分別 `1024²／2048²／4096²`；framebuffer average `195.03／207.66／214.93`。Release Hybrid 最高品質亦通過，證明共用 Deferred opaque path 未被破壞。
- ✅ Debug AO、Shadow、NaN/Infinity 通道均在最高品質實機執行；AO 舊版基準 average `244.65`，新 geometric-normal kernel `249.14`，且最終 HDR finite readback 為 0 invalid pixel。

### 本輪刻意未假完成

- ❌ 尚無 Cascaded Shadow Map；Point／Spot 仍是 screen-space contact shadow，未建立 cubemap／spot shadow atlas。
- ❌ 尚無烘焙 Reflection Probe、prefiltered cubemap／BRDF LUT、獨立半解析度 AO buffer＋bilateral upsample、Hi-Z pyramid、獨立多層 downsample／upsample Bloom。
- ❌ 尚無 TAA／history／reactive mask，因此沒有宣稱 Temporal Accumulation；本輪使用 FXAA，`Motion Vector` 僅供 Debug 與後續 TAA。
- ❌ 尚無真正 Volumetric Fog、SSGI、Tiled／Clustered Deferred、OIT、Parallax、GPU profiler；TAA History 與 Overdraw Debug View 也未用假資料填補。

## 本輪：解析球體地板穿透修正（2026-08-07）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| 球體等待腳下 Chunk collider | ✅ **【已完成－本輪】** | 已重現球體在非同步 collider 提交前先受重力、直接穿過已知實心地板；現在若球體緊鄰實心支撐，會先保留放置位置並優先建立該 Chunk collider，提交後才建立動態剛體。 |
| 球體移動範圍碰撞常駐 | ✅ **【已完成－本輪】** | collider residency 與建立優先級已納入每顆解析球體所在 Chunk；球離開玩家中心範圍後，不再因地板 collider 被回收而掉出世界。 |
| 高速地板穿透保護 | ✅ **【已完成－本輪】** | 解析球體由被動式接觸改為 BEPU continuous sweep，避免高速下落時跨過一格厚地板。 |
| 驗證 | ✅ **【已完成－本輪】** | 新增回歸測試，先在舊實作確認失敗，修正後 87/87 self-tests 通過；Debug／Release 均 0 warnings、0 errors；RTX 5050 上 Release Classic GPU smoke 實際顯示 72 frames 通過。 |

## 本輪：撤回程序造型工具／解析球體（2026-08-07）

| 項目 | 狀態 | 實作與驗證證據 |
|---|---|---|
| 程序斜面／曲面建造工具 | ✅ **【已撤回－問題已不存在】** | 已移除上一輪新增的 `ProceduralShapeEditor`、工具模式、UI、輸入與測試入口；目前程式碼搜尋（排除本段文件紀錄）為 0。水流、游泳跳躍及既有精密建造不受影響。 |
| 可推動解析球體 | ✅ **【已完成－本輪】** | 新增 world entity 與 BEPU 原生 `Sphere` 剛體；玩家接觸以有速度上限的連續位移／速度傳遞推球，無瞬間傳送。鐵球與完美鏡面球可在既有 B 工具箱直接放置，半徑 0.25～2 格，亦可移除最後放置球體。 |
| 無多邊形球面渲染 | ✅ **【已完成－本輪】** | Compute PT 使用精確二次方程 ray-sphere intersection；Hardware DXR 使用共享單位 AABB BLAS、Procedural Primitive Hit Group、Intersection／Any Hit／Closest Hit Shader，球移動只更新 TLAS transform；Raster 使用保守 billboard proxy，但每個 fragment 重新求解析球面命中、法線與深度，proxy triangle 不代表球面。 |
| 球面反射 | 🟡 **【部分完成－管線差異已明確】** | Compute PT／Hardware DXR 的鐵球與鏡面球會由真實球面法線發出既有反射射線；Raster 依解析法線做 PBR Fresnel、環境與 screen-space scene reflection。Raster 尚不是離屏全場景遞迴反射，未標記為真 DXR。 |
| 存檔 | ✅ **【已完成－本輪】** | Directory world 的 `physics.bin` 升級至 v2，保存球體中心、半徑、材質、線／角速度與睡眠狀態；讀取仍向下相容 v1 Physics Grid 檔。 |
| 驗證 | ✅ **【已完成－本輪】** | Debug／Release 均 `0 warnings / 0 errors`；86/86 self-tests 通過（新增球體推動、重力與存檔 round-trip）；RTX 5050 上 Classic、Forward、Deferred、Hybrid、Compute PT、Hardware DXR 六條 GPU smoke 全通過，Release Hardware smoke 亦通過。Hardware 證據包含 DXR Tier 1.1、1 個 analytic sphere、procedural BLAS／第二 Hit Group、6 次 dynamic update、5 次 TLAS update，framebuffer 通過且未 fallback。 |

## 本輪：Visual Studio 2022 與可選 .NET 10 Host（2026-08-07）

| 項目 | 狀態 | 實際內容／證據 |
|---|---|---|
| VS 2022 載入失敗 | ✅ **【已修正－本輪】** | 已確認本機為 Visual Studio 2022 `17.14.37`、系統 SDK `9.0.316`；原本直接宣告 `net10.0` 會被 VS 拒絕。主專案現在預設 `net9.0`，VS 不需 18.0 即可載入與偵錯。 |
| 可選 net10 Host | ✅ **【正式保留】** | 專案不把 net10 列為 VS 可見的多目標，而是以 `RTXSandboxHostTargetFramework` 作明確 opt-in；具 .NET 10 SDK 時可用 `-p:RTXSandboxHostTargetFramework=net10.0` 建置。預設 net9 runtimeconfig 亦使用 `LatestMajor` roll-forward。 |
| Mod／輸出目錄 | ✅ **【已同步】** | Mod API 維持 `netstandard2.1`、Example Mod 維持 `net6.0`；Example DLL 會跟隨目前 Host TFM 複製到 `bin/<Configuration>/<HostTFM>/BepInEx/plugins`，不再固定丟進 net10 目錄。 |
| 驗證 | ✅ **【已完成－本輪】** | VS 17.14 內建 MSBuild 直接 Restore／Debug Build 成功；系統 SDK 9.0.316 的 Debug／Release 均 `0 warnings / 0 errors`、86/86 self-tests、BepInEx 載入 net6 Example Mod，RTX 5050 六 Renderer smoke 全通過（Classic 68、Forward 70、Deferred 67、Hybrid 68、Compute PT 33、DXR 78 幀）。獨立 SDK 10.0.302 的 opt-in Release 亦零警告、86/86、BepInEx 啟動成功；最後已將 NuGet assets 恢復為 VS 預設 net9。 |

## 本輪：水面細節與共用流量資料（2026-08-07）

| 項目 | 狀態 | 實作與驗證證據 |
|---|---|---|
| 16 單位有限水量 | ✅ **【已完成－本輪更新】** | 每格水保存 1～16 單位；完整水面為 0.875 格，最薄層為 0.0547 格。向下、水平與壓力轉移均守恆，不再以「距離水源」複製水方塊。流量資料由 `VoxelWorld` 共用。 |
| 水面與岸邊形狀 | ✅ **【已完成－本輪】** | Raster 與 DXR 會為薄水建立個別頂面，並只在相鄰水面較低或接觸空氣／透明材質時建立露出的高度差側面；既有完整海面仍走 greedy mesh，沒有把整片海拆回逐方塊面。 |
| 六條 Renderer 一致性 | ✅ **【已完成－本輪】** | Classic、Forward PBR、Deferred、Hybrid PBR 共用分級水面 mesh；Compute PT 新增 R8UI fluid-level 3D texture；Hardware DXR 將相同高度寫入 BLAS 幾何。GPU smoke 的固定場景加入 0～5 級水面。 |
| 方塊排水 | ✅ **【已完成－本輪更新】** | 實心方塊覆蓋水格時會保留原水量：優先向下排放，再沿相連水體尋找出口；水平溢出時先形成 1/16 薄層。搜尋限制 2,048 格且按幀預算提交，找不到出口的剩餘水量會保留佇列等待空間。 |
| 連通管／靜水壓平衡 | ✅ **【已完成－本輪更新】** | 小型相連水體會比較最高與最低水頭，從高液面逐步轉移到低液面，包括經底部滿水管把另一側水柱頂起。大型海洋超過 512 格時不做整體 flood-fill，只保留局部流動。 |
| 存檔相容性 | ✅ **【已完成－本輪】** | Region v2 保存稀疏水量級別並可讀取 v1；部分水格重開後不會膨脹回完整水塊。 |
| 驗證 | ✅ **【已完成－本輪更新】** | 目前完整套件為 85/85 self-tests，水相關驗證包含有限水量守恆、實心方塊排水、封閉 U 型連通管液面平衡，以及最薄／完整水層使用相同出水跳躍速度。RTX 5050 實機六條 Renderer smoke 全通過；DXR 未回退。 |
| 後續高階流體 | 🟡 **【部分完成－後續】** | 尚未做流速向量、黏度、浮力耦合、泵／活塞、跨未載入 Chunk 持續流動與大型海洋全域壓力；本輪沒有假裝是完整 Navier–Stokes。 |

## 本輪：DXR 封閉漏光、夜晚、彩色透光與水流（2026-08-06）

| 項目 | 狀態 | 目前實作與驗證證據 |
|---|---|---|
| Perfect Mirror 封閉空間漏光／亮線 | ✅ **【根因修正＋專用實機回歸】** | 修改前確認 DXR Miss 永遠輸出白天藍天、CPU 夜間會把主光翻回地面上方，Closest-Hit 還無條件加入 `0.12–0.25` 固定環境光；密閉空間因此不需要任何有效光路也會亮。現在 Sun constant 帶真正 daylight，夜晚 Miss 接近黑色，固定環境光改為隨 daylight 收斂的極低能量，直接光與 LED 仍必須通過正式 DXR visibility ray。新增 `--gpu-smoke-hardware-dark-room`：午夜、Reference、12 次真反射、Perfect Mirror 六面封閉，RTX 5050 Release 實測 518,400 hits／518,400 reflection pixels，中心亮度 `4.20/255`（門檻 18）。 |
| Hardware RT 夜空不黑 | ✅ **【已完成－本輪】** | Miss Shader 由固定天空改為 day／night HDR sky 依太陽高度連續混合；午夜不再顯示白天藍天，月光 direct strength 僅保留微弱能量。密閉測試與一般 Infinite Mirror smoke 均通過。 |
| 發光方塊是真正射線可見光 | ✅ **【既有真 DXR 路徑保留並重新校正】** | 修改前已存在 emitter→receiver DXR shadow／visibility ray，本輪沒有重做成假 emission。現在可見發光面峰值由 `3.2` 收斂至 `1.55`，燈光作用半徑由最多 9 格放寬到 12 格，照明乘數 `1.35→1.05`，避免光源本體閃白但周圍沒有光源感；不透明物阻擋，透明 Any-Hit 累積 RGB transmission。Release Infinite smoke 實測 404,510 shadow rays、394,408 occluded pixels、32 emissive lights。 |
| 彩色玻璃染色光路 | ✅ **【已完成－Hardware DXR】** | GlassInterfaceFilter 改為較強的 Beer-style RGB absorption；方向光與發光方塊 visibility ray 每穿過一個玻璃界面都乘上實際 atlas tint，而不是只讓玻璃表面著色。RTX 5050 Release 的紅玻璃／鏡廳測試取得 `134,646` chromatic transparent pixels，138,437 transparent hits，未 DeviceRemoved。Raster shadow map 尚不是彩色 shadow，因此此完成狀態只屬 Hardware DXR。 |
| 馬賽克玻璃顏色太淡 | ✅ **【已完成－本輪】** | 4×4 mosaic 色塊改為更深、更飽和的紅／綠／紫／藍組合，基礎 albedo 下調，transmission `0.74→0.70`；格線、粗糙度與既有 4×4 顆粒層級保留。 |
| 水可流動 | ✅ **【Gameplay Flow V1 可運作】** | 新增有固定 0.12 秒步進與每步最多 20 次修改的水流：優先向下、受支撐後橫向擴散最多 5 層；移除玩家放置的源頭會回收其追蹤支流。只在已載入 Chunk 內流動，不會在邊界同步生成世界；世界載入／重建會清狀態。Raster 新增外側水牆但仍剔除水－水內部面，所以瀑布可見且不會回到每個水方塊四面接縫。自動測試實際驗證下落、橫流與回收；Classic／Deferred GPU smoke 通過。V1 尚未儲存 Minecraft 式 0–7 水位高度，流動格使用目前統一的 `0.875` 水面。 |
| 建置／回歸 | ✅ **【已完成－本輪】** | .NET 10 Debug／Release 均 `0 warnings / 0 errors`；79 項 self-test 全過（含新增水流測試）。RTX 5050 Release：Classic、Deferred、DXR Dark Room、DXR Infinite Mirror 全通過；Infinite 真 DXR 29 層＋近似 971 層仍可運作，DispatchRays GPU `44.08 ms`。 |

## 本輪：玻璃頂面穿牆／透視回歸（2026-08-06）

| 項目 | 狀態 | 目前實作與驗證證據 |
|---|---|---|
| 第一次「遞迴終端忽略牆面」修正 | ♻️ **【確有問題，但不足以解決回報】** | 到達反射上限後，舊 Any-Hit 在 Wax／Reference 模式確實會把可反射不透明表面 `IgnoreHit()`；此錯誤已移除，但使用者複驗證明 Default 仍可從玻璃頂面穿牆，因此不得把第一次修正繼續標成完整根因。 |
| Hardware DXR 玻璃緊貼實心牆 | ✅ **【第二根因已修正並建立專用回歸】** | 折射射線離開玻璃時，舊程式把起點沿表面法線推進 `0.035` 格；玻璃與牆共面時會直接從牆內開始，而牆的內部面已由 mesher 正確剔除，造成與 Render Style 無關的透視。Payload 現在記住發射射線的 instance＋primitive；子射線從入射側 `0.002` 格開始，只在 Any-Hit 忽略原玻璃三角形，與它共面的不透明牆仍是有效 Closest-Hit。 |
| Raster 玻璃輪廓旁透視 | ✅ **【根因已修正】** | 普通／特殊玻璃不再偏移 Scene Color UV 或跨鄰近像素模糊；這些 screen-space 取樣會越過牆面 silhouette，顯示實際不可見的鄰近背景。水仍保留動態折射，玻璃表面差異改由共用法線細節、粗糙度、Tint 與 Fresnel 呈現。 |
| 實機回歸 | ✅ **【專用場景通過】** | 新增 `--gpu-smoke-hardware-glass-occlusion`：固定俯視「Clear Glass → 緊貼 Light Absorber 實心層 → White LED」，強制 Hardware RT Default、品質≥2、3次反射。RTX 5050 實測牆存在時中心亮度 `34.35`，移除牆後才升至 `222.48`，`233,440` transparent hits、99 frames；測試若兩者差距不足會直接失敗。另以29層 real DXR＋1000層尾端得到 `150,177` transparent hits、`145,199` chromatic pixels、103 frames，未 fallback／DeviceRemoved。 |

## 本輪：DXR LED 漏光與共用選取框反射（2026-08-06）

| 項目 | 狀態 | 目前實作與驗證證據 |
|---|---|---|
| RTX LED 外殼漏光／彩色描邊 | 🟡 **【正式修正已接入／指定擺法待目視複驗】** | 已確認原本局部光使用擾動後法線判斷受光，法線貼圖可能把幾何背面的光誤判為正面；現在先以真正 geometric normal 阻止背面受光，再使用實際 DXR visibility ray。射線起點偏移由 `0.045` 收斂到 `0.012`，`TMin` 由 `0.025` 收斂到 `0.003`，並在發光方塊實體邊界前停止，不再忽略途中所有發光表面。RTX 5050 Debug／Release strict smoke 均通過，最終 Release 實際得到 `147,695` 個被遮擋 shadow pixels；但尚未保存「LED 四面封閉」專用前後截圖，因此不把使用者指定畫面驗收冒充完成。 |
| LED 太亮／閃白 | ✅ **【已完成－V1】** | DXR 局部燈最大半徑 `18 → 9`、最大強度 `8 → 2.5`、shader 乘數 `3.2 → 1.35`；可見發光面使用有界 HDR emission，而不是直接把 8～12 倍 emission 全量送入 tone mapper。沒有降低解析度、反射次數或 Renderer 品質。 |
| 紅色純度 | ✅ **【已完成－本輪】** | 發光面與局部燈都以最大色頻正規化後做 `pow(chroma, 1.65)`，壓低紅燈原資料中的少量綠／藍尾巴，同時保留白光與暖光的相對色調。 |
| Hardware DXR 選取框消失 | ✅ **【已完成－本輪】** | 問題只存在於 DXR：原本未把 selection 傳入 root constants／Closest-Hit。現在 static voxel 與 Physics Grid 都攜帶可辨識的 cell／Grid ID，Closest-Hit 會在所選面的實際 UV 邊緣產生選取框；次級反射射線命中同一方塊時也會取得選取框。 |
| 共用「選取框參與反射」設定 | ✅ **【已完成－本輪】** | 新增中文共用設定，預設開啟，不屬於任何單一 Renderer。Compute Path Tracing 依 bounce 決定是否帶入；Deferred／Hybrid 以 G-Buffer selection mask 決定 SSR 是否帶入；Hardware DXR 依 payload depth 決定。關閉時仍保留相機直接看到的選取框。設定 schema 已升至 V8 並有 round-trip 回歸測試。 |
| Deferred 反射 | ✅ **【問題原本不存在／已驗證】** | 修改前已具 environment reflection、Fresnel、roughness／metallic 與 SSR；本輪未重做。更新 selection mask 後 RTX 5050 Deferred smoke 通過：feature mask `1023`、16 Point Lights、1 Spot Light、50 shadow draws、framebuffer `221.27`。 |
| PBR 金屬反射 | ✅ **【問題原本不存在／已驗證】** | Forward PBR 修改前已用 metallic albedo 作 F0，並依 Fresnel、roughness 與反射品質加入天空環境反射；反射品質預設為 2。沒有因重複需求重寫既有實作；RTX 5050 Release smoke 通過，43 visible／6 culled chunks、73 frames。 |
| 建置／執行驗證 | 🟡 **【功能驗證完成／測試環境一項受限】** | Debug／Release 均 `0 warnings / 0 errors`；RTX 5050 Hardware DXR、Deferred、Compute PT smoke 全部通過。79 項 self-test 中 78 項通過；唯一未執行成功的是無關的 ModWorkspace 編譯測試：原使用者 NuGet 設定被沙箱拒讀，改用隔離設定後又因網路受限而無法取得 reference packs，不是本輪渲染程式失敗。 |

## 長期架構文件現況對照（2026-08-06）

> 已依目前原始碼、專案檔、建置與測試重新核對；這裡記錄的是能力層級，不把長期構想誤標成當前 Bug。

| 長期方向 | 狀態 | 目前證據與邊界 |
|---|---|---|
| Managed 輸出、Target Framework、依賴 | ✅ **【目前雙模式】** | VS／預設 CLI 使用 `net9.0`，具獨立 SDK 時可明確 opt-in `net10.0`；兩者 App 均位於各自 `bin/<Configuration>/<HostTFM>` 根目錄，Managed DLL 集中於 `Managed`。Mod API 保持 `netstandard2.1`，Example Mod 保持 `net6.0`。 |
| Mod SDK 基礎 | 🟡 **【部分完成】** | 已有獨立 ModAPI、Example Mod、MODDING 文件、遊戲內編譯與依賴引用；尚無 `dotnet new rtxmod` 套件及正式 SDK 發布包。 |
| Chunk／Region／Streaming／背景工作 | 🟡 **【可運作基礎完成】** | 已有 16×16 Chunk、32×32 Chunk Region、負座標、背景生成、取消、距離優先、工作預算、精確 dirty 與固定 Benchmark；遠距 LOD、Floating Origin 與 Generator Mod API 尚未完成。 |
| Renderer Capability 與管線專屬設定 | ✅ **【已存在／已驗證】** | 六條 Renderer 具有能力描述，UI 依目前管線顯示設定；各設定已連接 Renderer。尚未建立通用 Render Graph 或完整 Profile override system。 |
| Raster／Compute PT／Hardware DXR | 🟡 **【主要基礎可運作】** | Classic、Forward、Deferred、Hybrid、Compute PT、DXR 均為正式後端；Forward 已有簡化 temporal reprojection，完整 jitter／motion-vector TAA、統一 Temporal Context、正式 Denoiser、升頻 SDK 與完整多重反彈 GI 仍屬後續。 |
| 共用材質與 Content ID | ✅ **【已存在／已驗證】** | 使用 namespaced key、資料驅動 PBR 欄位、程序圖集與跨 Renderer 材質；Clearcoat、Subsurface、Anisotropy、完整 Asset Database 尚未完成。 |
| BEPU Physics Grid | 🟡 **【可運作基礎完成】** | 靜態 Chunk collider、動態 compound Grid、固定步長、CCD、睡眠、旋轉、存檔與局部修改已接入；車輛／船／機械與完整 Entity 系統仍是長期功能。 |
| 存檔版本與恢復 | ✅ **【已存在／已驗證】** | 世界、Region、Chunk、Player、Physics 各自有格式版本；具原子寫入、備份恢復、舊格式遷移及 namespaced Block remap。 |
| 測試、Profiler、Benchmark | 🟡 **【部分完成】** | 87 項回歸測試、六 Renderer GPU Smoke、每輪新建後刪除世界的 Adversarial Streaming Benchmark、CPU 階段統計、DXR `DispatchRays` timestamp 與 Forward 四段非阻塞 OpenGL GPU timer 已存在；獨立 Tests project、Classic／Deferred／Hybrid／Compute 的分 pass GPU timer 與 VRAM timeline 尚未完成。 |
| Renderer 錯誤恢復 | ✅ **【本輪完成】** | 不再只處理 DXR：任何非 Classic Renderer 啟動失敗都安全回退 Classic；執行中切換失敗保留原 Renderer；未知數字 ID 不再拋例外。回歸測試與 RTX 5050 Classic Smoke 通過。 |
| 完整安全模式 | ❌ **【尚未完成】** | 尚無單一 `--safe-mode` 流程去同時停用 Mod、忽略 UI layout 並禁止自動載入世界；本輪沒有用假開關冒充完成。 |
| 模型、動畫、音訊、通用 Entity | ❌ **【尚未完成】** | 目前有玩家、掉落物與 Physics Grid 的專用流程，但尚未建立 glTF／骨架動畫／Audio／通用 Entity lifecycle。 |

## Repository 工程檢查（2026-08-06）

| 檢查項目 | 狀態 | 目前證據 |
|---|---|---|
| 輸出內容重複巢狀目錄 | ✅ **【已完成】** | 已重現並修正；Debug／Release 建置後 `Content/Content` 與 `ContentPacks/ContentPacks/ExamplePack` 均不存在，正式內容與 App／Managed DLL 完整。 |
| `.vs`、`bin`、`obj`、`Saves`、BepInEx cache 被提交 | ✅ **【問題不存在】** | `.gitignore` 已涵蓋，`git ls-files -ci --exclude-standard` 無輸出。Benchmark 正式報告保留，執行時 IO 位於被忽略的輸出目錄。 |
| 專案 TFM 分工 | ✅ **【已明確驗證】** | 主程式預設 `net9.0`、可選 `net10.0` Host，公開 Mod API `netstandard2.1`、範例插件預設 `net6.0`；Example 輸出路徑會跟隨 Host TFM。 |
| `SandboxGame` 協調範圍過大 | 🟡 **【部分完成】** | Graphics Settings 與 Streaming Benchmark 已拆成 partial 檔；其餘仍是 4,000 行級協調類別。本輪沒有功能性故障證據，因此不做高風險全面重構。 |
| 自動測試不足 | 🟡 **【部分完成】** | 現有 79 項自我測試已涵蓋 Chunk／Region 負座標、存檔恢復、Block ID remap、Micro Voxel、Physics Grid、Content override 與 Mod 編譯；尚未拆成獨立測試專案。 |
| 專案名稱造成範圍誤解 | ✅ **【保留現況】** | 屬產品命名取向而非程式錯誤；README 已明確描述六條 Renderer、Voxel、Physics、World 與 Mod 範圍，不在未授權下重新命名。 |

## .NET Host 與舊 Mod 相容性審計（2026-08-06；2026-08-07 更新）

| 項目 | 狀態 | 證據／下一步 |
|---|---|---|
| Host TFM | ✅ **【已更新】** | 2026-08-06 曾完成純 net10 Host 實驗；因 VS 2022 17.14 無法載入，2026-08-07 改為 VS 預設 `net9.0`、獨立 SDK 可選 `net10.0`。不再要求使用者升級到尚未安裝的 VS 18。 |
| Mod API | ✅ **【維持現況】** | `RTXSandbox.ModAPI` 不依賴遊戲實作，繼續使用 `netstandard2.1`，可供新舊 Runtime 共用。 |
| NuGet／Renderer 相容性 | ✅ **【實機驗證完成】** | 所有既有 NuGet 依賴已在 net10 restore；RTX 5050 Release 實跑 Classic、Forward、Deferred、Hybrid、Compute PT、Hardware DXR 六管線皆通過，DXR Tier 1.1 真正建立 pipeline／shader table／BLAS／TLAS 並 Trace Rays。 |
| BepInEx 6 on net10 | ✅ **【相容修正完成】** | 原 `6.0.0-be.785` 內含 HarmonyX 2.10.2／MonoMod 22.7，在 .NET 10 的 `PrepareMethod` 會造成 CLR fatal error。現顯式使用 HarmonyX 2.16.1／MonoMod.RuntimeDetour 25.3.4，並避開 build 785 對已移除 `PlatformHelper` 的 Paths／Console 舊入口；Debug／Release `--mod-diagnostics` 均完成 Chainloader startup。 |
| net6／7／8／9／10 測試插件 | ✅ **【逐版本實載完成】** | 同一 Example Mod 依序以 `net6.0`、`net7.0`、`net8.0`、`net9.0`、`net10.0` 編譯並複製至 net10 Host；五次皆由 BepInEx 找到 1 個插件、建立 `BasePlugin` 並完成 startup。這證明最小插件 Runtime 相容；含衝突第三方依賴的實際 Mod 仍需個別驗證。 |
| 遊戲內 Mod 編譯目標 | ✅ **【刻意維持 net6.0】** | ModWorkspace 繼續產生 `net6.0` DLL，以便單一插件可被較廣 Runtime 使用；其引用掃描本來就會略過高於 net6 的主程式實作 DLL並使用 `netstandard2.1` ModAPI。本輪 79/79 self-tests 包含實際 ModWorkspace 編譯通過。 |

## Hardware DXR：彩色透光與 Infinite Mirror Extension V1（2026-08-06）

| 項目 | 狀態 | 實作與驗證證據 |
|---|---|---|
| 彩色玻璃改變穿透光色 | ✅ **【已完成－本輪】** | DXR Any-Hit 會依實際材質圖集、UV 與厚度累積 RGB transmittance；方向光陰影與發光方塊 visibility 都使用彩色穿透率，不再將所有透明命中直接忽略。RTX 5050 Infinite smoke 實測 `71,944` 個 chromatic transparent pixels。 |
| Real DXR Stage 1–29 | ✅ **【已完成－本輪】** | 保留正式 DXR `TraceRay` 遞迴，Infinite 模式固定使用 29 層真實反射；RayGen／Miss／Any-Hit／Closest-Hit、透明／折射／發光與動態 Physics Grid 仍走現有 Hardware DXR。沒有把 30 層後近似標成 DXR bounce。 |
| 平面鏡循環與平滑交接 | 🟡 **【部分完成】** | 已偵測低粗糙、高金屬鏡面的 A→B→A 法線／間距循環，24–29 層以權重平滑交給尾端；平行鏡 smoke 已通過。小角度非平行容差已有正式路徑，但尚未完成獨立截圖驗收。 |
| Reprojected Mirror Stage 30–128 | 🟡 **【V1 可運作／視覺驗收未完整】** | 兩張 quarter-resolution `R16G16B16A16_FLOAT` Atlas 雙緩衝；使用前一幀相機姿態、鏡面間距、虛擬投影點、深度／視差近似、色彩吸收與粗糙度累積重投影。相機瞬移或大角度轉向會清歷史。尚未完成逐層 30／64／128 自動截圖與貼圖接縫量化。 |
| Analytic Tail 129–1000 | 🟡 **【V1 可運作／視覺驗收未完整】** | 使用鏡面循環的分組取樣與幾何級數能量收斂延伸至設定目標；具投影尺寸／能量終止、有限值與 UV 防護，不會真的發射 1000 層 DXR 射線。Extreme 報告為真 DXR `29`、近似 `971`。尚未以自動截圖證明 256／512／1000 層各投影區域無可見重複或硬截斷。 |
| Off／Balanced／Quality／Extreme | ✅ **【已完成－本輪】** | 設定與中文 Hardware DXR UI 已接入：Off、128、512、1000。UI 明確列出 `Real DXR Reflections: 29`、`Reprojected: 30–128`、`Analytic Tail: 129–N`，並標示後兩段為近似。 |
| 資源生命週期與污染防護 | ✅ **【已完成－本輪】** | Extension 關閉時不配置暫存紋理；開啟時建立雙 Atlas 並追蹤 UAV／SRV state；關閉後等待 GPU 並釋放 Atlas。Infinite smoke 會先驗證 Atlas 非零，再切 Off 驗證 `0 bytes`，未釋放即失敗。 |
| 真實 GPU 計時 | ✅ **【已完成－本輪】** | D3D12 timestamp query 包住 `DispatchRays`，readback 後以 command queue frequency 換算，不再以 CPU fence 等待時間冒充 GPU time。RTX 5050 Debug／Release smoke 為 `47.69／42.43 ms`；固定 Extreme 光學壓力報告的最大 DispatchRays GPU time 為 `1.1752 ms`，尾端工作整合在同一 dispatch，未偽造獨立 Tail GPU pass。 |
| RTX 5050 固定壓力報告 | 🟡 **【已執行／效能未達標】** | Debug optical／Extreme：平均 `62.90 FPS`、1% low `1.25 FPS`、p99 `511.96 ms`、最慢幀 `1,249.95 ms`、主執行緒最長阻塞 `814.29 ms`、Atlas `518,400 bytes`；世界完成後已刪除。瓶頸包含場景串流／AS 尖峰，未達本專案 100／150 FPS 目標，故不標效能完成。 |
| 完整指定驗收 | ❌ **【尚未完成】** | 尚缺第 1／8／16／29／64／128／256／512／1000 層自動截圖、29 層硬停止與 cubemap A/B 對照、旋轉物件／玩家模型的長時間時域殘影檢查，以及小角度非平行鏡專項視覺驗收。本輪沒有用 smoke 或層數計數冒充這些畫面證據。 |

## Hardware DXR emissive visibility（2026-08-06）

| Item | Status | Evidence |
|---|---|---|
| Emissive blocks leaking through opaque walls | **Completed** | Each contributing Hardware DXR emissive light now traces a visibility ray to the emitter. Opaque geometry rejects the contribution; transparent surfaces remain transmissive. |
| Raster/Deferred emissive shadowing | **Not changed** | This round addresses the reported Hardware DXR light leak only. |

## Surface glass material set（2026-08-06）

| Item | Status | Evidence |
|---|---|---|
| Existing colored glass | **Already existed / verified** | Clear, blue, red, green, yellow, purple, smoked, diamond and prismatic glass were already present and were not duplicated. |
| Existing frosted glass | **Already existed / enhanced** | The existing ID 31 material now has explicit transmission plus a frosted color/height/roughness generator; no duplicate block was retained. |
| Matte, mosaic, acrylic, ribbed and etched glass | **Completed** | Five new data-driven blocks use distinct transmission, roughness, IOR and procedural color/height/roughness textures. |
| Shared renderer detail | **Completed** | Raster uses roughness-controlled refraction blur, Compute PT applies atlas normal detail during transmission, and Hardware DXR reads per-material IOR plus atlas normal detail. |
| Camera-obscura / pinhole projection | **Not completed** | Current Hardware DXR is direct/reflection/refraction rendering, not a light-transport projection solver. Compute PT may transport indirect light but has no dedicated stable projection benchmark yet. |

## Reflection termination and bounce limits（2026-08-06）

| Item | Status | Evidence |
|---|---|---|
| RTX over-limit mirror layer | **Completed** | At the configured limit no additional reflection ray is emitted. Reflective candidates are ignored by Any-Hit so the same ray continues to real geometry/sky behind them, avoiding black output, fake lighting and the mirror's white base color. |
| Compute Path Tracing maximum bounces | **Completed** | Settings, shortcut cycle and compute shader loop now permit 1–50 bounces. |
| Hardware RTX maximum reflections | **Partially completed** | Safe, fully DXR-lit recursive limit is 29. D3D12 declares a hard maximum TraceRay recursion depth of 31; 50 needs a separate non-recursive wavefront DXR implementation. |

## 草方塊側面貼圖方向（2026-08-06）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| 草方塊側面草帶上下顛倒 | **已完成** | Procedural atlas 改為在側面 V 的上端產生草帶；self-test 直接比較側面頂端與底端色彩，防止回歸。 |

## Hardware RTX 多重反射（2026-08-06）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| 鏡面反射追蹤 DXR 世界 | **已完成** | Closest Hit 可遞迴發出反射射線；次級命中也執行 DXR 陰影與材質光照，不再只呈現無陰影的局部光照。 |
| RTX 反射次數設定 | **已完成** | Hardware RTX 設定新增 1～12 次，預設 3 次；DXR Pipeline 最大遞迴深度為 14。 |
| 打蠟模式反射強度 | **已完成** | 有效反射混合提高為原效果 1.7 倍，非金屬材質仍保留底色。 |
| RTX 5050 實機驗證 | **已完成** | RTX 5050 Laptop GPU、DXR Tier 1.1；Hardware smoke 實際走到第三次反射的像素 53,582，112 frames，OpenGL 顯示 readback 通過，無 fallback。 |
| 更高次數效能與最終畫質調校 | **尚未完成** | 允許 4～12 次，但 GPU 成本會上升；本輪未進行各反射次數的完整效能基準。 |

## 本輪：120 FPS 串流／編輯／Physics Grid 優化（2026-08-05）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| Raster mesh／GPU upload | ✅ **【已完成－本輪優化】** | CPU vertex／greedy mask 使用 pooled buffer、移除 VBO upload 的 `ToArray()`，並合併重複 dirty；比較 run 的 upload max `35.03 → 1.19 ms`，配置量約 `1.54 GB → 1.18 GB`。 |
| Static collider／Physics Grid | ✅ **【已完成－本輪優化】** | collider shape 背景建立、玩家／Grid 鄰近駐留、逐幀 eviction、full-cell cuboid greedy merge、單 Box body、adaptive CCD、低 body count 單執行緒及最多 2 個 120 Hz catch-up step 均已接入。79/79 self-tests 通過。 |
| Compute streaming | ✅ **【已完成－ring V1】** | Camera 跨一個 Chunk 時只更新 3D texture 新 edge，不再搬移整個 `64×576×64` window；最高品質比較 run 的 upload max `201.4 → 50.5 ms`、Boundary `209.4 → 64.4 ms`。 |
| DXR dynamic update | ✅ **【已完成－scratch cache V1】** | dynamic BLAS refit scratch 重用並移除重複 idle wait；RTX 5050 strict smoke 實際 Trace Rays 91 frames、Tier 1.1、無 fallback。 |
| 所有指定操作不低於 120 FPS | 🟡 **【部分完成－尚未達標】** | 舊固定 V5 數據保留於 [ROADMAP_OPTIMIZATION.md](ROADMAP_OPTIMIZATION.md)。2026-08-06 新的每輪獨立世界／最高設定測試中：Classic 解開錯誤的 120 Hz benchmark 上限後平均 `327.6 FPS`，但最慢幀仍 `56.10 ms`；Compute PT 實際 50 bounces／8 samples 為 `29.0 FPS`、最慢 `4,966 ms`；DXR Wax 光學重載平均 `116.3 FPS`、最慢 `631 ms`。因此 100／150 FPS 目標與「所有操作不低於 120」均未完成。 |

## 本輪：Adversarial Performance Suite（2026-08-06）

| 項目 | 狀態 | 實作與驗證證據 |
|---|---|---|
| 每次建立全新世界並刪除 | ✅ **【已完成－本輪】** | 每輪以固定 seed `20260805`＋唯一 run id 建立世界；成功時在報告寫入前刪除 world／Region scratch，異常退出則由六管線協調器保底刪除，下一次直接啟動也會依 owner PID 清掉中止殘留。Classic 與 DXR Wax 實跑後 `Benchmarks/Worlds`、`Benchmarks/IO` 均為空。正常 `Saves` 完全不在清理範圍。 |
| 六 Renderer 相同普通場景 | ✅ **【已完成－本輪基線】** | Classic、PBR Forward、Deferred、Hybrid PBR Raster、Compute PT、Hardware DXR 均在 RTX 5050 上完成同 seed、1／5／10／20／50／100 Chunk、邊界 route、100 放置／破壞、256-cell Grid、10,000 修改與 Region write。六輪皆產生報告，且沒有一輪因 Renderer 名稱假代其他後端。 |
| 所有渲染設定真正最高 | ✅ **【已完成－本輪】** | Benchmark 設定在建立 GameWindow 前生效：VSync off、360 Hz 測量上限、100% render scale、所有 Raster 品質 3、Compute 50 bounces／8 samples／16,384 accumulated；修正 Compute 執行端仍硬 clamp 12 的舊問題。DXR 使用目前 Pipeline 的正式上限 29（D3D12 recursion 31 預留 primary＋shadow），未假寫 50。 |
| 複雜玻璃／金屬 Chunk payload | ✅ **【已完成－本輪場景】** | optical profile 會追加 5,120 次、跨 20 Chunk 的 clear／colored／smoked／diamond／prismatic／frosted／matte／mosaic／acrylic／ribbed／etched glass，並混入 Mirror、Metal、Copper、Glow 與玻璃 Physics Grid。DXR Wax 實跑總計 `15,823/15,823` operations、0 dropped、final state valid。 |
| DXR 打蠟模式特別測試 | ✅ **【已完成－本輪實機】** | RTX 5050 Laptop GPU、DXR Tier 1.1，報告確認 `HardwareRayTracing / optical / WaxMode`；平均 `116.34 FPS`、1% low `2.33`、p99 `272.70 ms`、最慢 `631.43 ms`，39 次 AS 操作（3 static／36 dynamic／28 TLAS update）。未達 150，故只標測試完成，不標效能完成。 |
| 報告完整度 | 🟡 **【部分完成】** | JSON／Markdown 已有 average、1%／0.1% low、p95／p99／p99.9、slowest／continuous freeze／main-thread block、generation／commit／mesh／collider／upload／shadow／DXR、GC／working set、queue peaks、requested／completed／dropped／failed、final-state validation及 100／150 FPS 判定；本輪加入真正的 DXR `DispatchRays` timestamp。❌ 其他 Renderer GPU pass timestamp、VRAM timeline、per-event latency histogram仍未完成。 |
| 15 類惡意攻擊完整覆蓋 | 🟡 **【部分完成】** | 已正式覆蓋 Chunk burst／teleport、邊界 route、100 與 10,000 edits、Physics Grid、Region save、DXR AS storm、光學材質與混合壓力。❌ 10,000 dynamic bodies、CCD bullet wall、惡意 Mod event storm、並行多存檔與 10 分鐘長時混合災難仍未加入，不以現有階段冒充全部 15 類。 |
| 100 FPS 最低／150 FPS 目標 | 🟡 **【部分完成－未達標】** | 解開測試器錯誤的 120 Hz 初始化上限後，最終普通 Raster 平均為 Classic `327.6`、PBR Forward `317.0`、Deferred `319.1`、Hybrid `316.9 FPS`，但各自最慢幀仍為 `47.7～58.1 ms`，不能宣稱操作全程不低於 120。普通六管線舊基線的 Compute PT `14.5 FPS` 當時實際只跑 12 bounce；正式 50-bounce Compute broad-phase 版本為 `29.0 FPS`，但有 `4.97 s` Grid 命中尖峰。DXR optical Wax 為 `116.3 FPS`／`631 ms`。下一瓶頸是 Compute Grid 的真正 BVH／wavefront，以及 DXR per-Chunk／per-Grid BLAS＋非同步 readback。 |

### 本輪追加修正

- ✅ **【已完成】** DXR 串流期間取消過期 static geometry job 不再呼叫 `ThrowIfCancellationRequested()`，也不把 cancellation token 交給 Task scheduler。背景工作以 `null` 結果安靜淘汰，避免正常 Chunk invalidation 觸發 `System.OperationCanceledException`／Visual Studio first-chance 中斷。隔離 Debug 與正常 Release 建置皆 0 warning／0 error、79/79 self-tests 通過；RTX 5050 Hardware DXR 雙 7×7 streaming smoke 實跑 163 frames、60,308 triangles、4 次 AS build 且無取消例外、無 fallback。

## 本輪：視窗啟動不搶焦點（2026-08-05）

| 項目 | 狀態 | 驗證證據 |
|---|---|---|
| 啟動視窗不搶前台 | ✅ **【已完成－本輪】** | OpenTK 視窗使用 `StartFocused = false`，初始化時未取得焦點便不抓取滑鼠；使用者點擊後仍可正常取得焦點。Win32 前台句柄探針完整監測 Classic smoke：啟動前／結束後前台 PID 均為 `24276`，RTX Sandbox PID `20340` 從未成為前台，smoke 正常渲染 100 frames 並以 exit code 0 結束。Debug／Release 皆為 0 warnings、0 errors。 |

## 本輪：世界串流與全引擎效能工程（2026-08-05）

詳細的固定世界、1／5／10／20／50／100 Chunk、六 Renderer Before／After、驗證證據與未完成限制已集中記錄於 [ROADMAP_OPTIMIZATION.md](ROADMAP_OPTIMIZATION.md)。

| 項目 | 狀態 | 摘要 |
|---|---|---|
| 固定可重複效能測試 | ♻️ **【舊 fixture 已由本輪需求取代】** | 場景 seed、路線與操作保持 deterministic，但不再重用唯讀 v5。每輪建立唯一臨時世界，完成即刪；仍包含 Cold／Warm-up／Measured、100-Chunk、10 秒 route、100 place/break、256-cell Physics Grid、10,000-operation soak 與 Region scratch。 |
| 世界串流主執行緒尖峰 | ✅ **【已完成－V1 可運作】** | 可重用且逐提交授權的低優先 worker、Chunk 所有權直接移交、背景 Raster mesh、集中式 count-based `FrameWorkBudget`、allocation-free collider scan 與背景 autosave 已接入。六 Renderer 的 20-Chunk 隊列皆完整消化且無一秒 freeze。 |
| Raster 幾何／記憶體 | ✅ **【已完成－本輪】** | 不透明方塊與連續水頂面使用安全 greedy merge；Classic 同條件配置量 -59%、GPU mesh/upload 約 -84%，六條 smoke 證明陰影／透明／水與 framebuffer 未消失。 |
| 全部附件架構 | 🟡 **【部分完成】** | ✅ Compute ring／edge upload 已完成。❌ 正式多階段 Job DAG、可調 ms/byte budget、Region dirty payload merge、Section＋Palette、遠距 unload、DXR per-Chunk/per-Grid BLAS 與非同步 readback尚未完成；均未以介面或 TODO 假標完成。 |

## 本轮：新区块载入卡顿再修正（2026-08-05）

用户实测确认上一轮「新区块生成卡死」仍然存在，因此原本的完成标记已更正为部分完成；本轮以当前 Repository 重新检查后，确认地形计算虽已在 worker，但主线程与 Renderer 仍有以下实际热点。

| 项目 | 状态 | 当前实现与验证证据 |
|---|---|---|
| worker 结果提交 | ✅ **【已完成－本轮】** | 背景生成完成后直接把完整 `VoxelChunkStore` 所有权移交给 live world；不再先转成数万笔 sparse voxel，再于 update thread 逐格复制进第二个 16×576×16 dense Chunk。原有取消、版本隔离和每帧最多提交一 Chunk 保持不变。 |
| Raster Chunk／Grid 网格建立 | ✅ **【已完成－本轮】** | Classic、Forward、Deferred、Hybrid 的 box-face 输出改为 bit mask 与 allocation-free corner 计算；移除每方块反复建立的 face jagged arrays、normal／UV／index arrays 与捕获 lambda。Chunk CPU meshing 现使用带一格边界的不可变快照在单一背景 job 建立，主线程只做每帧最多一个完成 VBO upload；版本检查可丢弃编辑／邻接变化后的过期结果。材质、UV、法线、透明面与选择资料不变。 |
| DXR 静态／动态几何 CPU 建立 | ✅ **【已完成－本轮】** | Hardware RT 使用同一套 allocation-free face 输出与只读材质 lookup；静態普通方塊進一步用同材質、同平面的 greedy face 合併，UV 仍逐格重複，micro／水／透明保持原語義。每 Chunk 的 CPU greedy 結果已有版本化 cache，BLAS 改採 `PreferFastBuild`。標準 strict 場景由 `116,498` 降至 `1,772` triangles，最終 static／refit `5.49／5.56 ms`，命中／陰影／反射／透明／framebuffer 證據不變。Natural 雙 7×7 window 為 `60,532` triangles，最終一次 static／refit `49.42／0.79 ms`。 |
| 實際 Natural 新區塊流程 | ✅ **【已完成－本輪自動實機驗證】** | 新增 streaming smoke：先完成 Natural 7×7 window，再橫移 8 Chunk 進入完全未載入的第二個 7×7 window，等待生成、Raster 背景 mesh／upload 或 DXR AS 更新全部完成。生成改由獨立 `BelowNormal` worker 執行，避免與 render thread 爭搶。RTX 5050 最終結果：Classic 最大／平均 frame delta `13.86／8.61 ms`，Deferred `17.03／8.56 ms`；Hardware DXR `115.14／11.04 ms`，且完成正式 Trace Rays 與 framebuffer 驗證。 |
| 完整消除長距離探索尖峰 | 🟡 **【部分完成】** | Raster 的實際雙 window 流程已沒有原本 0.5～1 秒級主執行緒 terrain copy／meshing 卡死；但 Hardware DXR 仍有一次約 `115.14 ms` 尖峰，來源是同步 build 單一靜態 BLAS，再以 D3D12 readback 回傳 OpenGL。每 Chunk BLAS instance、非同步 readback、time-based GPU upload budget 與遠距 unload 仍為後續項目，不假標為完成。 |

## V1 現況稽核（2026-08-04）

這一節是後續工作的優先比對表。開始修改前仍須用目前程式碼或測試確認；不得只因這裡曾記載問題就重複修改。

狀態標記：

- ✅ **【已完成－本輪】**：本輪新增，且已經建置、測試或實際啟動驗證。
- ✅ **【已完成－原有】**：檢查時原本就可用，本輪只驗證，沒有重新實作。
- ♻️ **【舊問題已不存在】**：Roadmap 舊敘述已不符合目前程式，已依現況修正。
- 🟡 **【部分完成】**：已有正式可用基礎，但仍缺少列出的 V1 要求。
- ❌ **【未完成】**：目前沒有正式可用實作；不可用 Placeholder 或假選項標成完成。

## 本輪：15 項已重現小缺陷打磨（2026-08-05）

本節只列修改前已由目前程式路徑、回歸案例或 GPU smoke 證實的問題；沒有把排版偏好、重構建議或未來功能算進 15 項。

| # | 修改前確認到的實際問題 | 狀態與本輪結果 |
|---:|---|---|
| 1 | 玩家快速移動／傳送後，背景 worker 仍會生成舊位置整圈 Chunk。 | ✅ **【已完成－本輪】** Generation window 改為可取消的 concurrent live set；新中心會剔除舊範圍，worker 對失效 request 直接跳過。 |
| 2 | 已在主執行緒同步生成的 queued Chunk 仍顯示 pending，worker 之後還會送回重複結果。 | ✅ **【已完成－本輪】** `EnsureChunkGenerated` 會取消同座標 queued request；完成佇列也只接受仍在 live set 的結果。新增 cancellation／pending-count 回歸測試。 |
| 3 | 刪除 Chunk 最高／最低方塊會重掃 147,456 個 cell；snapshot copy 與地表查詢也固定掃完整 576 層。 | ✅ **【已完成－本輪】** 每 Y layer 維護 occupied count，邊界收縮只掃 576 個 layer counter；linear/window copy 與 surface/top query 限制在實際垂直 bounds。Regular／micro 交替邊界測試通過。 |
| 4 | Compute PT 每收到一個新 Chunk 都重傳完整 `64×576×64` voxel window。 | ✅ **【已完成－本輪】** 攝影機 window 不變時只上傳生成 Chunk 的 `16×576×16` 子區；跨 window 才完整重傳。 |
| 5 | 世界重載後，舊 BEPU static／dynamic body 要等後續 frame budget 才逐步移除。 | ✅ **【已完成－本輪】** `WorldReloaded` 立即清除舊世界 bodies、shape 與 accumulator，再排新世界 rebuild；測試在 `Step()` 前即確認 body count 為 0。 |
| 6 | BEPU 每個 physics frame 都逐 element 重算 Physics Grid topology hash，儘管已有 `TopologyVersion`。 | ✅ **【已完成－本輪】** Dynamic body signature 直接使用正式 `TopologyVersion`；移動 Grid 不再 O(element count) 重雜湊。 |
| 7 | 玩家剛放置／修改的碰撞 Chunk 會排在串流建立的大量 static Chunk 後方，數幀內沒有新碰撞。 | ✅ **【已完成－本輪】** voxel／micro edit 使用 urgent dirty queue，仍維持每幀一 Chunk budget，但玩家編輯優先；三 Chunk backlog 回歸測試通過。 |
| 8 | Deferred 會在實際 edited mesh 尚未輪到 rebuild 時先消耗 shadow dirty，之後留下舊陰影。 | ✅ **【已完成－本輪】** Shadow dirty 改在 mesh upload 真正完成時提交；有既有 shadow 時等所有可見 dirty mesh 完成才更新。 |
| 9 | Chunk streaming 每提交一個 Chunk 就重畫一次 2048² static shadow map。 | ✅ **【已完成－本輪】** 首張 map 建立後保留上一張，串流 dirty mesh 清空才合併更新；strict smoke 的 static shadow update 由逐 Chunk 更新收斂為初始＋最終共 2 次。 |
| 10 | Classic／Forward 把透明與實心幾何混在同一 depth-writing pass；Deferred 透明 pass 也仍寫 depth，Chunk 順序未按距離。 | ✅ **【已完成－本輪】** Raster 改為 opaque depth pass＋back-to-front transparent pass；透明 pass 關閉 depth write，Deferred forward transparency 同步套用。 |
| 11 | Raster／DXR 對所有 Physics Grid 固定使用 12 格 culling radius，大型結構的一部分仍在畫面時可能整體消失。 | ✅ **【已完成－本輪】** Grid 依全部 element 與質心維護真實 bounding radius，兩條 culling／shadow-caster 路徑共用；48 格寬 Grid 回歸測試通過。 |
| 12 | Deferred 中 Grid 每次物理移動都遍歷全部 element 重新找 emissive material。 | ✅ **【已完成－本輪】** 依 Grid ID＋`TopologyVersion` 快取 local emissive lights；移動時只轉換真正發光點的位置。 |
| 13 | Hardware DXR 的 static BLAS 完全沒加入 micro voxel，micro 編輯也沒有 invalidation event。 | ✅ **【已完成－本輪】** Static micro boxes 正式建立 DXR triangles 並訂閱 cell／reload invalidation；RTX 5050 strict smoke 實測 36 micro triangles。 |
| 14 | DXR 動態 refit 每次重做 exposed-face topology、掃全靜態世界找燈、重建／重配 TLAS 與 emissive buffer。 | ✅ **【已完成－本輪】** Grid local geometry／lights 依 topology cache；靜態燈隨 static build cache；emissive upload buffer 循環使用；TLAS 正式以 `ALLOW_UPDATE + PERFORM_UPDATE` 原地更新。Strict smoke：3 dynamic refits、2 TLAS updates，最後 refit 4.77 ms。 |
| 15 | DXR 水仍逐格取樣不連續 atlas 與 height detail，Raster 接縫修正沒有套到 Hardware RT。 | ✅ **【已完成－本輪】** fluid Closest Hit 使用連續水色與材質 roughness，略過逐 cell height gradient；折射、Fresnel、透明命中與水下霧保持正式 DXR 路徑。 |

驗證：79/79 self-tests；Release 主專案與 solution 0 warning／0 error；RTX 5050 Laptop GPU 上 Classic、Forward、Deferred、Hybrid、Compute PT、Hardware DXR 全部 smoke 通過。DXR 使用 D3D12 Debug Layer：Tier 1.1、116,498 triangles、36 micro triangles、96 Grid triangles、2 static builds＋3 dynamic refits、2 TLAS updates，static/refit 55.83/4.77 ms，framebuffer average 217.83、range 13–220。Deferred／Hybrid static shadow updates 為 2，shadow depth `0.76623–1.0`，非黑 framebuffer 通過。

## 本輪：Infinite World／Region Storage（2026-08-05）

| 項目 | 狀態 | Repository 現況與驗證 |
|---|---|---|
| 無限水平世界 | ✅ **【已完成－本輪可運作版】** | X／Z 不再由 `64×64` 限制；`ChunkCoordinate` 使用負數安全的 floor division，玩家周圍以半徑 3 Chunk、Seed 決定性背景惰性生成。自動測試已比較 `(-41, 73)` 遠距 Chunk 的兩個同 Seed 世界，內容完全一致。 |
| 世界垂直範圍 | ✅ **【已完成－本輪】** | 世界座標正式為 Y `-64..511`（上界 512 exclusive）；一般地形／Flat surface 最高按需求限制在 256，但資料、編輯、碰撞與保存可使用完整高度。ModAPI 新增 `MinimumY`、`MaximumYExclusive` 與 `IsInside`。 |
| 32×32 Chunk Region 存檔 | ✅ **【已完成－本輪】** | Directory save 升級為 format v4；每個 `.region` 最多包含 32×32 個 16×16 Chunk，普通 voxel、micro voxel、負座標與空白但已生成 Chunk 都在同一容器。三個不同 Chunk 的測試只產生一個 Region；正負 Region 邊界、材質 ID remap、損壞 active Region 後整代 backup recovery 均通過。舊 v3 每-Chunk目錄與 v1–v7 單檔仍可載入並於下次保存遷移。 |
| Renderer／Physics 接入 | ✅ **【已完成－本輪可運作版】** | Raster 使用實際 Chunk 高度 bounds 與 sparse snapshot 建 mesh；Compute PT 改為跟隨玩家的 64×576×64 GPU window；DXR 從當前可見／ray-neighborhood Chunk 建立世界幾何，動態 Grid 仍走獨立 BLAS refit；BEPU 靜態碰撞由 sparse column 合併建立。RTX 5050 實測 Classic 74 visible／22 culled、Compute PT 3 幀通過；DXR Tier 1.1 跑 16 幀、419,300 triangles、518,400 hit pixels、dynamic refit 1 次與 framebuffer readback 通過。 |
| 程式／檔案結構整理 | ✅ **【已完成－本輪相關範圍】** | 新增獨立 `RegionCoordinate`；Chunk 儲存集中處理世界 Y→local Y 轉換與 sparse snapshot；Build Tool／Voxel Physics 改用 `VoxelPoint` key，移除無限座標下會碰撞的舊固定 64×64 linear key；刪除已停用的逐 Chunk 寫檔器。沒有重構無關子系統。 |
| 遠距卸載／背景生成 | 🟡 **【部分完成－背景生成已完成】** | ♻️ **【舊問題已不存在】**：玩家跨 Chunk 不再同步生成整圈；單一背景 worker 會連續生成，主執行緒每幀最多提交一個完成結果，普通 `Get()` 也不再暗中生成。❌ 已走過的 Chunk 暫不卸載，GPU upload budget、LOD 與遠距 unload 仍未完成。 |

驗證：Release build 0 warning／0 error；Debug 主程式與 ModAPI 使用獨立輸出（因使用者正在執行的 Debug 遊戲鎖住原輸出）0 warning／0 error；目前完整套件 79/79 self-tests 通過；RTX 5050 上六條 Renderer GPU smoke 均通過。

## 本輪：Chunk／放置／水面／DXR 卡頓修正（2026-08-05）

| 項目 | 狀態 | Repository 現況與實機證據 |
|---|---|---|
| 新 Chunk 生成卡死 | 🟡 **【部分完成－2026-08-05 再驗證】** | 地形 noise／洞穴／植被由獨立低優先級 worker 執行；本輪移除 live world 的逐 voxel 重複製、Raster／DXR 每方塊臨時陣列，並完成 Raster 背景 meshing。Natural 雙 7×7 實測 Raster 最大 frame delta 已降至 `17.03 ms`；Hardware DXR 仍有 `115.14 ms` 單體 BLAS 尖峰，因此整體不標記為全部完成。 |
| 放置方塊卡頓 | ✅ **【已完成－本輪可運作版】** | Raster 每幀最多重建一個 dirty Chunk，且只讀該 Chunk 的 micro voxels；BEPU 每幀最多重建一個靜態 Chunk。Deferred 不再因任何方塊修改重掃全世界發光方塊，改為每 Chunk light cache，普通方塊只更新 mesh／shadow，發光變更只掃受影響 Chunk。 |
| Raster 水面方塊接縫 | ✅ **【已完成－本輪】** | Classic／Forward／Deferred transparency／Hybrid 的靜態水只建立最高層雙面 top surface，不再 alpha 疊加每格四側／底面；同類 fluid 內部面一律剔除，水色與波紋改為連續 world-space 模型，不再逐格重複取樣不連續 atlas。DXR 靜態水同樣只保留最高 top surface。 |
| Physics Grid 渲染負載 | ✅ **【已完成－本輪可運作版】** | Raster 與 DXR 共用「只保留 Grid 外露面」規則，完整相鄰 cell 的內部面不再上傳。實際使用者存檔診斷由 465,408 Grid triangles 降至 31,176（約 -93%）；固定 strict 場景為 96 triangles 且 145,898 Grid-hit pixels，證明不是把 Grid 隱藏。 |
| Hardware DXR 切換／轉視角／更新 | 🟡 **【部分完成－本輪顯著修正】** | 移除建構期與第一幀重複的完整 BLAS/TLAS；品質 1–3 使用方向無關的 235 格 ray neighborhood，轉視角不再改 mask；串流／放置變更合併後才重建，且動態 refit 不再誤清靜態 dirty flag。Grid topology／燈與每 Chunk CPU greedy geometry 已快取，TLAS 使用正式原地 update；Hardware backend 切走後保留快取。RTX 5050 final strict smoke：`1,772` triangles、2 static builds＋3 dynamic refits＋2 TLAS updates，static／refit `5.49／5.56 ms`。❌ 同步 D3D12 readback→OpenGL upload、每 Chunk／每 Grid 獨立 BLAS 與純 transform TLAS-only instance 仍是後續瓶頸。 |

## V2-A（物理、RTX、暫停與背包）

| 項目 | 狀態 | 本輪結果／仍有缺口 |
|---|---|---|
| 1. Physics Grid 擠壓玩家 | ✅ **【已完成－本輪再次降速】** | 防卡推出維持 8 個水平安全方向，但固定降為每秒 2.4 格；完全移除 Physics Grid 表面速度加成，因此高速撞击不会把防卡逻辑变成额外冲量。60 FPS 每帧最多 0.04 格，静止与 120 格／秒 Grid 的测试步长完全相同；连续推出仍可解除重叠且不向上瞬移。 |
| 2. RTX 水材質 | ✅ **【已完成－本輪】** | 水／Transmission 幾何正式進入 DXR BLAS；Closest Hit 執行 Fresnel 反射、簡化折射、Albedo tint 與水色吸收，攝影機在水中時依命中距離套用水下霧。RTX 5050 一般場景 40,889 個透明主射線命中；水下場景 242,674 個透明命中且 framebuffer range 13–238。 |
| 3. RTX 發光方塊 | ✅ **【已完成－本輪】** | 世界與 Physics Grid 的發光材質建立最多 32 個 DXR emissive lights；Closest Hit 依距離、半徑、法線與能量照亮附近表面，不再只有自發光材質。一般 smoke 實際上傳 7 個光源，水下展示場景上傳 32 個。 |
| 4. RTX 效能 | 🟡 **【部分完成－本輪更新】** | 靜態世界與 Physics Grid 已拆成兩個 BLAS／TLAS instance；同拓撲 Grid 使用正式 `ALLOW_UPDATE`＋`PERFORM_UPDATE`。Grid exposed topology、靜／動發光候選、每 Chunk CPU greedy geometry 與 upload capacity 已快取；TLAS 現在也正式原地 update。RTX 5050 final strict smoke 為 `1,772` total／96 Grid／36 micro triangles、2 static builds＋3 dynamic refits＋2 TLAS updates、static／refit `5.49／5.56 ms`。Natural 串流仍有 `115.14 ms` 單體靜態 BLAS 尖峰。❌ 每 Chunk BLAS、純 transform TLAS-only instance、非同步 D3D12/OpenGL interop 與大型 benchmark 仍未完成。 |
| 5. ESC 與暫停選單 | ✅ **【已完成－本輪】** | ESC 開啟獨立暫停選單；再次 ESC 先成功存檔才關閉；切到 Windows 背景會自動開啟暫停並釋放滑鼠。✅ 後續修正：ImGui 的一般 `WantCaptureKeyboard` 不再攔截全域 ESC，設定／模組／物品欄都可關閉；只有真正進行中的 Windows IME composition 優先取得 Escape。 |
| 6. 背包 UI 外觀 | ✅ **【已完成－本輪】** | 保留 ImGui 與原功能，改為置中的方塊／物品面板、Minecraft 風格深灰底、粗暗邊框、較方正元件、獨立內容區與一致間距。✅ 物品欄取得 ImGui keyboard focus 後仍可用 ESC 或原開關鍵 E／I／Tab 關閉；文字搜尋欄正在輸入時不會誤把 E／I 當關閉鍵。 |

## V2-B（選單、世界管理、Renderer 與 IME）

| 項目 | 狀態 | 本輪結果／仍有缺口 |
|---|---|---|
| 7. 暫停選單排版 | ✅ **【已完成－本輪】** | 固定 8 個按鈕：頂部大按鈕「繼續遊戲」、中間 2×3「設定／成就／統計／模組／語言／按鍵」、底部大按鈕「離開世界」。✅ 模組已從設定 tabs 移出，暫停選單的「模組」會開啟獨立 BepInEx／插件設定畫面；設定與模組畫面都以「完成」返回原選單。離開世界會存檔並回到世界選單，不會退出程式。 |
| 8. 世界管理 | ✅ **【已完成－本輪】** | 世界選單新增開啟、同 Seed／名稱／可調生成設定的覆蓋重新生成、只修改 display name 的重新命名，以及二次確認刪除；目前世界不可刪除。資料層測試驗證重建清除玩家修改但保留 Seed／Preset、rename 不改 storage／Seed、刪除拒絕路徑逃逸並移除目錄。 |
| 9. 所有 Renderer 基本優化 | 🟡 **【部分完成－本輪 Physics／Shadow 快取】** | 原有 1.6× 保守視錐維持不變。Raster Physics Grid 改為局部座標靜態 VBO＋每幀 model matrix，只有 `TopologyVersion` 改變才重傳網格；Deferred／Hybrid 把完整靜態世界陰影與動態 Grid 陰影拆成兩張 2048² depth map，Grid 移動不再重畫 16 個靜態 Chunk。DXR 拆出靜／動 BLAS 並對同拓撲 Grid 做 refit。❌ 每 Chunk BLAS、occlusion culling、LOD、indirect/multi-draw 與大型世界 benchmark 留後續。 |
| 10. 新 Renderer | ✅ **【已完成－本輪】** | 在 Deferred 與 Compute PT 之間新增真正可選的 **Hybrid PBR Raster（混合式 PBR 光柵）**：Deferred PBR opaque／G-Buffer lighting＋forward water/glass，固定至少品質 2，包含 shadow、SSR、HDR、Bloom、水與透明。RTX 5050 strict smoke feature mask 1023、12 direct-visible＋4 offscreen shadow-caster Chunk、非黑 framebuffer 通過。 |
| 11. 中文輸入法 | 🟡 **【部分完成－等待人工組字確認】** | 取得焦點時以 `ImmAssociateContextEx(IACE_DEFAULT)` 恢復 Windows IME context；composition／candidate 座標完整轉成視窗 client space，排除區也不再混用 screen Y；文字輸入期間遊戲鍵盤控制保持停用。自動 IME callback、多語 Unicode 與鍵盤 ownership smoke 通過。🟡 自動測試無法替代使用者實際切換微軟注音並選字，仍需在本版執行檔做最後人工確認。 |

| 需求 | 狀態 | 目前可比對結果／下一個缺口 |
|---|---|---|
| 基本遊戲流程 | ✅ **【已完成－本輪放置距離】** | 啟動、世界進入、第一人稱移動、重力／跳躍、碰撞、放置／破壞、準星、選取框與安全保存均有正式流程。本輪統一普通、精密與 Physics Grid 放置規則：眼睛到命中面的距離小於 1.1 格一律拒絕，剛好 1.1 格允許；準星預覽與實際提交使用同一判定。 |
| 方塊、材質與內容資料 | ✅ **【已完成－本輪】** | 原有 namespaced ID、Fallback、Base Content 與 16-bit Registry 已可用；本輪新增 `ExamplePack`，實際載入 `examplepack:cyan_lamp` 並由測試驗證。 |
| 世界與 Chunk | 🟡 **【部分完成－本輪背景串流】** | ✅ 16×16 Chunk-owned 資料已支援負 X／Z 與 Y -64..511；玩家周圍半徑 3 Chunk 由單一 worker 背景生成，32×32 Chunk 合併成單一 Region 檔。❌ 遠距 unload／重新載入與 LOD 尚未完成。 |
| 世界生成 | ✅ **【已完成－本輪可運作版】** | Seed、確定性地形、地質、洞穴、植被與跨長距離 Pure Nature 氣候帶可按 Chunk 在背景生成，正常地形限制在 -64..256；live world 每幀最多提交一個完成 Chunk。79 項測試包含 queued generation 非同步提交、舊 window cancellation 與最終內容驗證。 |
| Chunk Mesh 與基本效能 | 🟡 **【部分完成－本輪背景 meshing】** | Raster camera pass 已按 Chunk 建 mesh、剔除遮蔽面、分離透明幾何；Chunk CPU meshing 已迁移到单一背景 job，主线程每帧最多上传一个版本匹配结果；box-face allocation 与每 voxel Registry lock 已移除。Physics Grid 只上傳外露面與拓撲變更，BEPU 每幀只重建一個靜態 Chunk。DXR 静态方块已使用 greedy face。❌ byte/time GPU upload budget、Raster Greedy Mesh、远距 unload 與長時間大型世界壓力驗證尚未完成。 |
| Renderer 架構與切換 | ✅ **【已完成－本輪】** | Compute Path Tracing、PBR Forward、Classic、Deferred、Hardware DXR 保持可用；Renderer 選單依序加入正式的 Hybrid PBR Raster，位於 Deferred 與 Compute PT 之間並通過 GPU smoke。 |
| Renderer Capability／圖形設定 | 🟡 **【部分完成－本輪有進度】** | ✅ Deferred 已有 Renderer／G-Buffer／Lighting／Shadow／Reflection／AO／Water／Transparency／Fog／Post 品質，以及 HDR、Bloom、Tone Mapping、Exposure、10 種 Debug View。✅ Hardware RT 的打蠟開關已整理成 Render Style：Default／Wax／Reference／Debug。❌ Deferred Cascade Shadow／SSGI／OIT／Clustered 與 DXR 折射／GI／降噪仍是後續。 |
| 水 | ✅ **【已完成－本輪更新】** | 游泳／上岸輸入規則維持已完成狀態。本輪修正所有 Raster 的方塊水接縫：只畫最高層 top surface、剔除 fluid 內部面、使用連續 world-space 水色／波紋；DXR 靜態水也只保留最高表面。水下 framebuffer average 181.10、range 13–238。 |
| 玩家碰撞與移動 | ✅ **【已完成－本輪】** | 固定碰撞體、連續 Grid 推出與固定物理步保持不變；本輪將水中岸邊抬升改為明確受跳躍輸入控制，避免前進鍵把高牆當成自動階梯。 |
| Physics Grid | ✅ **【已完成－本輪渲染效能修正】** | 獨立剛體、平移／旋轉、BEPU compound collision、質量／重心、固定時間步、睡眠、raycast／選取及 Grid 放置保持可用。Raster／DXR 現在只建立 Grid 外露面；使用者存檔診斷 Grid triangles 由 465,408 降到 31,176，strict 場景 96 triangles／145,898 hits。DXR 動態 refit 不再誤清放置方塊造成的 static AS 更新。撞擊二次斷裂仍列後續。 |
| 細分／Micro Build | 🟡 **【部分完成】** | 普通整格破壞、精密編輯、raycast、碰撞、渲染、Undo 與保存已完成。❌ Micro-volume 位於旋轉動態 Grid 的完整局部座標編輯／保存尚未完成。 |
| 存檔與世界管理 | ✅ **【已完成－本輪 Region V4】** | 多世界管理與原子 generation／backup recovery 保持可用；每個 Region 現合併 32×32 Chunk，支援負座標與 Y -64..511，並保留舊 v3 目錄與舊單檔遷移。 |
| Mod 與 ModAPI | ✅ **【已完成－本輪診斷補強】** | 原有獨立 `RTXSandbox.ModAPI` 與 BepInEx loader 已可用；未載入 DLL 現在會檢查 target framework 與 BepInPlugin／BasePlugin 入口，終端和模組頁會顯示明確原因。現有 `SoftBlockPhysicsMod.dll` 已確認為 .NET 9 且不含插件入口，因此不是可載入模組；Example Mod 仍可正常載入。 |
| UI 與操作入口 | 🟡 **【部分完成－本輪修正】** | ✅ 獨立 8 鍵暫停選單、ESC 存檔關閉、背景自動暫停、Minecraft 風格背包與世界管理入口均已接線。✅ 模組從設定移到暫停選單的獨立畫面；設定／模組使用「完成」返回原選單。✅ 全域關閉鍵與 ImGui keyboard capture 已拆開：設定可用 ESC，物品欄可用 ESC／E／I／Tab；只有文字欄輸入與實際 IME composition 會保留輸入鍵。🟡 真正 Windows 中文 IME 的人工組字選字仍需使用者確認。 |
| Shader 與錯誤回退 | 🟡 **【部分完成】** | Shader 編譯錯誤、Renderer 建立失敗保留舊 Renderer、缺少能力不顯示假後端及 GPU 資源釋放已有基礎。❌ 運行中 Shader Reload／保留上一版成功 program 的完整操作入口尚未完成。 |
| Debug、測試與效能資料 | 🟡 **【部分完成－本輪更新】** | 79 項自動測試全部通過；新增 generation cancellation、occupied bounds、urgent collision 與大型 Grid bounds。RTX 5050 上 Classic、Forward、Deferred、Hybrid、Compute PT、Hardware DXR 與水下 framebuffer smoke 全通過。❌ 尚缺長時間無限探索壓力測試與完整 GPU timing／benchmark。 |
| 專案、依賴與輸出 | ✅ **【已完成／TFM 已更新】** | `RTXSandbox.exe` 位於 `bin/<Configuration>/<HostTFM>/` 輸出根目錄；預設為 `net9.0`，可選為 `net10.0`。遊戲與相依 DLL、原生 runtime 集中於 `Managed/`，根目錄沒有 DLL。VS／MSBuild 的 `RunCommand`、偵錯工作目錄與 Example Mod 插件複製位置都跟隨目前 Host TFM。 |

### 本輪驗證基準

- Debug：使用者目前執行中的 `RTXSandbox (PID 1460)` 鎖住標準 Debug DLL／plugin deployment，因此沒有關閉使用者遊戲；主程式與 ModAPI 改用獨立輸出完整 Build，Example Mod 以獨立輸出 `Compile` target 驗證，三者 0 警告／0 編譯錯誤。標準 solution post-build copy 仍須關閉該執行中遊戲後才能覆寫 plugin。
- `dotnet build RTXSandbox.sln -c Release --no-restore`：成功，3 個專案，0 警告／0 錯誤。
- `--self-test`：79/79 通過；包含 queued Chunk 非同步提交／取消、occupied bounds、urgent collision rebuild 與大型 Grid bounds。
- Direct3D 12 DXR V1、Compute、Deferred、Hybrid PBR Raster、PBR Forward、Classic GPU smoke：最終版全部通過且都等待 49 個測試 Chunk 串流完成。Classic／Forward 為 43 visible／6 culled。
- Deferred／Hybrid strict GPU smoke：feature mask 1023、16 point lights、1 spot light、35 shadow draws、shadow depth 0.76569–1.0、49 visible Chunk 與非黑 framebuffer 通過。
- DXR strict GPU smoke：Tier 1.1、`1,772` triangles、96 Grid／36 micro triangles、147,826 Grid-hit pixels、2 static builds＋3 dynamic refits＋2 TLAS updates，static／refit `5.49／5.56 ms`；shadow／reflection／transparent／textured hits 與 framebuffer average 217.83、range 13–220 全通過，拒絕 fallback。
- Natural streaming smoke（兩個完整 7×7 window）：Classic 最大／平均 `13.86／8.61 ms`，Deferred `17.03／8.56 ms`，Hardware DXR `115.14／11.04 ms`；DXR `60,532` triangles、static／refit `49.42／0.79 ms`，實際 Trace Rays 與 framebuffer 通過。
- 暫停選單（8 controls）、設定、獨立模組畫面、背包、主選單、世界建立與水下 framebuffer smoke：全部通過。Shortcut smoke 額外讓物品欄／設定先取得 ImGui keyboard focus，再驗證 E／ESC 的共用實際關閉分支成功。
- `--gpu-smoke-ime`：提交中／日／韓／泰／西里爾文字串、觸發 Win32 IME 候選位置 callback，並驗證文字欄位取得鍵盤所有權；此項不再記成真正 OS 中文組字的替代證據。
- `--mod-diagnostics`：BepInEx 實際載入 Example Mod，並準確回報 `SoftBlockPhysicsMod.dll` 目標 .NET 9、高於遊戲 .NET 6，且缺少 BepInPlugin／BasePlugin 插件入口。

## Renderer Pipeline 設定矩陣（逐項比對基準）

這一節定義每條 Pipeline 自己應擁有的完整設定頁；即使名稱重複也不視為跨 Pipeline 共用畫面。`✅` 代表目前確實接入，`🟡` 代表底層已有部分能力但設定尚未齊全，`❌` 代表尚未實作，禁止做成看似有效的假選項。

### Classic（Compatibility）

- ✅ Display：Resolution Scale、VSync、FPS Limit；Camera：FOV；Lighting：分級光照、Gamma／曝光。
- 🟡 Display：Resolution、Fullscreen、Borderless；Camera：Near／Far Clip、View Distance。
- ❌ Shadow：開關、解析度、距離；Rendering：Backface Culling、Depth Test、Wireframe、Face Culling；Post Process：Tone Mapping、Bloom、Fog；Debug：GPU／CPU time、Draw Calls、Chunk Border。

### Forward PBR

- ✅ Display：Resolution Scale、VSync、FPS Limit；Camera：FOV；Lighting：曝光；PBR：Albedo、Metallic、Roughness、Emission、Specular、Transmission、IOR；Water：Reflection、Refraction、Transparency。
- 🟡 Lighting：Sun、Ambient、Gamma；PBR：Normal／AO 由程序材質提供，但尚無獨立品質控制；Reflection／Specular 已有 0–3 品質設定。
- ❌ Resolution／Fullscreen、Near／Far Clip、LOD、Auto Exposure、HDR、Clear Coat、陰影／Cascade、Water Fog／Depth Color、Distance／Height Fog、Bloom／Tone Mapping／FXAA／SMAA／TAA／Sharpen／Vignette，以及完整 Debug views／timing。

### Deferred

- ✅ **【已完成－本輪】** Geometry：不透明 Depth Pre-Pass；五目標 G-Buffer 寫入 Position／Depth、Albedo＋Roughness、Normal＋Metallic、Emission（Alpha 保留材質 AO 通道）、Motion Vector；SSAO 於 Lighting Pass 由 Position／Normal 計算，動態 Physics Grid 與 Chunk Mesh 共用相同流程。
- ✅ **【已完成－本輪】** 共用材質：Raster 現在正式上傳並取樣與 Compute Path Tracing、DXR 相同的 `ProceduralMaterialAtlas.DetailPixels`，以逐像素 roughness 與 height gradient 產生法線細節；三條管線沒有各自維護 Normal Map 資料。
- ✅ **【已完成－本輪】** Lighting：GGX／Smith／Schlick PBR、Directional Sun、最多 16 個由世界發光方塊產生的 Point Light、攝影機 Spot Light、Ambient／Sky／Environment IBL；CPU 只保留能量最高的 16 燈並由半徑衰減剔除。
- ✅ **【已完成－本輪陰影與效能修正】** Shadow／Ambient／Reflection：修正建構後首次 G-Buffer Resize 誤刪 shadow FBO／texture 的資源生命週期錯誤；Directional Sun 使用 2048² light-space depth、opaque-only caster、法線 bias 與品質 1–3 PCF。靜態 Chunk 與動態 Grid 分成兩張 depth layer，shader 取兩者較近深度；Grid 移動不再重畫完整靜態世界。Point／Spot Shadow 目前仍是螢幕空間 contact shadow；SSAO、SSR 與天空環境反射維持原實作。
- ✅ **【已完成－本輪更新】** Water／Transparency：Deferred／Hybrid 不透明結果完成後，以 Forward Alpha pass 處理水與玻璃；會取樣 HDR Scene Color 做螢幕空間折射、天空反射、Fresnel、透明度與距離水霧。Classic／Forward 也會在透明 pass 前複製實際 opaque framebuffer，不再從空的 `uSceneColor` 取樣；玻璃 alpha 改由 transmission＋roughness 控制，遠距背景不再被固定灰色蓋住。
- ✅ **【已完成－本輪】** Atmosphere／Post：RGBA16F HDR lighting target、Exposure、Reinhard／Filmic／ACES Tone Mapping、亮部鄰域 Bloom、Height＋Distance Fog、簡單 Color Grade 與 Sharpen。
- ✅ **【已完成－本輪】** Debug／設定：Final、Albedo、Normal、Depth、Metallic、Roughness、AO、Emission、Lighting、Motion Vector；Renderer／G-Buffer／Lighting／Shadow／Reflection／AO／Water／Transparency／Fog／Post 品質與 HDR／Bloom／Tone Mapping 均已接到底層。
- ✅ **【實機證據－本輪】** RTX 5050 Laptop GPU：Debug Deferred／Hybrid depth range 0.452230–1.0、feature mask 1023、Point／Spot Light 與非黑 framebuffer 通過；Release Deferred／Hybrid depth range 0.432853–1.0、17 caster draws、1–7 Point Lights、1 Spot Light 與非黑輸出通過。Smoke 直接讀回 shadow texture，不能再只靠 draw counter 假定陰影存在。
- 🟡 **【部分完成】** Directional Shadow V1 已是真正 shadow map，但仍是覆蓋 64×64 世界的單張正交投影，尚非 Cascade Shadow Map；Point／Spot 尚未建立 cubemap／spot shadow map。Environment Reflection 是天空 IBL，尚未建立可烘焙 Reflection Probe；Bloom 是單 pass 鄰域濾波。
- ❌ **【尚未完成】** SSGI、Cascade Shadow Map、多光源 shadow atlas、可放置 Reflection Probe、Alpha Cutout 材質資料、OIT、Volumetric Fog／Cloud、Tiled／Clustered Deferred、TAA／Motion Blur／DOF 與 GPU profiler。

### Compute Path Tracing

- ✅ Display：Resolution Scale、FPS Limit；Sampling：每幀 samples、累計上限；Ray：Max Bounce；Lighting：Sky、Environment、Direct shadow ray；Materials：Albedo、Metallic、Roughness、Emission、Glass、Transmission、IOR；Water：Reflection、Refraction、Absorption、Scattering、Underwater；Debug：累計 SPP。
- 🟡 Accumulation 已可用，但 Adaptive Sampling／獨立 Reflection、Refraction、Shadow Bounce／MIS／NEE／Blue Noise 尚未拆成設定。
- ❌ OIDN／NRD／Temporal／Spatial denoiser、Tile／Workgroup／Frame Budget，以及 Ray Count／Bounce Heatmap／GPU Time／Noise debug views。

### Hardware Ray Tracing（Direct3D 12 DXR V1）

- ✅ **【已完成－本輪】** Windows／高效能 DXGI adapter 選擇、D3D12 Device、DXR Tier 檢測、Command Queue／Allocator／GraphicsCommandList4／Fence 同步。
- ✅ **【已完成－本輪】** 由目前世界不透明可見方塊建立 triangle vertex buffer、BLAS、單 instance TLAS；VoxelChanged／WorldReloaded 後於下一幀安全重建。
- ✅ **【已完成－本輪】** DXC `lib_6_3`、Ray Generation／兩個 Miss／Any Hit／Closest Hit、DXR State Object、Global Root Signature、64-byte 對齊 Shader Table 與 `DispatchRays`。
- ✅ **【已完成－本輪】** Closest Hit 會依品質實際發出二次 DXR `TraceRay`：方向光硬陰影射線與一層材質反射射線；粗糙度／金屬度已隨世界頂點上傳，不再只是 GL 也能仿造的固定方向光著色。
- ✅ **【已完成－本輪】** Hardware RT 0–3 品質已接到真正射線預算；0 可只跑主射線降低負載，1 為主射線＋陰影，2 為陰影＋選擇性反射，3 為陰影＋廣泛反射。未實作的 denoiser 仍禁用且明確標示，不做假選項。
- ✅ **【已完成－本輪】** Hardware RT 的打蠟選項已整理為獨立 Render Style：Default、Wax Mode、Reference、Debug；Wax 仍倍增全部不透明材質反射率並限制非金屬混色，Reference 讓介電材質依 Fresnel 參與反射，Debug 顯示法線／粗糙度／金屬度。
- ✅ **【已完成－本輪】** DXR 現在把共用 `ProceduralMaterialAtlas` 的 Color RGBA8 與 Detail RG8 正式上傳為 D3D12 SRV；Closest Hit 依 material ID／top-side-bottom／UV 取樣與 Path Tracing、Raster 相同的 16×16 顏色、逐像素 roughness 與高度法線細節，不再只用純色 Albedo。
- ✅ **【已完成－本輪效能修正】** Physics Grid 與靜態世界已拆成各自的 BLAS／TLAS instance。相同頂點拓撲的 Position／Orientation 更新會覆寫動態 vertex buffer，使用 `ALLOW_UPDATE`＋`PERFORM_UPDATE` refit 動態 BLAS，再更新 TLAS；Grid 完整相鄰 cell 的內部面不再進 vertex buffer／BLAS，且 Grid 在 Hardware RT 仍可見。
- ✅ **【已完成－本輪更新】** 水與其他 transmission geometry 已正式進入 BLAS；DXR 折射現在可連續穿過玻璃入口與出口，並依 entering／exiting 使用正確 IOR，不再讓第二個表面落入不透明灰色著色。透明 Fresnel 次要分支只保留前三層以避免鏡廳中的指數射線樹，折射與不透明鏡面設定的 29 層遞迴不受影響；到達遞迴預算時透明終端會放行至後方真實表面。
- ✅ **【已完成－本輪】** 發光方塊不再只輸出 emission：CPU 從靜態世界與 Grid 選出最多 32 個 emissive lights，上傳 root SRV；Closest Hit 以距離、半徑與 N·L 對鄰近材質加入直接光照。
- ✅ **【已完成－本輪修正】** DXR 品質 1–3 以 235 格、方向無關 ray neighborhood 保留 shadow／reflection／refraction 可達物件；品質 0 仍使用 camera culling。單純轉視角不再重建靜態 AS；串流／放置更新會 debounce，但最慢 1.5 秒仍正式更新。動態 refit 不再誤清 static dirty flag。
- ✅ **【已完成－本輪陰影修正】** DXR shadow ray 以未受材質高度擾動的幾何法線判斷受光面與偏移起點，Any-Hit 忽略極近自交並讓水／玻璃通過；剔除與 Trace 現在共用同一個日／夜主要光源方向，夜間不再一邊保留月光 caster、一邊把射線射向地平線下的太陽。
- ✅ **【已完成－本輪】** DXR UAV 離屏結果經 D3D12 readback 上傳至既有 OpenGL 全螢幕顯示；天空、共用材質紋理、高度細節、RTX 遮蔽陰影與受材質限制的反射已可辨識。
- ✅ **【實機證據－本輪 Physics 效能】** NVIDIA GeForce RTX 5050 Laptop GPU，DXR Tier 1.1 final strict smoke：116,498 triangles、96 Grid／36 micro triangles、147,826 Grid-hit pixels、2 static builds＋3 dynamic refits＋2 TLAS updates，最後 static/refit 55.83/4.77 ms；shadow 273,753、reflection 279,323、transparent 81,243、textured 360,566 hits，framebuffer average 217.83／range 13–220，拒絕 fallback。使用者大型 Grid 診斷的動態三角形另由 465,408 降至 31,176。
- 🟡 **【部分完成】** Hardware RT 可由 Renderer 選單選擇；初始化失敗會顯示原因並安全回退。靜態世界與動態 Grid 已分離且同拓撲可 refit，但 V1 仍是同步 D3D12 readback→OpenGL upload；尚未做到每 Chunk／每 Grid BLAS cache 與純 transform TLAS-only update。
- ✅ **【已完成－本輪】** 靜態 micro voxel 現在正式進入 static BLAS；cell edit／bulk reload 會使 DXR 幾何失效，strict smoke 實測 36 micro triangles。
- 🟡 **【部分完成－本輪有進度】** Hardware DXR 的方向光與發光方塊 visibility ray 現在由 Any-Hit 對每個玻璃界面累積具顏色與能量衰減的 transmission；白光穿彩色玻璃會保留玻璃色，彩色光也會與玻璃濾色相乘。Raster shadow map 與 Compute PT 的直接光陰影尚未實作彩色 transmission，因此這項不標成跨 Renderer 完成。軟陰影、粗糙多採樣反射、完整體積水散射、GI／AO、denoiser、temporal、DLSS／FSR／XeSS、共享 D3D12/OpenGL texture、每 Grid BLAS cache 與分 Chunk AS仍留後續。
- ✅ **【實機證據－2026-08-06 玻璃修正】** RTX 5050 Laptop GPU：79/79 self-tests；Classic、Forward PBR、Deferred、Hybrid、Compute PT GPU smoke 全部通過。Hardware DXR 一般 smoke 為 145,506 transparent hits；29 層 real DXR＋971 層近似尾端壓力 smoke 為 145,732 transparent hits、141,121 chromatic glass pixels、DispatchRays GPU 22.06 ms。修正前的無限制玻璃 Fresnel 分叉可重現 `DXGI_ERROR_DEVICE_REMOVED`，限制透明次要分支後同一測試完成 97 frames 且未 fallback。

### Hybrid PBR Raster（高畫質光柵）

- ✅ **【已完成－本輪】** 正式 pipeline 名稱為 Hybrid PBR Raster／混合式 PBR 光柵，Renderer 選單順序位於 Deferred 與 Compute Path Tracing 之間。
- ✅ **【已完成－本輪】** 採用 deferred PBR opaque geometry／lighting／HDR post 加 forward water／glass；至少品質 2，包含 multi-light、shadow、SSAO、SSR、HDR、Bloom、tone mapping、水反射／折射／透明。
- ✅ **【實機證據－本輪】** RTX 5050 Debug／Release strict GPU smoke：feature mask 1023、1–7 Point Lights、1 Spot Light、17–18 caster draws；直接讀回的 shadow depth range 為 0.432853–1.0 至 0.452230–1.0，framebuffer 非黑且具變化。

### Voxel GI（未建立 Pipeline）

- ❌ Voxel Resolution／Size／LOD、Voxel GI／AO／Shadow、Update Budget／Cache／Chunk Upload、Visualization／Brick／Clipmap／Heatmap。

## 0.13 — Engine Architecture（目前版本）

- [x] `IRenderBackend` 能力模型與資料驅動 catalog
- [x] 安全切換與設定持久化的 `RenderBackendHost`
- [x] Compute Path Tracing、Deferred G-buffer、Hybrid PBR Raster、PBR Forward、Classic 五個非 DXR 可用後端
- [x] ♻️ Deferred 鎖定的舊記錄已失效；目前已有真正 Geometry／Lighting pass 並通過 GPU smoke
- [x] Hardware RT V1：Windows Direct3D 12 DXR Tier 1.0+，包含 BLAS／TLAS、RayGen／兩個 Miss／AnyHit／ClosestHit、Shader Table、主射線、二次陰影／反射、共用材質 atlas、靜態世界與 Physics Grid 幾何及 OpenGL 顯示
- [x] Mod API 拆出 Blocks、World、Rendering、Physics
- [x] `IRenderBackendService`、`IVoxelPhysicsService` 穩定服務
- [x] Graphics 設定區開始移出主遊戲檔案
- [x] 版本化 `settings.json`、原子寫入與 `.bak` 損壞恢復
- [x] 16×16 共用 Chunk 座標、邊界失效與 Raster 每 Chunk GPU mesh
- [x] Physics Grid 獨立動態 mesh；睡眠剛體不再使靜態世界每幀重建；移動 Grid 擠壓玩家時連續向水平安全方向推出
- [x] Micro-voxel 帶座標變更事件，精確失效 render／physics Chunk
- [x] Regular／micro voxel 的 Chunk-owned 稀疏資料儲存與線性舊存檔快照邊界
- [x] 每 Chunk 稀疏存檔、整代原子提交／備份恢復與舊單區域存檔遷移
- [x] 主選單多世界瀏覽／切換、同名保護、同 Seed 覆蓋重建、display rename、二次確認刪除，以及預設簡潔、可展開模板的建立流程
- [x] 移除失效的 Physics Mode／生存設定；結構 Grid 保持為世界規則
- [ ] 背景 save／mesh job 與 streaming
- [ ] Generator pass／biome／climate／river 分層
- [x] **【部分完成】** 16 單位有限水量、守恆流動、方塊排水、小型連通管平衡、分級水面與 Region v2；流速／黏度／大型海洋壓力仍待後續
- [x] 外部 Example Mod 獨立專案、BepInEx DLL 部署及 ModAPI 註冊驗證
- [ ] 將目前位於主程式的 `BepInExBootstrap` 拆成獨立 loader adapter 專案

## 0.12 — Precision Building（已完成）

- [x] Sparse micro-volume；支援 1/2、1/4、1/6、1/8、1/16、1/32、1/64 與精確混合網格
- [x] 微方塊放置、拆除、材質取樣、獨立 Undo/Redo、UI 與熱鍵
- [x] 精確 ray hit、玩家碰撞與結構支撐
- [x] GPU 二層 DDA + AABB path tracing
- [x] Save format 6 微幾何序列化與 namespaced material remap
- [ ] 任意角度旋轉、斜面與 transform gizmo
- [ ] 動態剛體化微結構；目前微結構作為靜態支撐幾何
- [x] 無邊界 X／Z 與玩家周圍惰性 Chunk 生成；遠距 unload／背景 job 仍列後續

## 0.8 — Creative Editor Core（已完成）

- [x] 移除本體生存循環與生存 HUD；保留舊 API／存檔相容欄位。
- [x] 完整方塊庫，不限數量放置、擦除、替換與材質吸取。
- [x] A/B 區域選取、填充、清除、條件替換。
- [x] 複製、貼上、Y 旋轉、X 鏡像。
- [x] 球形增加／擦除、平整與可撤銷爆破。
- [x] 64 筆命令式 Undo／Redo。
- [x] 第一人稱、碰撞飛行、noclip。
- [x] 六面連通支撐、局部 Grid 剛體、連續重力／旋轉／碰撞／休眠（取代逐格落下）。
- [x] 網格歸屬轉移工具；限制為座標對齊且六面相鄰。
- [x] 正式切換為 BEPUphysics 2：Chunk 靜態碰撞體、Grid 動態 compound、CCD、接觸流形、摩擦與休眠。
- [x] 120 Hz 固定時間步、兩步追幀上限與世界重載／釋放生命週期；避免長幀後連跑六次 simulation 形成卡頓螺旋。
- [ ] 以 BEPU 接觸事件實作撞擊二次斷裂；舊的未接線實驗選項已移除。
- [x] BepInEx 自訂 Build Tool API 與強制覆蓋。
- [x] 時間循環與 GUI 調時、1–12 bounce 渲染設定。
- [x] Flat／Natural／Pure Nature／Void／Showcase 世界建立介面與版本化生成 metadata。
- [x] 草原、森林、竹林、沙漠、雪地、山地、沼澤、河帶、植物與洞穴原型。

## 0.9 — World Presets（已完成）

- [x] Flat、Natural、Pure Nature、Void、Showcase。
- [x] 七種可鎖定生態與 Auto 氣候分配。
- [x] Flat 分層材質、湖泊、植物與高度選項。
- [x] Pure Nature 宏觀過渡、河帶與洞穴。
- [x] 建立前備份、重新出生、Mod WorldReady 重發與第 4 版存檔 metadata。

## 0.10 — Material Library（完成）

- [x] 196 個資料驅動定義／194 種可建造材質。
- [x] Nature、Wood、Building、Metal、Technology、Optical、Lighting、Special 分類。
- [x] Palette 分類過濾、名稱／key／tag 搜尋與 PBR tooltip。
- [x] Transmission／IOR 與 Tiles、Panels、Circuit、Grid、Rainbow、Hologram、Cloud、Lava、Checker、Fabric 程序表面。
- [x] 4,096 槽 16-bit Registry、R16UI GPU voxel texture、第 5 版存檔與舊 byte 世界遷移。

## 0.11 — Procedural Materials & Creative Library（目前版本）

- [x] 所有內建與 Mod 材質共用真正的 16×16 程序圖集，包含 top/side/bottom、逐像素 roughness 與高度法線細節。
- [x] 自然材質以 namespaced key、材質種子與方塊座標做確定性變化；純色材質可保持零雜訊。
- [x] Creative Library 使用等角 3D 方塊預覽、搜尋、分類、收藏與最近使用。
- [x] **【已完成－2026-08-06】**「全部」頁使用穩定的簡單分類排序；普通、彩色及特殊表面玻璃會連續排列，彩色材質採固定色序；新增的磨砂、霧面、馬賽克、壓克力、壓紋與蝕刻玻璃均顯示中文名稱。79/79 自我測試通過。
- [x] **【已完成－2026-08-06 玻璃表面層級】** 馬賽克使用 4×4 粗格、磨砂使用 2×2 中顆粒、霧面使用逐 texel 細顆粒；自我測試會比較磨砂與霧面的相鄰重複頻率，防止兩者再次退化成相同外觀。
- [x] 九格快捷欄可配置並以 namespaced key 持久化；數字鍵、滾輪與中鍵吸取共用同一套選取邏輯。
- [x] 有黑色外框的情境準星，可辨識放置、移除、選取與碰撞阻擋，並可在設定調整。
- [x] Natural v2：domain warp、多噪聲大陸度／侵蝕／山脊／氣候／河道、地質分層、3D 洞穴與群落式植被。

## 0.12 — Chunk World & Presentation

- [x] **【已完成－2026-08-06】** 建置會清除舊版輸出規則留下的 `Content/Content` 與 `ContentPacks/ContentPacks/ExamplePack`，正式內容只保留單一 `Content/Base` 與 `ContentPacks/ExamplePack`；不會刪除其他使用者 Content Pack。
- [x] Raster Chunk mesh ownership、面鄰接失效與動態 Grid mesh 分離。
- [x] Regular／micro voxel 由 Chunk 擁有，空 Chunk 自動釋放並相容既有存檔。
- [x] 每 Chunk 稀疏存檔、空世界零 payload、整代損壞恢復與第 1–7 版單區域遷移。
- [ ] 串流、背景生成／保存／meshing、texture 分頁與大型世界座標。
- [x] 多世界瀏覽器：命名、seed、列表、切換、模板建立、同 Seed 重建、重新命名與刪除。
- [ ] 世界複製、匯出，以及刪除前自動備份／資源回收筒。
- [ ] 選取範圍線框、筆刷 ghost preview、transform gizmo。
- [ ] 方塊收藏、最近使用與可重排快捷列（搜尋與分類已完成）。
- [ ] 將現有綜合 Showcase 拆成水世界、金屬城市、玻璃森林、黑暗洞穴四個獨立大型模板。
- [ ] 雨、雪、霧、濕表面、雷電與環境 preset。
- [ ] 快速截圖、無 UI 截圖與高樣本離線輸出。

## 0.13 — Precision & Physics

- [x] ♻️ 舊問題已不存在：Chunk 內 sparse micro-volume 已支援 1/2、1/4、1/6、1/8、1/16、1/32、1/64。
- [x] ♻️ 舊問題已不存在：Micro-voxel 選取、碰撞、複製貼上、保存和光追交集已有正式流程與測試。
- [x] 結構六面連通元件、依材質密度／體積計算質量與重心、支撐移除斷裂及 BEPU 休眠。
- [ ] 撞擊接觸造成二次斷裂；舊的未接線開關已移除，不冒充完成。
- [ ] 大型 Grid compound shape 的分區重建與每幀形狀烹調預算；保留未來 PhysX 後端介面。
- [ ] 爆炸衝擊、碎片壽命、粒子和音效。

## 0.14 — Cinematic & Renderer Backends

- [ ] 固定鏡頭、關鍵幀、樣條路徑、焦距與景深。
- [ ] 時間／天氣軌道與鏡頭序列存檔。
- [ ] 時域／空間去噪、可調 accumulation lock。
- [x] DXR V1 後端與一層 RTX 陰影／反射／折射、水下效果、emissive direct lights 與可見 Chunk AS；Compute 與 Raster 保持獨立降級後端。進階 GI／降噪與互通效能仍待後續。
- [ ] Mod shader／renderer extension 的能力與安全邊界。

## 0.15 — Manual Mod Workspace（本輪）

- [x] **【已完成－本輪】** 模組頁新增檔案工作區；任意副檔名只要實際內容是 UTF 文字（上限 4 MiB），右鍵即可開啟「編輯、編譯、移動、刪除」選單，不監看也不自動編譯。
- [x] **【已完成－本輪】** 手動編譯固定輸出 `net6.0` DLL，引用 .NET 6 SDK 系統參考、輸出 `Managed` 內相容組件，以及整個 `BepInEx/plugins` 內相容前置 DLL；native DLL 與高於 .NET 6 的組件不冒充可用參考。
- [x] **【已完成－本輪】** 直接放在 plugins 根目錄的散裝來源只在玩家按「編譯」後建立同名資料夾，來源與同名 DLL／PDB／deps 一起移入；已在子資料夾內的來源保持原位置，編譯不刪除或改寫來源。
- [x] **【已完成－本輪】** 內建純文字編輯器支援輸入、明確保存、撤銷未保存內容與回滾上次保存；保存前副本位於 `BepInEx/editor-backups`。移動拒絕越出 plugins 或覆寫同名檔，刪除需二次確認並移至可復原的 `BepInEx/trash`。
- [x] **【驗證證據－本輪】** Release solution 與隔離 Debug 編譯均零警告；目前完整套件為 79/79 self-tests 通過。模組編譯／回滾、物理、保存遷移、Infinite World／Region V4、generation cancellation、occupied bounds 與 urgent collision 都包含在同一套回歸測試中。
- [ ] **【尚未完成】** 執行中 DLL hot reload、多來源檔／自訂 `.csproj` 專案建置、NuGet 套件管理、相依版本求解、安全沙箱與可信簽章；目前新 DLL 仍需重新啟動遊戲，編譯端需要玩家電腦已安裝可建置 net6.0 的 .NET SDK。

## RTX Sandbox Optimization Roadmap（不降低畫質）

本節只收錄不降低解析度、畫質、Samples 或關閉效果的優化。狀態完全依目前 Repository 判定：`✅` 已有實際執行路徑、`🟡` 只有部分 Pipeline／基礎能力、`❌` 尚未實作。這是程式碼現況，不把「理論上由 Driver 處理」當成已完成，也不以建置成功冒充效能提升。

### 驗收護欄

- 同一場景、相同解析度／設定／Samples／效果，比較 CPU frame、GPU frame、Draw Call、triangle、mesh build、upload、BLAS／TLAS build／update 時間與記憶體峰值。
- 優化前後必須做固定攝影機 framebuffer 比對；允許浮點誤差，不允許可見方塊、陰影、反射、透明水或發光影響消失。
- Frustum／Occlusion／RTX instance culling 必須使用保守 bounds。對 DXR 而言，「主攝影機看不到」不代表可移除：仍可能被 shadow、reflection 或 refraction ray 命中。
- 背景工作只能建立 CPU 資料；OpenGL／D3D12 資源提交必須遵守各 API 的 thread／queue ownership，並設每幀 upload budget，避免把平均 FPS 換成卡頓尖峰。

### 一、CPU 端（1–10）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 1 | View Frustum Culling | 🟡 | `RasterVoxelRenderer` 對 Chunk／Physics Grid 使用 1.6× 保守視錐；DXR 品質 0 使用可見 mask，品質 1–3 為陰影／反射正確性保留全域 ray geometry；Compute PT 不使用 Chunk mesh。 |
| 2 | Occlusion Culling | ❌ | 沒有 Hi-Z、occlusion query 或 software occluder。必須先有可重現 benchmark，避免目前僅 16 Chunk 時查詢成本高於收益。 |
| 3 | Chunk Culling | 🟡 | Raster 已整個 Chunk 不送 draw；DXR 只有品質 0 在重建 monolithic BLAS 前略過 Chunk，尚非獨立 BLAS／instance culling。 |
| 4 | Backface Culling | 🟡 | Mesher 已不產生被相鄰不透明方塊遮住的內部面，但 Raster 明確 `GL.Disable(CullFace)`，尚未啟用真正 GPU backface culling。 |
| 5 | Greedy Mesh | ✅ | Raster 不透明 full blocks、連續同材質水頂面、DXR 靜態 full blocks 與 BEPU full-cell collider 都已使用對應的精確 greedy merge；Micro／透明幾何保留精確路徑。 |
| 6 | Mesh Cache | ✅ | Raster 以 `_chunkMeshes` 長期持有每 Chunk VBO，不會每幀重建所有 Chunk。 |
| 7 | Dirty Chunk Update | 🟡 | Raster／BEPU／Compute 只排入變更 Chunk 並合併重複 edit；Raster Grid 以 `TopologyVersion` 區分拓撲與 transform。DXR CPU geometry 有 per-Chunk cache，但任何靜態 topology commit 最終仍重建 monolithic static BLAS。 |
| 8 | Multi-thread Chunk Build | ✅ | Natural generation、Raster CPU mesh、BEPU collider shape 與 DXR static CPU geometry 均由版本化背景工作建立；render／simulation thread 只提交仍有效結果。 |
| 9 | Async Upload | 🟡 | CPU mesh build 與提交已拆開，Raster／Compute 每幀最多上傳 1 Chunk；但實際 OpenGL `BufferData`／`TexSubImage3D` 仍同步，尚無 persistent staging ring／fence。 |
| 10 | Physics Sleeping | ✅ | BEPU body 的 `Awake` 狀態會同步為 `PhysicsGrid.IsSleeping`，睡眠 Grid 不再持續發布 render invalidation。 |

### 二、GPU Raster（11–20）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 11 | Early-Z | 🟡 | 所有 Raster 使用 depth test；Deferred／Hybrid 有明確 opaque depth pre-pass。Classic／Forward 尚未提供可量測的 early-Z 專用流程。 |
| 12 | Depth Pre-pass | 🟡 | Deferred／Hybrid 已實作；Classic／Forward 尚無，加入前需以 overdraw 場景證明收益大於額外 Draw Call。 |
| 13 | GPU Instancing | ❌ | 目前每 Chunk／Grid 使用獨立 `DrawArrays`，沒有 instance buffer。 |
| 14 | Indirect Draw | ❌ | 沒有 `MultiDrawIndirect`／GPU-generated draw arguments。 |
| 15 | Bindless Texture | ❌ | 使用固定 texture units 與單一 atlas，沒有 bindless handle。 |
| 16 | Persistent Mapping | ❌ | VBO／SSBO 更新使用 `BufferData`，沒有 persistent mapped ring buffer。 |
| 17 | Texture Atlas | ✅ | 所有內建與 Mod 材質使用共用 `ProceduralMaterialAtlas` color／detail atlas。 |
| 18 | Material Batching | ✅ | material ID 隨 vertex 傳入，同一 Chunk 可在同一 draw 取樣 atlas，不需要逐材質切 shader／texture。 |
| 19 | State Sorting | ❌ | 沒有一般化的 pipeline/material state sort key。 |
| 20 | Render Queue | 🟡 | Deferred／Hybrid 已固定 opaque depth→GBuffer→lighting→forward transparency；尚無可擴充、可排序的通用 Render Queue。 |

### 三、Deferred／Hybrid（21–26）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 21 | Light Volume | ❌ | Lighting 仍為全螢幕 pass，沒有 sphere／cone light volume。 |
| 22 | Tiled Deferred | ❌ | 尚無 screen tile light list。 |
| 23 | Clustered Deferred | ❌ | 尚無 3D cluster／depth slice light list。 |
| 24 | Light Culling | 🟡 | CPU 只保留能量最高的 16 個 emissive point lights，shader 也做 radius rejection；但每個 pixel 仍循環全部入選燈，尚非 tiled/clustered culling。 |
| 25 | Shadow Cache | ✅ | 修正 Resize 誤刪 shadow texture/FBO；完整靜態世界與動態 Grid 使用兩張 2048² depth layer。世界／材質只失效 static layer，Grid transform 只失效 dynamic layer；光向變化以小於四分之一 shadow texel 的門檻合併。 |
| 26 | Reflection Probe Cache | ❌ | 目前是天空環境反射／SSR，尚未建立 Reflection Probe。 |

### 四、Hardware RTX／DXR（27–39）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 27 | TLAS 只更新 | 🟡 | Grid transform 現在保留 static BLAS、refit dynamic BLAS 並重建小型雙 instance TLAS；尚未做到純 transform 只更新 instance transform／TLAS refit。 |
| 28 | BLAS Cache | ❌ | 尚無每 Chunk／Grid BLAS cache。 |
| 29 | Static BLAS | 🟡 | 靜態世界已有獨立 BLAS，Grid transform 不再令它失效；尚未拆成每 Chunk cache。 |
| 30 | Dynamic BLAS | 🟡 | 所有 Physics Grid 使用獨立於世界的 dynamic BLAS，相同 vertex count 走 `PERFORM_UPDATE` refit；尚未拆成每 Grid BLAS。 |
| 31 | Instance TLAS | 🟡 | TLAS 已有 static world＋dynamic Grid 兩個 instance；尚未達到每 Chunk／每 Grid instance。 |
| 32 | Ray Distance Limit | ✅ | DXR primary／shadow／reflection／refraction `TMax=220`；Compute PT world trace 上限 140，沒有無限距離射線。後續可依世界 bounds 收緊而不裁掉可達幾何。 |
| 33 | Ray Cone | ❌ | 尚無 ray cone footprint／texture LOD。 |
| 34 | Ray Compaction | ❌ | 尚無 active-ray queue／prefix sum compaction。 |
| 35 | Shader Table Cache | ✅ | Shader Table 在 DXR backend 建立時產生一次，所有 frame／DispatchRays 重用。 |
| 36 | Pipeline Cache | 🟡 | DXR State Object 在 backend 生命週期內重用；尚無 D3D12 pipeline library／跨啟動磁碟 cache。 |
| 37 | AS Refit | 🟡 | Dynamic Grid BLAS 建立時啟用 `AllowUpdate`／`PreferFastBuild`，相同頂點數更新使用 `PerformUpdate`；RTX 5050 strict smoke Debug 9.24 ms、Release 5.92 ms。Static Chunk 與 TLAS 尚未 refit。 |
| 38 | Skip Invisible BLAS | 🟡 | 品質 0 會在建 monolithic BLAS 時略過保守視錐外 Chunk；品質 1–3 為避免再次破壞陰影／反射保留全場景。待每 Chunk BLAS 後，必須以 primary＋shadow＋reflection/refraction 可達集合決定 TLAS inclusion，不能只看主視錐。 |
| 39 | RTX Instance Culling | 🟡 | 現在只有 build-time Chunk mask／Grid bounds，尚無真正多 instance TLAS culling。 |

### 五、Compute Path Tracing（40–47）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 40 | Adaptive Sampling | ❌ | 每個 pixel 每輪固定取樣。 |
| 41 | Russian Roulette | ✅ | bounce > 0 後依 throughput survival probability 終止並補償權重。 |
| 42 | Importance Sampling | ✅ | diffuse 使用 cosine hemisphere；specular／diffuse 依 Fresnel／metallic 機率選擇並做 PDF 權重補償，另有直接方向光 shadow ray。這是基礎 V1，尚非 MIS。 |
| 43 | Blue Noise | ❌ | 使用 hash RNG，沒有 blue-noise texture／sequence。 |
| 44 | ReSTIR | ❌ | 尚無 reservoir sampling。 |
| 45 | Wavefront PT | ❌ | 目前是單一 compute megakernel。 |
| 46 | Ray Sorting | ❌ | 沒有依 material／direction／bounce queue 排序。 |
| 47 | Temporal Reuse | 🟡 | 已有跨 frame running-average accumulation；尚無 motion-aware sample／reservoir reuse，攝影機或世界變更會 reset。 |

### 六、Memory（48–53）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 48 | Texture Streaming | ❌ | atlas／voxel textures 一次常駐。 |
| 49 | Mesh Streaming | ❌ | Raster 為固定世界全部 Chunk 配置 mesh object。 |
| 50 | Chunk Streaming | 🟡 | 無邊界 X／Z、半徑 3 Chunk 背景生成、逐幀 commit、Raster background mesh、Compute ring edge upload 與 DXR background CPU geometry 已接入；遠距 world unload／Region read cache／LOD 尚未完成。 |
| 51 | GPU Upload Budget | 🟡 | Raster／Compute 已有每幀 1 Chunk count budget，`FrameWorkBudget` 也保存 byte／ms 目標；尚未依實測成本做跨幀 byte/time cutoff 或 staging fence。 |
| 52 | Arena Allocator | 🟡 | BEPU 使用 `BufferPool`，Raster vertex／greedy mask 使用 `ArrayPool`；DXR geometry 與一般遊戲資料尚無統一 frame／persistent arena。 |
| 53 | Object Pool | 🟡 | BEPU buffer/body 與 Raster mesh build arrays 會重用底層配置；遊戲與 UI 尚無通用 object pool。 |

### 七、Voxel（54–58）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 54 | Voxel Clipmap | ❌ | 尚無多層 clipmap。 |
| 55 | Sparse Chunk | ✅ | `VoxelChunkStore` 只建立非空 Chunk、空 Chunk 自動移除；存檔也只寫 loaded/non-empty Chunk。 |
| 56 | Sparse Voxel | 🟡 | micro voxel 使用 sparse box dictionary；regular voxel 在已配置 Chunk 內仍為 dense `ushort[]`。 |
| 57 | Chunk LOD | ❌ | 尚無 Chunk 幾何層級。 |
| 58 | Distance Mesh | ❌ | 尚無遠距簡化 mesh／impostor。 |

### 八、Shader（59–63）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 59 | Shader Variant Cache | 🟡 | 每個 backend 生命週期內 program/state object 會重用，OpenGL uniform location 也有 dictionary cache；尚無 variant key、binary cache 或跨啟動 cache。 |
| 60 | Specialization Constant | ❌ | OpenGL shader 使用 uniforms，DXR 使用 root constants；沒有 specialization constant。 |
| 61 | Branch Reduction | ❌ | 尚未有以 profiler／shader permutation 驗證的系統性 branch reduction，不把現有零星 early-out 算完成。 |
| 62 | LUT（例如 BRDF LUT） | ❌ | 尚無 BRDF LUT；材質 atlas 不等於 lighting LUT。 |
| 63 | Shared Material | ✅ | Raster、Compute PT、DXR 共用 `BlockDefinition` 與同一套 `ProceduralMaterialAtlas` color/detail 資料。 |

### 九、世界更新（64–68）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 64 | Update Budget | 🟡 | 結構分析每次最多 6 個 dirty seeds；Chunk／collider commit、Raster／Compute upload、autosave snapshot 均有 count budget；DXR Grid update 以 120 ms coalesce。尚缺可自適應的硬性 ms／byte cutoff。 |
| 65 | Save Budget | 🟡 | Autosave 每幀最多擷取 1 個 Chunk snapshot，背景單寫者處理 Region；手動明確保存仍會等待完整 generation 完成。 |
| 66 | Async Save | 🟡 | 自動保存與 benchmark Region write 已在背景單寫者執行；手動 `SaveWorld` 為維持離開世界的完成語意仍同步等待。 |
| 67 | Async Generate | 🟡 | 玩家周圍 Natural Chunk 使用可取消的 `BelowNormal` worker 與逐幀 commit；整個世界的管理介面 `Regenerate` 仍是明確同步操作。 |
| 68 | Async Lighting | ❌ | 沒有背景 lighting job；目前主要 lighting 是每幀 shader。 |

### 十、UI（69–70）

| # | 項目 | 現況 | Repository 證據／下一步 |
|---:|---|:---:|---|
| 69 | ImGui Dirty Update | ❌ | ImGui 每幀重建 draw data，controller 也每幀 `BufferData` 上傳；尚無靜態面板 cache／persistent UI buffers。 |
| 70 | Pause GPU | ❌ | 暫停選單會停止遊戲輸入／更新，但背景 3D Renderer 仍以完整 frame rate 渲染。後續可暫停累計、重用最後一張世界畫面並只重畫 UI，不改變返回遊戲後的畫質。 |

### 依目前程式碼重新排序的前十項

原清單中的 Frustum Culling、Chunk Culling、Mesh Cache、Dirty Chunk、Physics Sleeping、Texture Atlas、Shadow Cache、Russian Roulette、基本 Importance Sampling 與 Shader Table reuse 已全部或部分存在，因此不再把它們當成「從零開始」的前十項。下一輪建議依相依性安排：

1. DXR Static per-Chunk BLAS cache。
2. DXR Dynamic per-Grid BLAS。
3. Multi-instance TLAS＋只更新 transform／instance mask。
4. D3D12→OpenGL 非同步／共享資源，移除同步 readback。
5. GPU staging／persistent upload ring 與依 ms／byte 的跨幀 budget。
6. Raster MultiDraw／Indirect Draw，減少每 Chunk Draw Call。
7. Region dirty payload merge 與遠距 Chunk／mesh／AS unload。
8. sparse vertical Section＋Palette，降低生成／快照配置量。
9. 保守 RTX instance culling（包含 shadow/reflection/refraction ray 可達性驗收）。
10. Deferred Tiled Light Culling；Clustered Deferred 待大型燈光壓力測試後接續。

Compute PT 的 Adaptive Sampling、Blue Noise、Wavefront／Ray Sorting、Temporal Reuse／ReSTIR 是獨立後續階段，不與 DXR AS 重構混在同一輪。世界已突破固定 64×32×64 bounds；下一階段可在現有惰性 Chunk 基礎上實作 Texture／Mesh streaming、遠距 unload、Voxel Clipmap 與 LOD。

### 每條 Renderer 的 Optimization Profile

目前 Repository 實際有六條可選 Pipeline（原本五條之外已加入 Hybrid PBR Raster），Profile 以各自資料流為準，不做一個假開關套全部：

| Pipeline | 目前可用 | 下一階段專屬優化 |
|---|---|---|
| Classic | Frustum／Chunk culling、Greedy Mesh、pooled Mesh cache、Dirty coalesce、背景 meshing、count upload budget、Material atlas | Backface Culling、persistent upload ring、MultiDraw |
| Forward PBR | Classic 基礎＋共用 PBR material batching | 量測後決定 Depth Pre-pass、透明 Render Queue、MultiDraw／Indirect |
| Deferred | Raster 基礎＋Depth Pre-pass、Light radius rejection、Shadow cache | Tiled Light Culling、Clustered Deferred、Light Volume／Shadow atlas cache |
| Hybrid PBR Raster | Deferred opaque 流程＋Forward water／glass | 與 Deferred 共用 tile/cluster，另保留透明 queue／reflection cache |
| Compute Path Tracing | per-Chunk 增量上傳、3D texture ring edge streaming、跨幀 accumulation、Russian Roulette、基本 Importance Sampling；超過 140 格的 Grid 不送入 SSBO，每個 Grid 有整體 AABB broad phase | Physics Grid 真正 BVH／spatial hierarchy、大型 teleport upload budget、Adaptive Sampling、Blue Noise，之後才是 Wavefront、Ray Sorting、Temporal reservoir／ReSTIR |
| Hardware RTX | 固定 ray distance、static/dynamic BLAS 分離、dynamic refit、雙 instance TLAS、runtime Pipeline／Shader Table／scratch reuse、品質 0 保守 build mask | per-Chunk/per-Grid BLAS cache、multi-instance TLAS、transform-only update、非同步 readback、ray-aware instance culling |

## 1.0 — Platform Release

- [ ] 穩定的 world/content/mod 格式與遷移器。
- [ ] Mod 管理器、錯誤隔離、安全模式、相依版本 UI。
- [ ] 藍圖匯入匯出與可分享展示世界。
- [ ] 大型世界壓力測試、GPU 相容矩陣和無 RTX 降級體驗。
- [ ] 內建教學，以建造與展示任務引導工具，但不加入生存壓力。
