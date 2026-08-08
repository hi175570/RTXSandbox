# RTX Sandbox

RTX Sandbox 是一個以 C#／.NET 開發的「體素建造、物理實驗、多渲染管線與 Mod 沙盒」。它不是只展示單一 Shader 的渲染 Demo：同一個世界、材質、物理與存檔資料，可以在六條不同渲染管線之間切換，用來建造、破壞、測試流體、觀察結構物理、比較 Raster／Path Tracing／Hardware Ray Tracing，並透過內容包與 BepInEx Mod 擴充。

目前 Windows x64 是完整功能與 Hardware DXR 的主要平台；Linux x64 已實際完成遊戲核心、OpenGL 渲染與 Mod 啟動驗證；Android、ARM64 與 macOS 的狀態各不相同，詳見「[全平台支援](#全平台支援)」。本 README 只把目前 Repository 已存在且有證據的功能列為完成，不把跨編譯、預留入口或正在接入的功能寫成完成。

> 專案仍在密集開發中。最精確的逐項完成狀態、實機證據和已知限制請看 [ROADMAP.md](ROADMAP.md)；效能工作請看 [ROADMAP_OPTIMIZATION.md](ROADMAP_OPTIMIZATION.md)。

## 目錄

- [專案定位](#專案定位)
- [主要功能](#主要功能)
- [六條渲染管線](#六條渲染管線)
- [世界、Chunk 與程序生成](#世界chunk-與程序生成)
- [材質、玻璃、光源與水](#材質玻璃光源與水)
- [建造、Micro Voxel 與物理](#建造micro-voxel-與物理)
- [介面與遊戲流程](#介面與遊戲流程)
- [存檔與世界管理](#存檔與世界管理)
- [Mod 與內容擴充](#mod-與內容擴充)
- [全平台支援](#全平台支援)
- [建置與執行](#建置與執行)
- [測試與效能量測](#測試與效能量測)
- [操作方式](#操作方式)
- [輸出目錄](#輸出目錄)
- [專案結構](#專案結構)
- [已知限制](#已知限制)
- [相關文件](#相關文件)

## 專案定位

RTX Sandbox 的核心定位是：

```text
Creative voxel building
+ structural physics experiments
+ shared material/world data
+ six comparable rendering pipelines
+ precision building and editor tools
+ content packs and BepInEx Mods
```

基礎遊戲採創造模式，不以飢餓、資源採集、怪物或科技樹為主線。舊的生存系統 API 仍可保留給 Mod 相容性使用，但遊戲本體的主要循環是：

1. 選擇世界、材質與工具。
2. 建造、挖除、替換或精細雕刻。
3. 讓結構進入 Physics Grid，觀察重力、碰撞與破壞。
4. 切換時間、天氣感與 Renderer，比較視覺結果。
5. 儲存世界，或用內容包／Mod 增加新內容。

## 主要功能

### 體素世界

- X／Z 水平方向採無界 Chunk 座標與按需程序生成，不以舊的 64×64 GPU 視窗作為世界邊界。
- 世界垂直座標為 Y `-64..511`，上界 `512` exclusive。
- Chunk 水平尺寸為 `16×16`；Region 將 `32×32` 個 Chunk 合併保存，也就是每個 Region 覆蓋 `512×512` 方塊。
- Flat、Natural、Pure Nature、Void、Showcase 五種世界預設。
- Auto、Plains、Forest、Bamboo Forest、Desert、Snow、Mountains、Swamp 等生態設定。
- Natural 生成包含 continentalness、erosion、ridge、domain warp、氣候、河流、洞穴、地質與植被。
- 背景 Chunk 生成、距離優先佇列、過期工作取消、每幀提交預算與 dirty Chunk 精確更新。

### 建造與編輯

- 一般方塊放置、挖除、替換與中鍵取樣。
- 九格快捷列、搜尋、分類與 3D 等角方塊預覽。
- A／B 區域選取、填滿、清除與條件替換。
- 複製、貼上、Y 軸旋轉、X 軸鏡像。
- 球形新增／刪除筆刷、平整與爆炸工具。
- 最多 64 筆建造操作 Undo／Redo。
- 1/2 到 1/64 的精密 Micro Voxel 建造，以及混合網格的精確資料表示。
- 方塊準星資訊限制為最多三行，避免遮住遊戲畫面。

### 物理

- 使用 BEPUphysics 2。
- 靜態 Chunk collider 與可移動的 compound Physics Grid。
- 六面結構支撐、斷裂、材質密度、質量、重心、摩擦、旋轉、碰撞、CCD 與睡眠。
- 120 Hz 固定物理步進與 catch-up 上限，避免低幀率時無限制補算。
- Physics Grid 擠壓玩家時採連續水平防卡推離，不依賴物體速度瞬間把玩家傳送到上方。
- 可被玩家推動、碰撞和破壞的解析球體；球體視覺使用解析表面，物理使用球形 collider。

### 資料驅動內容

- 目前 Roadmap 記錄 196 個資料驅動定義、194 種可建造材質。
- 16-bit Block ID，Renderer 目前保留 4,096 個材質槽。
- `namespace:key` 穩定識別碼，存檔可依 key remap，降低內容包載入順序改變造成的 ID 錯位。
- 材質可定義 Albedo、Metallic、Roughness、AO、Emission、Transmission、IOR、碰撞、流體、重力、表面圖樣、聲音與粒子中繼資料。
- 程序材質 Atlas 支援 top／side／bottom 不同表面、草方塊側面方向、roughness 與高度法線細節。

## 六條渲染管線

六條 Renderer 共用同一份世界、方塊定義、材質參數、Atlas、Micro Voxel 和 Physics Grid 資料。各管線可以有自己的品質設定與實作差異，但不應維護互相矛盾的遊戲世界。

| 順序 | Renderer | 技術 | 目前定位 |
|---:|---|---|---|
| 1 | Classic | OpenGL Raster | 低負載、相容與建造工作模式 |
| 2 | PBR Forward | Forward PBR Raster | 高品質即時 Raster、動態陰影與水 |
| 3 | Deferred Lighting | G-Buffer Deferred | 多材質／多光源、Debug G-Buffer 與後處理 |
| 4 | Hybrid PBR Raster | Deferred opaque + Forward transparent | 類高品質 Shader Pack 的混合 Raster |
| 5 | Compute Path Tracing | OpenGL 4.3 Compute Shader | 漸進式 Path Tracing；不是硬體 RT API |
| 6 | Hardware Ray Tracing | Direct3D 12 DXR | 真正建立 BLAS／TLAS、Shader Table 並 DispatchRays |

### Classic

Classic 是最低成本的貼圖 Raster 管線，保留世界材質、Micro Voxel、水、透明物與基本日夜光照。它適合建造、除錯、低負載裝置或與其他 Renderer 作為效能對照。

獨立設定包括：光照品質、日光強度、環境光、色彩飽和度、水品質、水波強度、透明品質與霧密度。

### PBR Forward

PBR Forward 使用 Cook–Torrance BRDF、GGX NDF、Smith geometry 與 Schlick Fresnel，並包含：

- 共用材質 Atlas、法線、Metallic、Roughness、AO 與 Emission。
- Directional shadow map 與可調解析度、bias、normal bias、slope bias、柔化和距離。
- 最多 16 個可見發光代理光源。
- SSAO、SSR、HDR、曝光、Tone Mapping、Bloom、霧與近似 GI。
- Forward 水、透明、玻璃與螢幕空間反射。
- Potato、Medium、High、Ultra 與 Custom 品質預設。
- 14 種 Debug View，包括 Albedo、Normal、Depth、Roughness、Metallic、Emission、AO、Shadow、Direct／Ambient Lighting、SSR、Approximate GI 與 NaN／Infinity。

它目前不是完整的電影級 Forward+：尚未完成 CSM、點光／聚光陰影 Atlas、離屏 Reflection Probe、真正多反彈 GI 與完整 motion-vector TAA。

### Deferred Lighting

Deferred 不是只有骨架，而是實際執行：

```text
Shadow Pass
→ Depth Pre-Pass
→ Five-target G-Buffer Geometry Pass
→ Full-screen PBR Lighting
→ HDR / Bloom / Tone Mapping
→ Forward Water and Transparency Pass
```

G-Buffer 包含 Albedo／Roughness、Normal／Metallic、World Position／Material ID、Emission／Selection、Motion Vector 等資料。Lighting 支援 Directional、Point、Spot、Ambient 與 Sky approximation，並提供 SSAO、SSR、Shadow、HDR、Bloom、水、玻璃及 19 種 Debug View。

獨立品質設定涵蓋 Renderer、G-Buffer、Lighting、Shadow、Shadow Resolution／Bias／Softness／Distance、Reflection、AO、Water、Transparency、Fog、Post Processing、HDR、Bloom、Tone Mapping 與 Debug View。

尚未完成的高階項目包括 CSM、Point／Spot shadow atlas、完整 IBL／Reflection Probe、SSGI、Tiled／Clustered Deferred、OIT、完整體積霧與完整 TAA。

### Hybrid PBR Raster

Hybrid PBR Raster 位於 Deferred 與 Compute Path Tracing 之間，是一條真正獨立存在的高品質 Raster Pipeline：不透明世界使用 Deferred PBR 與陰影，水、玻璃和其他 transmission geometry 使用 Forward pass。

它有自己的 Lighting、Specular、Shadow、Shadow Resolution、Reflection、AO、Water、Transparency、Fog 與 Bloom 設定，不是把 Deferred 選項改名，也不是 Path Tracing。

### Compute Path Tracing

Compute Path Tracing 使用 OpenGL 4.3 Compute Shader 實作漸進式路徑追蹤，支援基本 GI、陰影、反射、折射、Emission、透明材質、Micro Voxel、Physics Grid 與解析球體。

主要設定包括：

- 每條路徑反射／折射深度 1–50。
- Samples per frame 與最大累積 Samples。
- Ray distance。
- Direct lighting、Environment、Emission 與 Reflection 強度。
- Russian Roulette 起始深度。

這條管線不使用 DXR／Vulkan RT，因此即使在 RTX GPU 上執行也不應被描述為 Hardware Ray Tracing。它目前也沒有完整 Denoiser、MIS／NEE、ReSTIR、Wavefront PT 或最終級 Temporal Reprojection。

### Hardware Ray Tracing（DXR）

Hardware Ray Tracing 是 Windows 專用的 Direct3D 12 DXR 管線。它會實際：

- 建立 D3D12 Device 並檢查 Ray Tracing Tier。
- 建立 Command Queue、Command Allocator、Command List、Fence 與同步資源。
- 為靜態世界建立 BLAS，為場景建立 TLAS。
- 將動態 Physics Grid 與解析球體加入可更新的加速結構。
- 建立 Ray Generation、Miss、Any Hit、Closest Hit／Intersection shader 與 Ray Tracing Pipeline State。
- 建立 Shader Table 並呼叫 `DispatchRays`。
- 將 D3D12 結果 readback 到目前的 OpenGL 視窗呈現路徑。
- 初始化失敗時顯示實際原因並安全回退，不把 Compute PT 冒充 Hardware RT。

DXR 支援共用材質、基本法線明暗、直接光照、陰影、反射、折射、透明／彩色玻璃、水、發光材質、動態 Grid 和攝影機更新。Render Style 包含 Default、Wax Mode（打蠟模式）、Reference 與 Debug。

真實 DXR 反射最多 29 層。Infinite Mirror Extension 的 Extreme 模式可把平面鏡的視覺尾端延伸到 1000 層，但 30 層以後是 Reprojected／Analytic Tail 近似，不會被介面或文件假稱為 1000 次真實 DXR bounce：

```text
Real DXR Reflections:       1–29
Reprojected Reflections:   30–128
Analytic Reflection Tail: 129–1000
```

目前 DXR 還不是完整 Path Tracer：沒有完整多反彈 GI、Denoiser、DLSS／FSR／XeSS、Frame Generation、最終 per-Chunk BLAS 架構或完整動態流體光學。OpenGL／D3D12 之間目前仍有 readback 成本。

### 共用渲染規則

- 所有 Renderer 使用同一套方塊材質資料，包含 top／side／bottom 貼圖、Normal、Metallic、Roughness、AO、Emission、Transmission 與 IOR。
- Raster、Compute PT 和 DXR 都能讀取世界、Micro Voxel、Physics Grid 與水資料；細節能力依 API 不同。
- 可見 Chunk 採保守 1.6× 視錐範圍，避免玩家仍看得到的 Chunk 被過早剔除。
- 陰影視錐與相機視錐分離，避免只因物件不在主畫面內就讓陰影消失。
- 選取框可參與支援反射的 Renderer；統一設定不綁定單一管線。
- Voxel GI 目前不是第七條 Renderer，也沒有用空選項或假畫面佔位。

## 世界、Chunk 與程序生成

### 座標與儲存單位

| 項目 | 目前值 |
|---|---:|
| 水平世界 | 無界 Chunk 座標，按需生成 |
| 垂直範圍 | Y `-64..511` |
| Chunk | `16×16` X/Z，垂直資料稀疏保存 |
| Region | `32×32` Chunk |
| Region 覆蓋 | `512×512` 方塊 |
| 舊 GPU clip window | `64×64`，不是世界邊界 |

Chunk 生成在背景工作執行；完成結果由主執行緒依每幀預算提交。玩家改變方向或離開後，過期的生成／mesh 工作可被取消或丟棄，附近 Chunk 優先。Raster mesh 和 BEPU collider 都採 dirty Chunk 與版本檢查，避免舊工作覆蓋新資料。

### 世界預設

- **Flat**：可設定表面高度與地層材質。
- **Natural**：完整程序地形、生態、河流、洞穴、植被與地質。
- **Pure Nature**：偏自然展示的生成配置。
- **Void**：空世界，適合純建造或工具測試。
- **Showcase**：集中展示材質、玻璃、發光與物理內容。

世界建立介面可設定名稱、Seed、Preset、Biome 與進階世界選項。世界管理支援載入、重新命名、以相同 Seed 重建／覆蓋，以及刪除世界。

## 材質、玻璃、光源與水

### 共用 PBR 材質

內容定義存放在 `Content/Base`，外部內容包存放在 `ContentPacks/<pack>`。內建材質分類包含 Nature、Wood、Building、Metal、Technology、Optical、Lighting 與 Special。

常用資料欄位：

```text
key / id / displayName / category / tags
albedo / metallic / roughness / ambientOcclusion
emission / transmission / indexOfRefraction
isTransparent / isCollidable / isFluid / affectedByGravity
surfacePattern / textureGenerator / soundSet
```

### 玻璃

玻璃家族包括一般玻璃、彩色玻璃、磨砂、霧面、馬賽克、壓克力與雕紋／蝕刻玻璃。物品庫會把玻璃集中排序，彩色系列採穩定顏色順序；磨砂、霧面與馬賽克使用不同尺度的表面細節。

DXR 可讓白光穿過彩色玻璃後依 transmission 顏色染色，並處理玻璃入口／出口 IOR。Raster 管線則使用各自可負擔的透明、折射與螢幕空間近似，不能把所有離屏彩色焦散都視為已完成。

### 發光材質

Emission 不只改變方塊自身顏色。Raster 管線建立有限數量的發光代理光；Compute PT 和 DXR 會在其追蹤流程中使用發光資料。遮擋、距離與色彩仍依 Renderer 的光照模型而異；完整無偏差多重彈射不是目前 DXR V1 的完成項目。

### 有限體積水

水不是固定高度的純裝飾方塊。每格使用 16 單位流體量，完整水面高度為 0.875 格，最低薄層約為完整量的 1/16。現有機制包含：

- 向下落下與向四周擴散。
- 量守恆與薄層表面。
- 固體放入水中時的排水與鄰格溢流。
- 小範圍連通管壓力／液面平衡。
- 世界修改、存檔、載入及 Renderer 更新。
- 游泳阻力；跳躍鍵在不同水深仍提供足以離水的跳躍，不用自動爬牆。

目前水模擬仍不是 Navier–Stokes：尚無完整速度向量、黏度、泵浦／活塞、跨未載入 Chunk 的全域連通或大型海洋壓力求解。

## 建造、Micro Voxel 與物理

### Micro Voxel 精密建造

按 `V` 切換精密模式；支援 1/2、1/4、1/6、1/8、1/16、1/32、1/64 網格。資料採 sparse container，不會讓整個世界固定配置 64³ 子方塊。Micro Voxel 支援：

- 放置、挖除、中鍵取樣。
- Raycast 與玩家碰撞。
- 結構支撐與 Physics Grid 轉換。
- Undo／Redo、存檔與載入。
- Raster、Compute PT 與 DXR 幾何。

先前嘗試的任意函數曲面建造工具因效能和效果不符合要求，已從主線取消；目前不把任意曲面編輯器列為完成。解析球體是獨立的程序表面／物理物件，不代表已支援任意曲面。

### Physics Grid

被判定失去支撐的結構可轉成動態 Grid。每個 Grid 保存局部方塊／Micro Voxel 拓撲、世界 transform、質量與慣性；同拓撲移動時 Raster 只更新 model matrix，DXR 優先更新動態加速結構，不應因單純 transform 變化重建整個靜態世界。

仍待後續的物理項目包括碰撞衝擊造成的二次斷裂、超大型 Grid 的分區 shape rebuild、完整爆炸碎片與聲效，以及更大規模的 collider／mesh 串流。

## 介面與遊戲流程

桌面版使用 ImGui.NET，主要畫面包括：

- 世界選擇與世界建立。
- 遊戲 HUD、九格快捷列、選取框與三行方塊資訊。
- Creative Library／物品庫。
- 建造工具與精密模式。
- ESC 暫停選單：繼續、六個功能入口、離開世界。
- 設定：全域顯示設定與當前 Renderer 專屬光影設定。
- Mod 管理、檔案右鍵選單與簡易文字編輯器。

設定畫面的完成按鈕會回到上一層暫停選單；Mod 不再混在設定頁。再次按 ESC 會儲存並關閉暫停選單，視窗失去焦點時會自動開啟暫停選單。桌面中文輸入使用 IME composition，避免組字跑到左上角或中文輸入造成操作焦點卡住。

Android 已有觸控虛擬按鍵、八方向 D-pad／搖桿配置、跳躍、蹲下、左右鍵、快捷列、物品庫，以及可在遊戲內重新綁定並保存的外接鍵盤輸入。桌面與 Android 直接共用 `SharedImGuiScreens` 的暫停、成就與語言畫面；Android Editor 使用與 PC 相同的 `Blocks / Tools`、九格快捷欄、搜尋與材質卡片排列，設定使用相同三頁籤與配色。觸控編輯是 Android 唯一額外的暫停選單區塊。Android 也已接入世界建立、載入、重新命名、同 Seed 重建與刪除；Mod runtime 與所有桌面進階 Renderer 細項仍待接入，詳見「[Android](#android)」。

## 存檔與世界管理

- 世界根資料使用 `level.json`。
- Chunk 以 Region 檔合併保存，不會一個 16×16 Chunk 產生一個獨立檔案。
- Region 格式保存方塊、Micro Voxel、流體與必要的世界資料。
- 儲存採 generation 目錄、原子切換與 `.bak` 備份，避免寫入中斷直接破壞唯一存檔。
- 玩家位置、快捷列、世界生成設定、Seed、Physics Grid 等會隨世界保存。
- 舊格式可經 migration 讀入；未知 namespaced material key 有明確 remap／fallback 行為。
- 世界重新命名只改名稱；Rebuild 保留名稱與 Seed、重建世界內容；刪除使用明確世界目標。

## Mod 與內容擴充

### 內容包

不需要執行任意程式碼的方塊與材質可用 JSON 內容包：

```text
ContentPacks/
└─ MyPack/
   ├─ pack.json
   └─ blocks/
      └─ my-blocks.json
```

內容包採 namespaced key；同 key 的覆寫依載入與 force-override 規則處理。

### BepInEx Mod

桌面 Host 使用 BepInEx 6 CoreCLR `6.0.0-be.785` 與 HarmonyX。插件放置方式：

```text
BepInEx/plugins/
└─ MyMod/
   ├─ MyMod.dll
   └─ 其他相依 DLL
```

公開的 `RTXSandbox.ModAPI` 目標為 `netstandard2.1`，提供 Blocks、Items、Recipes、Tools、World、Rendering、Physics、Events 與 service locator 等介面。遊戲本體預設使用 `net9.0`，可由 .NET 10 SDK選擇性建置 `net10.0` Host，以改善新舊 TFM Mod 的 Runtime 條件；這不代表所有第三方依賴一定二進位相容。

遊戲內 Mod 頁面可對文字／C# 檔案開啟右鍵選單，進行編輯、保存、移動、重新命名、刪除與手動編譯。編譯永遠保留原始碼；刪除走回收筒／可恢復流程。第一版編譯器會引用遊戲 `Managed`、Mod 目錄及依賴 DLL，但目前沒有安全可靠的 Runtime Hot Reload，完成編譯後仍建議重新啟動遊戲。

> Mod 是可執行程式碼，只安裝可信來源。ARM64 上 MonoMod／RuntimeDetour 仍有上游不支援警告，依賴 Runtime patch 的 Mod 不能宣稱完整相容。完整說明見 [MODDING.md](MODDING.md)。

## 全平台支援

### 支援層級的定義

- **實機／VM 驗證**：不只編譯，已在該平台啟動並執行測試。
- **核心驗證**：共享世界、存檔或 self-test 已執行，但沒有該平台 GPU 畫面證據。
- **跨編譯**：只證明能產生目標架構檔案，不等於可玩。
- **進行中**：有正式程式碼，但尚未達到完整一致或尚未完成本輪重新驗證。

### 平台狀態（2026-08-08）

| 平台 | 架構 | 目前證據 | Renderer／限制 | 狀態 |
|---|---|---|---|---|
| Windows 10/11 | x64 | Debug／Release、91/91 self-tests、RTX 5050 DXR smoke、BepInEx 實際載入 | 六條 Renderer；DXR 完整主要驗證平台 | **主要支援** |
| Windows | ARM64 | 可產生 self-contained／framework-dependent 發行檔 | 尚無 ARM64 Windows 實機；MonoMod hook 有限制 | **跨編譯** |
| Linux（Ubuntu／WSL） | x64 | 87/87 self-tests；Mesa／Xvfb GL 4.3 下 Classic、PBR、Deferred、Hybrid、Compute smoke；BepInEx `Started: True` | 五條 OpenGL Renderer；無 D3D12 DXR | **實際驗證** |
| Linux | ARM64 | self-contained 發行檔在 QEMU AArch64 通過 87/87 self-tests | 尚無 ARM64 GPU 畫面；Mod hook 有限制 | **核心驗證** |
| macOS | x64／ARM64 | 兩種 RID 可跨編譯，已有 OpenGL 4.1／GLSL 410 fallback | 沒有 Apple 實機；Compute PT 需要 GL 4.3 因而停用；無 DXR | **跨編譯** |
| Android 11+ | x64 | API 30 Emulator 實際覆蓋安裝、舊世界存讀、完整世界管理、流水、Micro Voxel、Physics Grid、觸控／可編輯外接輸入、共用 UI，以及 Classic／PBR／Deferred／Hybrid／Compute | host-GPU GLES 3.1 VM 中 Classic、Deferred、Compute 均約 58–60 FPS；Hardware DXR 不可用；Mod runtime 與完整桌面 Renderer 細項尚未接入 | **V1 可玩／持續接入** |
| Android 8+ | ARM64 | Release APK、共用世界、流水、Micro Voxel、BEPU Physics Grid、輸入及 16 KB 對齊 ImGui native library已封裝；已安裝至 Samsung A32／Android 13 | 手機受安全鎖保護，無權繞過；本輪只能確認安裝，尚未驗證真機 3D 畫面、效能及多點觸控 | **安裝完成／解鎖後複驗** |
| iOS／iPadOS | ARM64 | 無正式 target | 尚未處理 Metal、簽名、觸控 Host 與發佈 | **未支援** |

### Renderer 可用性

| Renderer | Windows x64 | Linux x64 | macOS 目標 | Android V1 |
|---|:---:|:---:|:---:|:---:|
| Classic | ✅ | ✅ | 預期可用，待實機 | ✅ GLES |
| PBR Forward | ✅ | ✅ | 預期可用，待實機 | ✅ GLES V1 |
| Deferred | ✅ | ✅ | fallback 已編譯，待實機 | ✅ GLES 3 MRT G-buffer |
| Hybrid PBR Raster | ✅ | ✅ | fallback 已編譯，待實機 | ✅ GLES 3 Deferred + screen-space reflection |
| Compute Path Tracing | ✅ GL 4.3 | ✅ GL 4.3 | ❌ Apple OpenGL 4.1 | ✅ GLES 3.1 Compute V1 |
| Hardware DXR | ✅ D3D12 DXR | ❌ | ❌ | ❌ |

### Android

Android 專案不是獨立世界 Demo：它直接 link／重用 `World`、`PlayerController`、`Camera`、`CreativeHotbar`、`ItemRegistry`、`WorldSaveService`、平台服務與 ModAPI，並使用同一份基礎內容 JSON。APK 支援 `android-x64` 與 `android-arm64`，最低 API 26，現有 Emulator 驗證使用 API 30。

已存在：

- 相同世界生成、Region save、玩家與快捷列資料。
- Classic GLES、PBR Forward GLES、GLES 3 MRT Deferred、Hybrid PBR Raster，以及 GLES 3.1 Compute Path Tracing V1。
- 可編輯虛擬按鍵 Profile、八方向 D-pad 或搖桿。
- 跳躍、Shift 蹲下、Ctrl＋前進／連按兩次前進衝刺、移動、左右鍵、V／B、物品庫與快捷列。
- 外接鍵盤、滑鼠與 Gamepad 事件映射；外接鍵盤綁定可在 ImGui 頁面捕捉、重設並跨重啟保存。
- App 失去焦點時釋放輸入、開啟暫停，並以合併式背景工作保存目前世界，避免在 Activity 主執行緒等待 Region I/O。
- GLES 上的 Dear ImGui 暫停、物品欄、建造工具、Renderer、繁中／簡中／英文語言與成就列表；觸控編輯器只由暫停選單開啟。
- Android 主選單可建立、載入、同 Seed 重建、重新命名及刪除世界；離開世界會先保存再返回主選單。

尚未完成：

- 將桌面的 Mod 管理與所有進階 Renderer 設定接到 Android GLES Host。
- Hardware DXR 是 Windows D3D12 專用，不在 Android 以其他 API 冒充。Android Compute V1 尚未包含 Micro Voxel／Physics Grid 的解析求交與完整透明折射語意。
- Micro Voxel／Physics Grid 的 V1 顯示、放置、刪除與 BEPU 模擬已接入；仍需真機多點觸控、長時間壓測與所有桌面編輯工具的一致性複驗。
- ARM64 實機效能、記憶體與所有 ABI 的最終驗證。
- Android Mod runtime 與桌面 BepInEx 等價能力。
`libcimgui.so` 的 x64／ARM64 版本已以靜態 C++ runtime 重新連結，ELF `LOAD` alignment 均為 `0x4000`，符合 16 KB page-size；Android x64 Debug 與 ARM64 Release 都以 `0 warnings / 0 errors` 建置。這代表核心 ImGui 已正式接入，但不代表所有桌面頁面都已移植。Android 專用建置說明見 [RTXSandbox.Android/README.md](RTXSandbox.Android/README.md)。

### macOS、Linux 與 ARM64

- Linux 使用 OpenTK／OpenGL 路徑；BepInEx 的 Unix console 啟動問題已處理並在 x64 驗證。
- macOS 因 Apple OpenGL 停在 4.1，Shader 會選 GLSL 410 fallback；需要 Compute Shader 的 Path Tracing 不顯示為可用。
- Hardware DXR 只在 Windows D3D12 上提供，其他平台會顯示不可用原因並安全回退。
- x64 與 ARM64 都有 publish RID，但「有發行檔」不等於「GPU、原生依賴和 Mod hook 全部驗證」。
- macOS 最終發佈仍需要 Apple 硬體上的簽名、Gatekeeper、OpenGL context、輸入與檔案權限驗證。

## 建置與執行

### 桌面需求

- .NET 9 SDK（預設 Host）。
- Windows／Linux：支援 OpenGL 4.3 的 GPU 與驅動，才能使用 Compute Path Tracing。
- macOS：OpenGL 4.1 fallback；不支援 Compute PT。
- Hardware DXR：Windows 10/11、D3D12、DXR Tier 1.0+ GPU／驅動。
- Visual Studio 2022 可使用預設 `net9.0`；Visual Studio 2022 不能直接載入 net10 TFM。

### Visual Studio

開啟 `RTXSandbox.sln`，選擇 `RTXSandbox` 為啟動專案並建置。專案已將啟動程式設為輸出根目錄的 apphost，而不是 `Managed` 內的 DLL，避免 VS 將工作目錄錯設到 DLL 目錄。

### CLI

```powershell
dotnet restore RTXSandbox.sln
dotnet build RTXSandbox.sln -c Debug
dotnet build RTXSandbox.sln -c Release
dotnet run --project RTXSandbox.csproj -c Release
```

### 選擇性 .NET 10 Host

預設仍為 `net9.0`，確保 Visual Studio 2022 可以正常載入。安裝 .NET 10 SDK 後可用：

```powershell
dotnet build RTXSandbox.sln -c Release `
  -p:RTXSandboxHostTargetFramework=net10.0
```

`RTXSandbox.ModAPI` 維持 `netstandard2.1`。這是選擇性 Host 目標，不要求為升級 Runtime 重寫遊戲系統。

### 一次發行桌面平台與 Android

```powershell
# Framework-dependent
.\build\Build-Platforms.ps1 -Configuration Release

# Self-contained
.\build\Build-Platforms.ps1 -Configuration Release -SelfContained
```

Linux／macOS shell：

```bash
./build/build-platforms.sh Release
./build/build-platforms.sh Release --self-contained
```

桌面 RID：

```text
win-x64      win-arm64
linux-x64    linux-arm64
osx-x64      osx-arm64
```

### Android APK

先安裝 .NET Android workload、Android SDK／NDK 與 JDK：

```powershell
dotnet workload install android

dotnet build RTXSandbox.Android/RTXSandbox.Android.csproj -c Release `
  -p:RuntimeIdentifier=android-x64

dotnet build RTXSandbox.Android/RTXSandbox.Android.csproj -c Release `
  -p:RuntimeIdentifier=android-arm64
```

安裝到裝置／Emulator：

```powershell
adb install -r <signed-apk-path>
```

若本機未自動找到 SDK／JDK，可依 [RTXSandbox.Android/README.md](RTXSandbox.Android/README.md) 傳入 `AndroidSdkDirectory` 與 `JavaSdkDirectory`。

## 測試與效能量測

### 共享核心測試

```powershell
dotnet run --project RTXSandbox.csproj -c Release --no-build -- --self-test
dotnet run --project RTXSandbox.csproj -c Release --no-build -- --mod-diagnostics
```

目前已記錄的共享測試總數為 91/91。測試涵蓋世界、Region save、流體、Micro Voxel、物理、Renderer 設定、平台服務、可編輯輸入、三語導航與 Mod 相關核心行為。

### GPU Smoke

```powershell
dotnet run -c Release --no-build -- --gpu-smoke-classic
dotnet run -c Release --no-build -- --gpu-smoke-pbr
dotnet run -c Release --no-build -- --gpu-smoke-deferred
dotnet run -c Release --no-build -- --gpu-smoke-hybrid
dotnet run -c Release --no-build -- --gpu-smoke
dotnet run -c Release --no-build -- --gpu-smoke-hardware
```

其中 `--gpu-smoke` 是 Compute Path Tracing；Hardware DXR 必須明確使用 `--gpu-smoke-hardware`。其他針對性測試包括：

```powershell
--gpu-smoke-hardware-infinite
--gpu-smoke-hardware-glass-occlusion
--gpu-smoke-hardware-dark-room
--gpu-smoke-menu
--gpu-smoke-pause
--gpu-smoke-inventory
--gpu-smoke-ime
--gpu-smoke-world-creator
--gpu-smoke-underwater
```

### 可重複效能與惡意壓力測試

```powershell
dotnet run -c Release -- --world-stream-benchmark
dotnet run -c Release -- --adversarial-performance-suite
```

測試工具可針對 Classic、PBR Forward、Deferred、Hybrid、Compute PT、Hardware DXR 使用相同路線與場景，量測 Chunk burst、邊界移動、連續放置／挖除、水、玻璃、發光材質、Micro Voxel 與 Physics Grid。惡意套件使用獨立測試世界並在完成後清理，避免在使用者世界清單留下垃圾存檔。

Benchmark 報告可記錄平均 FPS、1% Low、P99、最慢幀、最大連續 freeze、主執行緒阻塞、Chunk Generation／Mesh／Collider／GPU Upload／Region I/O，以及 Deferred Shadow、DXR BLAS／TLAS 等資料。實際數值與尚未達標項目記錄在 [ROADMAP_OPTIMIZATION.md](ROADMAP_OPTIMIZATION.md)，不以單次 GPU smoke 代替效能驗收，也不保證每台設備固定 120／150 FPS。

### CI

`.github/workflows/desktop-cross-platform.yml` 會：

- 在 Windows、Ubuntu、macOS runner 建置 desktop core 並執行 self-test。
- 產生六個 desktop RID artifact。
- 安裝 Android workload 並建置 ARM64／x64 APK。

CI 的跨編譯結果不能取代 RTX GPU、Android 實機或 Apple 實機驗證。

## 操作方式

### 基本移動與互動

| 輸入 | 功能 |
|---|---|
| WASD | 水平移動 |
| 滑鼠 | 轉動視角 |
| Space | 跳躍／飛行上升；快速按兩次切換飛行 |
| Shift | 蹲下、邊緣防掉落；飛行時下降 |
| Ctrl＋W／連按兩次 W | 衝刺；FOV 以平滑動畫擴張與恢復 |
| 左鍵 | 挖除／破壞選取物 |
| 右鍵 | 放置方塊／精密單元 |
| 中鍵 | 取樣看向的材質 |
| 1–9 | 選擇快捷列 |
| Q／滾輪 | 切換快捷列 |
| E／I／Tab | 開關 Creative Library／物品庫 |
| B | 開關建造工具箱 |
| V | 開關 Micro Voxel 精密模式 |
| Alt＋滾輪 | 切換精密網格 |
| N | 切換 Noclip；開啟時同時啟用飛行 |
| Esc | 開關暫停選單；從子畫面返回 |

### 編輯與世界工具

| 輸入 | 功能 |
|---|---|
| Ctrl+C／Ctrl+V | 複製／貼上選取區 |
| Ctrl+F | 填滿選取區 |
| Ctrl+Delete | 清除選取區 |
| X | 剪貼簿 Y 軸旋轉 |
| M | 剪貼簿 X 軸鏡像 |
| `[`／`]` | 筆刷半徑 -/+ |
| T | 暫停／恢復世界時鐘 |
| `,`／`.` | 時間 -/+ 1 小時 |
| `-`／`=` | 時間速度 ×0.5／×2 |
| R | 將時間設回正午 |

### Renderer 與除錯

| 輸入 | 功能 |
|---|---|
| F1 | VSync 開／關 |
| F2 | 循環 Path Tracing bounce 品質 |
| F3 | 顯示／隱藏 Debug HUD |
| F4 | 循環目前 Forward／Deferred／Hybrid Debug View |
| F5 | 儲存世界 |
| F6 | 切換 Classic |
| F7 | 切換 PBR Forward |
| PageUp／PageDown | Exposure +/− |
| Home／End | FOV −/+ |

## 輸出目錄

專案刻意把應用程式放在輸出根目錄，把 Managed DLL 與 Runtime 資料放入 `Managed`，方便 Mod 引用並避免根目錄堆滿 DLL：

```text
bin/Release/net9.0/
├─ RTXSandbox.exe            # Windows；其他平台為對應 native apphost
├─ Managed/
│  ├─ RTXSandbox.dll
│  ├─ RTXSandbox.ModAPI.dll
│  ├─ *.deps.json
│  ├─ *.runtimeconfig.json
│  └─ 第三方與 Runtime DLL
├─ BepInEx/
│  └─ plugins/
├─ Content/
├─ ContentPacks/
├─ Shaders/
└─ settings.json
```

跨平台 publish 使用相同概念：artifact 根目錄是 native apphost，`Managed/` 放 DLL 與 runtime，`Content`、`ContentPacks`、`Shaders` 和設定放在可攜式資料位置。

## 專案結構

```text
RTXsandbox/
├─ Program.cs                         命令列入口、自測與 Smoke 路由
├─ SandboxGame.cs                     桌面遊戲 Host、UI 與主要流程
├─ SandboxGame.StreamingBenchmark.cs  世界串流效能測試
├─ Configuration/                     設定 schema、載入與驗證
├─ Content/Base/                      內建方塊、物品與配方 JSON
├─ ContentPacks/                      外部資料包
├─ Gameplay/
│  ├─ PlayerController.cs             移動、飛行、蹲下與游泳
│  ├─ BuildToolSystem.cs              選區、筆刷、剪貼簿與 Undo
│  ├─ PrecisionBuildSystem.cs         Micro Voxel 精密建造
│  ├─ VoxelPhysicsSimulator.cs        結構支撐與 Physics Grid
│  └─ BepuPhysicsBackend.cs           BEPU collider、body 與背景建置
├─ World/
│  ├─ VoxelWorld.cs                   世界、生成、Chunk 與流體資料
│  ├─ VoxelChunkStore.cs              稀疏 Chunk 儲存
│  ├─ WaterFlowSimulation.cs          有限體積水
│  ├─ PhysicsGrid.cs                  動態方塊結構
│  └─ AnalyticSphere.cs               可推動解析球體
├─ Rendering/
│  ├─ Backends/RasterVoxelRenderer.cs Classic／Forward／Deferred／Hybrid
│  ├─ RayTracingRenderer.cs           OpenGL Compute Path Tracing
│  ├─ Backends/DxrHardwareRenderer.cs D3D12 DXR、BLAS／TLAS／DispatchRays
│  ├─ RenderBackendHost.cs            Renderer 切換與安全回退
│  └─ ProceduralMaterialAtlas.cs      共用材質 Atlas
├─ Shaders/                           GLSL 與 HLSL
├─ Persistence/WorldSaveService.cs    level／Region／generation 存檔
├─ Modding/                           BepInEx Host、編譯與診斷
├─ RTXSandbox.ModAPI/                 公開 netstandard2.1 Mod API
├─ RTXSandbox.Android/                Android Host、GLES、觸控與外接輸入
├─ Infrastructure/                    平台抽象、診斷與壓力測試
├─ UI/                                ImGui controller 與設定畫面
├─ build/                             多 RID 發行腳本
└─ .github/workflows/                 跨平台 CI
```

## 已知限制

以下內容不是「名稱已有所以完成」，而是目前仍需後續工作的明確邊界：

- Android 的流水、Micro Voxel、Physics Grid、世界管理、共用核心 ImGui 與五條非 DXR Renderer V1 已接入；尚未具備 Windows-only Hardware DXR、BepInEx Mod runtime、全部桌面進階設定與 Compute 的完整透明／精密幾何一致性。
- macOS 只有跨編譯與 fallback 程式碼，尚無 Apple 實機啟動證據。
- Windows／Linux ARM64 的 GPU 與 Mod hook 尚未完整實機驗證。
- DXR 只有 Windows D3D12；其他平台不會以 Compute PT 冒充 Hardware RT。
- Infinite Mirror 的 30–1000 層是重投影／解析近似，不是真實 1000 bounce DXR。
- 世界雖採無界 Chunk 座標，遠距離 Chunk unload、背景 save、mesh streaming、LOD 與完整記憶體預算仍需持續完成。
- 靜態 DXR 世界仍有 monolithic BLAS rebuild 路徑；最終 per-Chunk BLAS cache／compaction／refit 架構未完成。
- 水是有限體積格點模擬，不是完整連續流體；跨未載入 Chunk 的全域流動未完成。
- Deferred／Forward 尚未包含全部 CSM、Shadow Atlas、Probe、SSGI、Clustered Lighting、OIT 與 Volumetric Fog。
- Path Tracing 尚無完整 Denoiser、ReSTIR、MIS／NEE、Wavefront、Temporal Reprojection。
- 高壓串流與最高 Renderer 設定仍可能出現低於目標 FPS 的場景；請以 benchmark 報告而不是平均 FPS 宣稱完成。
- Runtime Mod hot reload 尚未提供；ARM64 RuntimeDetour 相容性有限。

### ROADMAP 上目前未完成項目（2026-08-08 摘錄）

以下清單按優先主題彙整，避免你再手動逐頁查找。

#### 核心未完成

- 完整安全模式（`--safe-mode`）：同時停用 Mod、忽略 UI layout、禁止自動載入世界還未成形。
- Mod SDK 與通用工具鏈：多來源 `.csproj` 編譯、NuGet 版本解析、沙箱與簽章流程仍未完成。
- 解析球體/物理進一步補完：目前有可推動球體、解析碰撞與反射，但尚未納入完整通用模型生命週期（模型、音訊、動畫）。
- 警示機制與錯誤回報：仍缺少完整的熱更新回退路徑與「運行中 Shader Reload」入口。

#### 世界與內容

- 0.12 世界呈現：串流、背景生成／保存／meshing、texture 分頁與大型世界座標仍未完成。
- 0.12 世界管理：世界複製、匯出、刪除前自動備份或資源回收筒尚未完成。
- 0.12 編輯體驗：選取框線框、筆刷預覽 ghost、transform gizmo 尚未完成；Showcase 尚未拆成 4 種獨立模板；天氣 preset（雨、雪、霧、濕表面、雷電）未完成；快速截圖/高樣本離線輸出未完成。
- 0.13 物理進階：二次斷裂、動態重建預算、爆炸衝擊與粒子音效尚待實作。
- 1.0 平台正式版：世界／內容／Mod 遷移器、世界分享與教學系統仍是長期路線。

#### Rendering 與效能（按當前 Roadmap 狀態）

- 未完成/待補（明確標為 `❌` 或 `部分完成`）：
  - Occlusion Culling、GPU Instancing、Indirect Draw、Bindless Texture、Persistent Mapping、State Sorting
  - Light Volume、Tiled/Clustered Deferred、Shader 特定 LUT、Specialization Constant、BRDF/其他相關 LUT、Branch Reduction
  - 每 Chunk / 每 Grid BLAS cache、每 Chunk Instance TLAS、Ray Cone、Ray Compaction
  - Adaptive Sampling、Blue Noise、ReSTIR、Wavefront PT、Ray Sorting
  - Texture/Mesh Streaming、Voxel Clipmap、Chunk LOD、Distance Mesh
  - Async Lighting、ImGui Dirty Update、Pause GPU（暫停仍未縮減背景 3D 負載）
  - DXR 對每 Chunk 的可見性/遮蔽決策仍在單體可行流程階段，仍需完整 multi-instance transform-only 更新與 ray-aware instance culling。
- 15 類惡意攻擊壓力測試仍不完整：已補 10k 左右範圍內的主要類別，但 10k dynamic bodies、CCD bullet wall、多存檔並行、長時間混合災難等仍待加入。

#### 平台與編譯

- macOS：目前仍缺 Apple 實機驗證流程（發行與簽章可用性、OpenGL context、權限）。
- Windows/Linux/Android ARM64：雖有跨編譯與部分測試，但實機 GPU + Mod hook 路線仍非全驗證。
- Android：六條非 DXR 渲染路線已接入，但缺少完整桌面 Mod runtime 對等與高負載真機壓測；實機觸控還需長時間一致性驗證。

#### 重要補充

- ROADMAP 的未完成項目不等同「不能運作」，而是「尚未達到可驗收的完整目標」。
- 本專案原則仍然是：不把建置成功、假設定或已知 placeholder 當成完成項；每個真正完成項目都應對應實際測試證據。

## 相關文件

| 文件 | 內容 |
|---|---|
| [ROADMAP.md](ROADMAP.md) | 所有功能的完成／部分完成／未完成狀態與實際驗證證據 |
| [ROADMAP_OPTIMIZATION.md](ROADMAP_OPTIMIZATION.md) | CPU、GPU、Chunk、Raster、Deferred、Compute PT、DXR 與記憶體優化 |
| [GAME_DESIGN.md](GAME_DESIGN.md) | 遊戲定位、核心循環、創造模式與功能邊界 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 核心資料流與系統責任；若與新 Roadmap 衝突，以目前程式碼／Roadmap 為準 |
| [MODDING.md](MODDING.md) | Mod 安裝、API、編譯、依賴、保存與安全說明 |
| [RTXSandbox.Android/README.md](RTXSandbox.Android/README.md) | Android SDK、APK、Emulator 與目前限制 |

---

RTX Sandbox 的目標不是讓每一條 Renderer 看起來一模一樣，而是讓它們在同一份真實世界資料上各自採用正確的圖學方法，並把已完成、近似、平台限制與尚未完成的工作清楚分開。
