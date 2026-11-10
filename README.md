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
- 平整、爆炸、選區、複製與貼上工具；舊球形體素筆刷已移除。
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

平面鏡尾端歷史在一般轉頭時會沿用上一幀相機資料進行重投影，不再因每個滑鼠視角增量先清空而短暫變黑；視窗重建或真正的大幅相機跳躍仍會安全丟棄不相容歷史。

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

共用程序 Atlas 不再對所有金屬套同一張純色鏡面：鑄鐵、拉絲鋁、不鏽鋼、錘紋鋼、鈦、青銅、黃銅、銀與鍍鉻分別使用 cast／brushed／hammered／polished 微表面與不同粗糙度。鍍鉻仍可接近完美鏡面，其餘金屬會保留各自的細節、反射展寬與底色；各 Renderer 讀取同一份材質資料。

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
- 可以對已有水的格子繼續倒水；每次增加一桶有限水量，超過單格容量的部分會守恆地流向鄰格。
- 小範圍連通管壓力／液面平衡。
- 世界修改、存檔、載入及 Renderer 更新。
- 游泳阻力；跳躍鍵在不同水深仍提供足以離水的跳躍，不用自動爬牆。
- 模拟量仍以格点保存，但可见水面不是每格重复的静态贴图：Raster／Android 使用独立透明 pass、跨方块与 Chunk 连续的 world-space 几何波和细节法线波；Compute PT 使用连续光学法线，Hardware DXR 使用相同相位扰动命中法线而不每帧重建 BLAS。

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
- Creative Library／物品庫：Java 版 Minecraft 風格的淺灰凹凸面板、分類圖示分頁、9×5 完整物品格，以及底部九格快捷欄。滑鼠滾輪逐列切換，拖動捲軸也會吸附整列，不裁切半個物品；分類過多時用左右按鈕翻分頁列。
- 建造工具與精密模式。
- ESC 暫停選單：繼續、六個功能入口、離開世界。
- 設定：全域顯示設定與當前 Renderer 專屬光影設定。
- Mod 管理、檔案右鍵選單與簡易文字編輯器。

設定畫面的完成按鈕會回到上一層暫停選單；Mod 不再混在設定頁。再次按 ESC 會儲存並關閉暫停選單，視窗失去焦點時會自動開啟暫停選單。桌面不会在进入游戏时强制切换英文键盘配置；Windows IME 只在实际文字输入框取得焦点时与游戏窗口关联，普通菜单和游玩状态不会让 IME 抢走 Shift／WASD。中文输入框仍使用原生 composition 与候选窗定位。

創造物品欄是**遊戲本體**的 UI，並非模組管理頁或需要安裝的外掛。頂部分頁依序為全部、自然、木材、建築、玻璃、金屬、光學、照明、科技、工具等內容分類，收藏固定在最後；背包改為獨立側邊標籤，搜尋框則始終保留，不再偽裝成內容分類。所有玻璃由既有 key／`glass` tag 集中到「玻璃」分頁；能量投射器等可用工具則集中到「工具」，不改變方塊 ID、材質或存檔。搜尋可匹配名稱、namespaced key 與標籤，物品庫右鍵切換收藏。新增內容的 Category 會自動產生分頁。`CreativeInventoryPanel` 和 `CreativeInventoryModel` 由桌面及 Android 直接共用，三種語言的分頁名稱也共用同一張表。

侧边「背包」提供 **9×5＝45 格**，格子尺寸与物品库完全相同，另外保留九格快捷栏且没有装备栏。创造模式中的格子保存的是一个材质引用，不再显示虚假的 64 数量；旧版 81 格／count 设定会安全压缩并迁移为最多 45 个材质。背包与快捷栏按 namespaced key 保存，切换世界仍保留这套创造工具配置，世界建造继续无限取用。

| 操作 | 效果 |
|---|---|
| 點物品库／Shift＋點物品库 | 游标拿起一个材质引用；Shift 不再制造 64 的假数量 |
| 點背包／快捷栏格子 | 拿起、放回或与不同材质交换；放回同类格会清空游标 |
| Shift＋點擊／Shift＋左鍵拖曳 | 背包與快捷欄之间快速搬运到空格 |
| 右鍵點物品库 | 加入或移除收藏 |
| 背包右下角垃圾桶 | 點擊銷毀游標材質；Shift＋點擊會清空游標、45 格背包及九格快捷欄 |

物品庫滾輪仍是整列捲動，不顯示半個物品。拿著物品時可以切分類再放入背包；ESC／叉叉關閉會取消**尚未完成的拿取與交換**，回復到這次拿起之前，不把游標物品弄丟。完成放入後立即成為保留的配置，關閉背包時保存。任何 UI 可见时都会释放并显示鼠标，暂停菜单与背包之间切换也不会重新把鼠标藏起来。操作参考 [Mouse Tweaks](https://github.com/YaLTeR/MouseTweaks)，是原生游戏实现，不是载入 Minecraft 模组或完整移植其所有设定。

針對此介面可執行 `dotnet run --project RTXSandbox.csproj -c Release -- --inventory-self-test`；测试包括真实 ImGui 点击／拖曳、分页、侧边背包、独立垃圾桶、45 格与九格快捷栏、三种视窗大小、交易回滚、持久化、鼠标捕获与 IME ownership。

Android 已有觸控虛擬按鍵、八方向 D-pad／搖桿配置、跳躍、蹲下、左右鍵、長按畫面破壞、快捷列、物品庫，以及可在遊戲內重新綁定並保存的外接鍵盤輸入；觸控按鈕可選完整標準 104 鍵名稱。桌面與 Android 直接共用 `SharedImGuiScreens` 的暫停、成就與語言畫面，語言清單以 `繁體中文／简体中文／English` 自稱顯示；Android 創造物品欄直接共用桌面的 `CreativeInventoryPanel` 分頁、完整格子、整列捲動與底部快捷欄；建造工具則仍由原有工具畫面提供。Android 設定恢復 PC 的「畫面／時間與光照／控制與介面」核心頁籤，另加「觸控」；顯示距離、FOV、日夜循環、日長、靈敏度、建造距離、飛行／穿牆、物理開關與保存均接入實際狀態。Android 也已接入世界管理與可實際執行 `BasePlugin` 的 Mod runtime V1；完整桌面 BepInEx 等價能力與所有 Renderer 進階細項仍待接入，詳見「[Android](#android)」。

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

桌面 Host 使用 BepInEx 6 CoreCLR `6.0.0-be.785` 與 HarmonyX。框架程序集位於標準的 `BepInEx/core`，遊戲自己的 managed runtime 位於 `Managed`。根目錄啟動器會先執行 BepInEx 官方 `AssemblyPatcher`，再載入遊戲 DLL、啟動 Chainloader；一般插件放在 `plugins`，BepInEx 6 啟動前補丁放在 `patchers`。這條 CoreCLR 啟動流程不依賴 Unity Doorstop／`winhttp.dll`。補丁在記憶體套用，發生錯誤會留下 log 並停止啟動，可用 `RTX_SANDBOX_DISABLE_BEPINEX=1` 救援；詳細限制及測試指令見 [MODDING.md](MODDING.md#desktop-startup-patchers)。Android 仍使用原有插件 adapter，尚未加入桌面啟動前補丁。插件放置方式：

```text
BepInEx/plugins/
└─ MyMod/
   ├─ MyMod.dll
   └─ 其他相依 DLL
```

本專案不再提供獨立 `RTXSandbox.ModAPI`。模組框架的定位是直接劫持／擴充遊戲：插件引用目前的 `Managed/RTXSandbox.dll`，透過公開成員、Reflection 或 Harmony patch 操作本體。這不提供跨版本二進位相容承諾，但也不以一層受限 API 阻擋模組能力。遊戲預設使用 Visual Studio 2022 可載入的 `net9.0`；可選擇性建立 `net10.0` Host，但 Runtime 升級不代表第三方依賴一定相容。

遊戲內 Mod 頁面可對文字／C# 檔案開啟右鍵選單，進行編輯、保存、移動、重新命名、刪除與手動編譯。編譯永遠保留原始碼；刪除走回收筒／可恢復流程。第一版編譯器會引用遊戲 `Managed`、Mod 目錄及依賴 DLL，但目前沒有安全可靠的 Runtime Hot Reload，完成編譯後仍建議重新啟動遊戲。

#### 本體軟體物理、能量工具與機械結構

透明凝膠、彈性橡膠與柔軟泡棉，以及持續運行的質點／彈簧求解器，現在屬於遊戲本體，不必安裝 Mod。含軟材質的面相連結構會以整個 `PhysicsGrid` 為單位變形；普通硬方塊可作為同一結構的剛性負載，貼著世界的接觸面固定，最後支撐被破壞後則解除固定並受重力影響。每格仍可單獨破壞，硬方塊不會被揉成軟體。

創造物品欄的物理分類包含：

- **透明凝膠**：柔軟、帶透射，形變後緩慢恢復。
- **彈性橡膠**：剛度和回彈最高。
- **柔軟泡棉**：形變最大、阻尼最高。

- **能量投射器**：左鍵發射可見的能量脈衝；右鍵打開專屬設定，可獨立選擇低／中／高力度及 0.15～4 格受力半徑。脈衝可推動動態 Physics Grid 與解析球。普通中等脈衝已足以使非夾層玻璃碎裂；世界玻璃與 Physics Grid 內的玻璃都會移除被命中的原格，從實際命中點向四邊產生固定上限八片、帶隨機邊界的三角棱柱碎片。每片都有獨立重力、線速度與角速度；視覺使用明確角點，碰撞使用片內緊縮代理，不再把一格玻璃展開成 `8×8×8` 粉末，也不再用幾個長方體假裝不規則碎片。已經是 Micro Voxel 的玻璃只會把既有元素分組，不會再次把每個元素乘上 64。Physics Save V3／單檔世界 V9 會保存碎片表皮，重開後不會退回碰撞小方塊。
- **動力與傳動元件**：高扭力／高速／重載馬達、減速／增速齒輪箱、傳動軸、飛輪、煞車、大小直齒輪、傘齒輪、直線齒條、離合器、萬向接頭、鏈輪與皮帶輪，以及直線伺服缸、升降曲柄、旋轉主軸、曲柄搖臂、輸送驅動輪與凸輪推板。致動器必須透過面相連的機械元件連到動力源，且機架必須實際接觸可碰撞的靜態世界支撐才會運轉。馬達、齒輪箱與致動器外殼保持固定，只有輸出接觸區向獨立剛體負載施加有界力／扭矩；拆掉最後支撐後整組機械會退出固定狀態並受普通剛體重力影響，不再沿腳本錨點漂浮、擺動或旋轉。世界渲染與桌面／Android 物品欄共用機械輪廓，不再以一排換色方塊冒充不同元件。
- **水面互動**：移動中的物理結構或解析球進入水體時，會把有限流體量重新分配成小幅局部波紋；不生成誇張水花，也不憑空增加或刪除水量。

材質現在有獨立的實際物理密度。`BlockDefinition.Density` 的單位是每個完整一立方公尺方塊的公噸數（同時等價於 t/m³）；Physics Grid 的質量、質心與慣量會使用體積×密度。金屬度與粗糙度只影響渲染，不再冒充重量。鐵塊預設為 7.87 t/格，方塊資訊 HUD 會在金屬度／粗糙度同一行顯示質量。

核心求解器以固定 120 Hz 小步進計算重力、阻尼、材質剛度、形狀恢復與世界碰撞；每格使用八個明確角點與面／體對角約束，最多同時接管 32 個、每個 512 格的結構。可見表皮與玩家碰撞仍維持封閉、平滑且可行走的方塊外殼，不以球串或 Capsule 冒充軟方塊。表面 UV 綁定未變形時的格點，再隨目前角點插值，因此材質會跟著彎曲、剪切與旋轉，而不是在每格重新貼一張未變形方圖。變形後的凝膠表面也使用實際角點三角面做射線命中，因此可從彎曲後的側面繼續放置或逐格破壞，不再只命中未變形的舊 AABB。

硬方塊不套用軟表皮。每一組面相連的金屬／普通硬 Cell 會建立獨立 shape-matching 剛性群：它能被凝膠帶著平移、旋轉並把重量傳回軟梁，但群內距離與方塊排列保持剛性，不會被 spring solver 揉成一坨。Physics Grid 的整體方向會跟隨主要剛性群，因此末端鐵塊和與它銜接的軟梁會使用同一個旋轉基準，不再出現鐵塊保持水平、梁卻獨自彎曲的錯位。硬 Cell 的可見／碰撞盒沿用來源方塊尺寸，不再由旋轉後的 solver 質點 AABB 膨脹；標準鐵塊始終是一格方塊。自動 smoke 同時量測剛性群最大形狀誤差，超過 0.04 格或硬方塊尺寸偏離一格即判定失敗。

`Examples/SoftBodyPhysicsMod` 已改為 **Impact Plasticity V2** 範例：它不再擁有或重複運行本體的連續軟體求解器，而是直接用 Harmony patch 能量命中的內部流程。土壤／凝膠保留體素材質反應；金屬不再刪除 `8×8×8` 小格來製造像素坑，而會把受擊面轉成共享角點的連續細分曲面，以超橢圓壓痕、方向性加工硬化折面及平滑法線形成永久凹陷。凹面使用簡化碰撞但仍可選取，並由 Physics Save V3 保存。玻璃碎裂仍在本體，確保沒裝 Mod 也能正確破裂。這個例子刻意不經過獨立 Mod API。

目前限制：不保存每條 spring 的瞬時狀態；運行中的 Micro Voxel 結構組裝、軟體互撞、布料、繩索、撕裂及 GPU solver 尚未完成。塑性 Mod V2 只在能量命中時重算局部形狀，不把全世界變成持續軟體模擬。

### RTX 展示世界

Showcase 預設現在是一座共用展示園區：水與折射實驗室、特殊玻璃光譜牆、平行鏡廳、具單格針孔及倒立投影的暗箱、PBR 金屬庭院、剛體／解析球場、具實體牆面固定端的軟體梁，以及有馬達、齒輪箱、傳動軸與飛輪供電的機械場都在同一世界。凝膠／橡膠／泡棉梁在存檔中是與牆相連的世界方塊，不是預先懸空建立的 Physics Grid；遊戲啟動後才由軟體求解器在保留牆面固定點的前提下接管。機械場則把固定於基岩的機架與可動負載保存為不同 Physics Grid，避免整台馬達連同負載一起播放懸空動畫。六條 Renderer 看到相同資料。維護用命令可在無視窗模式生成及重載驗證：

```powershell
dotnet run -c Release --no-build -- --install-showcase-world=world1 --showcase-source-root=C:\D\games\RTXSandbox\Saves --showcase-output-root=<staging>
dotnet run -c Release --no-build -- --verify-showcase-world=world1 --showcase-source-root=C:\D\games\RTXSandbox\Saves
```

> Mod 是可執行程式碼，只安裝可信來源。ARM64 上 MonoMod／RuntimeDetour 仍有上游不支援警告，依賴 Runtime patch 的 Mod 不能宣稱完整相容。完整說明見 [MODDING.md](MODDING.md)。

## 全平台支援

### 支援層級的定義

- **實機／VM 驗證**：不只編譯，已在該平台啟動並執行測試。
- **核心驗證**：共享世界、存檔或 self-test 已執行，但沒有該平台 GPU 畫面證據。
- **跨編譯**：只證明能產生目標架構檔案，不等於可玩。
- **進行中**：有正式程式碼，但尚未達到完整一致或尚未完成本輪重新驗證。

### 平台狀態（2026-08-09）

| 平台 | 架構 | 目前證據 | Renderer／限制 | 狀態 |
|---|---|---|---|---|
| Windows 10/11 | x64 | Debug／Release、108/108 self-tests、RTX 5050 DXR smoke、BepInEx／Impact Plasticity V2 實際載入 | 六條 Renderer；DXR 完整主要驗證平台 | **主要支援** |
| Windows | ARM64 | 可產生 self-contained／framework-dependent 發行檔 | 尚無 ARM64 Windows 實機；MonoMod hook 有限制 | **跨編譯** |
| Linux（Ubuntu／WSL） | x64 | 87/87 self-tests；Mesa／Xvfb GL 4.3 下 Classic、PBR、Deferred、Hybrid、Compute smoke；BepInEx `Started: True` | 五條 OpenGL Renderer；無 D3D12 DXR | **實際驗證** |
| Linux | ARM64 | self-contained 發行檔在 QEMU AArch64 通過 87/87 self-tests | 尚無 ARM64 GPU 畫面；Mod hook 有限制 | **核心驗證** |
| macOS | x64／ARM64 | GitHub `macos-14` 已完成 Release build 與 91/91 shared self-tests；兩種 RID publish 成功，已有 OpenGL 4.1／GLSL 410 fallback | 尚無 Apple 實機 GPU 視窗證據；Compute PT 需要 GL 4.3 因而停用；無 DXR | **核心驗證／GPU 待驗證** |
| Android 11+ | x64／x86 | API 30 Emulator 實際覆蓋安裝、舊世界存讀、完整世界管理、流水、Micro Voxel、Physics Grid、觸控／可編輯外接輸入、共用 UI，以及 Classic／PBR／Deferred／Hybrid／Compute；x64 Debug／Release 亦實際執行 Example Mod | Hardware DXR 不可用；Mod runtime V1 可載入 `BasePlugin`，但依賴隔離／Hot Reload 與完整桌面 Renderer 細項尚未接入；本輪雙 VM 下量測不沿用早期 58–60 FPS 作新結論 | **V1 可玩／持續接入** |
| Android 8+ | ARM64／ARMv7 | 兩種 Release APK 都封裝共用世界、流水、Micro Voxel、BEPU Physics Grid、輸入及 16 KB 對齊 ImGui native library | 本輪遵照要求只使用模擬器；ARM64／ARMv7 尚無新的實機執行證據，不能宣稱真機畫面、效能及多點觸控完成 | **建置封裝完成／真機待驗證** |
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

Android 專案不是獨立世界 Demo：它直接 link／重用 `World`、`PlayerController`、`Camera`、`CreativeHotbar`、`ItemRegistry`、`WorldSaveService`、平台服務與遊戲內部 Engine contracts，並使用同一份基礎內容 JSON。APK 支援 x86／x64／ARMv7／ARM64，最低 API 26。

已存在：

- 相同世界生成、Region save、玩家與快捷列資料。
- Classic GLES、PBR Forward GLES、GLES 3 MRT Deferred、Hybrid PBR Raster，以及 GLES 3.1 Compute Path Tracing V1。
- 可編輯虛擬按鍵 Profile、八方向 D-pad 或搖桿。
- 跳躍、Shift 蹲下、Ctrl＋前進／連按兩次前進衝刺、移動、左右鍵、右側畫面長按破壞、V／B、物品庫與快捷列；獨立 LMB／RMB 鍵仍保留。
- 外接鍵盤、滑鼠與 Gamepad 事件映射；外接鍵盤綁定可在 ImGui 頁面捕捉、重設並跨重啟保存。
- 觸控按鈕綁定選單提供標準 104 鍵 PC 鍵盤名稱；按鍵標籤使用 `ESC／SHF／SPC／LMB／RMB` 等原始名稱或短寫。
- App 失去焦點時釋放輸入、開啟暫停，並以合併式背景工作保存目前世界，避免在 Activity 主執行緒等待 Region I/O。
- GLES 上的 Dear ImGui 暫停、物品欄、建造工具、Renderer、繁中／簡中／英文語言與成就列表；語言名稱固定以各自語言顯示，觸控編輯器只由暫停選單開啟。
- Android 主選單可建立、載入、同 Seed 重建、重新命名及刪除世界；離開世界會先保存再返回主選單。
- BepInEx 6 CoreCLR 與 Android managed-plugin adapter；授予一次模組資料夾權限後，玩家可把模組及相依 DLL 直接放入公開的 `Documents/RTXSandbox/Mods/`，重新啟動後由該目錄載入。Mod 頁會顯示實際路徑與已載入插件；系統檔案選擇器僅保留為可選的單 DLL 匯入方式。Release x64 已用直接放置流程實際載入 Example Mod、註冊方塊並接收 `WorldReady`。

尚未完成：

- 將全部桌面 Renderer 進階設定接到 Android GLES Host。
- Hardware DXR 是 Windows D3D12 專用，不在 Android 以其他 API 冒充。Android Compute V1 尚未包含 Micro Voxel／Physics Grid 的解析求交與完整透明折射語意。
- Micro Voxel／Physics Grid 的 V1 顯示、放置、刪除與 BEPU 模擬已接入；仍需真機多點觸控、長時間壓測與所有桌面編輯工具的一致性複驗。
- ARM64 實機效能、記憶體與所有 ABI 的最終驗證。
- Android Mod runtime 尚未達桌面 BepInEx 完整等價：依賴拓撲排序、隔離式 AssemblyLoadContext、Hot Reload 與 RuntimeDetour 類 Mod 仍需逐項驗證。
`libcimgui.so` 的 x64、x86、ARM64 與 ARMv7 版本已以靜態 C++ runtime 重新連結，ELF `LOAD` alignment 均為 `0x4000`，符合 16 KB page-size；32 位版本另固定使用與 ImGui.NET 相同的 pointer-sized `ImTextureID` ABI。四種 Release APK 都能產生，x86 已在 API 30 VM 實際顯示共用 ImGui；ARMv7 目前只有建置／封裝證據。這代表核心 ImGui 已正式接入，但不代表所有桌面頁面都已移植。Android 專用建置說明見 [RTXSandbox.Android/README.md](RTXSandbox.Android/README.md)。

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

模組需直接引用與 Host 相同版本的 `RTXSandbox.dll`；可選 `net10.0` Host 不會產生另一份 Mod API，也不要求重寫遊戲系統。

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

目前 Windows 共享測試總數為 118/118。測試涵蓋世界、Region save、流體、Micro Voxel、剛體／核心軟體物理、軟體存檔拓撲復原、變形表面逐格編輯、能量玻璃碎裂、展示世界、Renderer 設定、平台服務、可編輯輸入、三語導航與 Mod 相關核心行為。其他平台表格中的較低數字是該平台最後一次實際執行的證據，不直接冒充已同步重跑。

### GPU Smoke

```powershell
dotnet run -c Release --no-build -- --gpu-smoke-classic
dotnet run -c Release --no-build -- --gpu-smoke-pbr
dotnet run -c Release --no-build -- --gpu-smoke-deferred
dotnet run -c Release --no-build -- --gpu-smoke-hybrid
dotnet run -c Release --no-build -- --gpu-smoke
dotnet run -c Release --no-build -- --gpu-smoke-hardware
dotnet run -c Release --no-build -- --gpu-smoke-hardware-debris
dotnet run -c Release --no-build -- --gpu-smoke-hardware-soft-body
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
├─ Engine/Contracts/                  編入遊戲的共用內部合約
├─ Modding/                           BepInEx Host、直接 Hook 編譯與診斷
├─ RTXSandbox.Android/                Android Host、GLES、觸控與外接輸入
├─ Infrastructure/                    平台抽象、診斷與壓力測試
├─ UI/                                ImGui controller 與設定畫面
├─ build/                             多 RID 發行腳本
└─ .github/workflows/                 跨平台 CI
```

## 已知限制

以下內容不是「名稱已有所以完成」，而是目前仍需後續工作的明確邊界：

- Android 的流水、Micro Voxel、Physics Grid、世界管理、共用核心 ImGui、五條非 DXR Renderer V1 與可執行 BasePlugin 的 Mod runtime V1 已接入；尚未具備 Windows-only Hardware DXR、桌面 BepInEx 的完整依賴隔離／Hot Reload 對等、全部桌面進階設定與 Compute 的完整透明／精密幾何一致性。
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

### 可繼續開發但尚未完成（2026-08-09 現況）

以下項目已依目前 Repository、[ROADMAP.md](ROADMAP.md) 與 [ROADMAP_OPTIMIZATION.md](ROADMAP_OPTIMIZATION.md) 重新整理。`部分完成` 表示已有正式可運作基礎；`未完成` 表示尚無可驗收實作。清單不把純美化、假選項或已修好的舊問題重新算入。

| 主題 | 狀態 | 已有基礎 | 尚未完成／可繼續做的內容 |
|---|---|---|---|
| 長距離世界串流 | **部分完成** | 無界 X／Z、背景 Chunk generation、背景 Raster meshing、Region 保存與逐幀 commit 已可用 | 遠距 Chunk unload、mesh／AS eviction、Region read cache、Chunk LOD、resident-memory 硬上限與長時間無限探索驗證 |
| 串流與互動效能目標 | **部分完成，未達驗收** | 有固定世界、Burst20、Place100、Break100、Physics 與六條桌面 Renderer benchmark | 仍不能保證加载、放置、挖除及物理始終 120 FPS；需要正式 priority worker pool、自適應 ms／byte budget、精確 `ChunkDirtyReason` 與更完整 GPU timing |
| Chunk 儲存與記憶體 | **未完成** | 16×16 Chunk 與 32×32 Region 已存在 | 16³ sparse Section、Palette 壓縮、ArrayPool／worker arena、texture／mesh streaming 與大型世界記憶體回收 |
| Raster CPU／GPU 提交 | **部分完成** | per-Chunk VBO cache、遮蔽面剔除與背景 mesh 已存在 | Raster Greedy Mesh 的完整桌面路徑、MultiDrawIndirect、GPU instancing、材質批次、persistent mapping、state sorting 與可靠 occlusion culling |
| Hardware DXR 加速結構 | **部分完成** | 靜態世界與動態 Grid 已拆成兩個 BLAS；動態同拓撲可 refit，TLAS 可 update | per-Chunk／per-Grid BLAS cache、每 Chunk／Grid instance、純 transform TLAS update、AS compaction、ray-aware instance culling 與 DXR update budget |
| D3D12／OpenGL 顯示互通 | **未完成** | DXR 結果可同步 readback 後顯示於 OpenGL 視窗 | shared resource 或 double／triple-buffer 非同步傳遞，移除同步 fence／readback／upload 尖峰 |
| Deferred／Forward 完整效果 | **部分完成** | PBR、方向光 shadow、SSAO／SSR、HDR／Bloom、水與 G-buffer debug 已有 V1 | CSM、Point／Spot shadow atlas、Reflection Probe、完整 IBL／SSGI、Tiled／Clustered lighting、OIT、Volumetric Fog 與完整 TAA／motion vector 流程 |
| Compute Path Tracing | **部分完成** | Compute shader trace、累積、反射／基本透明與世界 voxel 求交可用 | Denoiser、Adaptive Sampling、MIS／NEE、Blue Noise、ReSTIR、Wavefront／Ray Sorting、Temporal Reprojection，以及 Android Micro Voxel／Physics Grid 精確求交 |
| Hardware RT 畫質 | **部分完成** | 真 DXR pipeline、BLAS／TLAS、反射／折射、彩色玻璃 transmission、發光方塊與 Infinite Mirror extension 已可執行 | 完整多反彈 GI、粗糙反射多採樣、體積水散射、AO／denoiser／temporal，以及跨材質能量一致性的完整驗證 |
| DLSS／FSR 補幀 | **未完成／受架構阻塞** | 尚未用假開關冒充 | 需先有 D3D12 或 Vulkan presentation、motion vectors、depth、UI-less color 與 pacing；目前 OpenGL 最終呈現不具備正式 Frame Generation 輸入 |
| 有限水體 | **部分完成** | 1～16 單位守恆流水、排水、再次倒水、連通管 V1 與逐格動畫已存在 | 跨未載入 Chunk 的持續流動、大型水域壓測、流速／黏度與真正連續流體模型 |
| Physics Grid 進階 | **部分完成** | 剛體、碰撞、睡眠、推擠、Micro placement、保存與 Raster／DXR 顯示可用 | 碰撞造成二次斷裂、超大型 Grid 分區 shape rebuild、完整爆炸碎片、粒子／聲效與大規模 collider streaming |
| Mod SDK／Runtime | **部分完成** | 桌面編譯入口、公開 Mod API、BepInEx，以及 Android 公開 `Documents/RTXSandbox/Mods/` 的 `BasePlugin` 載入已驗證 | 多來源 `.csproj`、NuGet／相依版本求解、隔離式 `AssemblyLoadContext`、Hot Reload、RuntimeDetour 跨 ABI、安全沙箱與可信簽章 |
| Android 與桌面一致性 | **部分完成** | 共用世界資料、核心 ImGui、五條非 DXR Renderer V1、設定核心頁籤、觸控與外接輸入已接入 | 全部桌面 Renderer 進階設定與效果、Compute 精密幾何／透明語意、所有編輯工具逐項一致性，以及 ARM64 真機多點觸控／手把／長時間高負載驗證 |
| Apple／ARM 平台 | **部分完成或未支援** | macOS hosted runner 已通過 Release build 與 91/91 core tests；桌面八 RID、Android 四 ABI CI 均成功 | macOS Apple GPU 視窗、簽章／權限與發行驗證；Windows／Linux ARM64 GPU 和 Mod hook 實機驗證；iOS／iPadOS Metal Host 仍未開始 |
| UI／開發工具 | **部分完成** | 暫停、設定、世界管理、Mod 管理、成就列表版面與基本錯誤回退已存在 | 完整 `--safe-mode`、運行中 Shader Reload、世界複製／匯出／刪除前備份、筆刷 ghost／transform gizmo、天氣 preset、快速截圖與高樣本離線輸出 |

建議後續優先順序：先處理遠距 unload／記憶體上限與自適應工作預算，再完成 per-Chunk／per-Grid BLAS 和非同步 D3D12／OpenGL 互通；之後才是 Android 真機一致性、Mod 隔離／Hot Reload，以及 Renderer 高階畫質功能。

ROADMAP 的未完成項目不等同「不能運作」，而是尚未達到完整驗收目標。建置成功、預留入口與 Placeholder 都不標記為完成。

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
