# RTX Sandbox

## 0.13 Engine Architecture

- 新增可在遊戲內即時切換的渲染後端：`Compute Path Tracing`、`PBR Forward`、`Classic`。選擇會寫入 `settings.json`。
- `Hardware Ray Tracing` 與 `Deferred Lighting` 會顯示在設定頁但鎖定為「規劃中」；目前沒有用名稱或假選項冒充 DXR／GBuffer 實作。
- `RenderBackendHost` 讓遊戲迴圈與模組依賴渲染介面；切換失敗會保留上一個可用後端。
- PBR／Classic 都使用真正的 voxel mesh、16×16 程序材質 atlas、micro-voxel 幾何與選取框，不是空白模式。
- Mod API 已拆出 `Blocks`、`World`、`Rendering`、`Physics` contracts，並新增 `IRenderBackendService` 與 `IVoxelPhysicsService`。
- 詳細的現況邊界、依賴方向與下一階段拆分原則見 [ARCHITECTURE.md](ARCHITECTURE.md)。

驗證三個圖形模式：

```powershell
dotnet run -c Release -- --gpu-smoke
dotnet run -c Release -- --gpu-smoke-pbr
dotnet run -c Release -- --gpu-smoke-classic
```

## 0.12.1 互動、反射與中文介面

- 修正普通模式左鍵錯誤放置：現在左鍵固定破壞、右鍵固定放置；進階筆刷啟用時左鍵才執行筆刷。
- 所有非金屬表面也使用視角相關的 dielectric Fresnel 反射；金屬仍使用材質色反射，粗糙度控制模糊程度。
- 選取框改為 path-traced emission，會出現在金屬、玻璃與水面的次級反射中，不再只是第一層畫面覆蓋。
- `E`／`I`／`Tab` 開啟獨立「創意方塊庫」，`B` 開啟獨立「建造工具」，不再把物品欄包在設定／Editor 分頁裡。
- 新 UI 使用遊戲化深藍面板、立體材質預覽、繁體中文字型與中文化方塊名稱；搜尋框支援中文輸入。

## 0.12 Precision Building

- 真正的 sparse micro-voxel 幾何，不是視覺縮放：內建 `1/2`、`1/4`、`1/6`、`1/8`、`1/16`、`1/32`、`1/64` 網格。
- 同一大格可混用不同精度；系統用最小公倍數保持精確，例如 `1/6 + 1/16 = 1/48`，不建立巨大三維陣列。
- `V` 切換精密模式；精密模式左鍵移除一個微格，普通模式左鍵會清除整個細分容器，右鍵放置，`Alt + 滾輪` 切換精度。
- 微方塊共用 CPU 射線命中、玩家 AABB 碰撞、六面物理支撐、GPU path tracing 與第 7 版存檔資料。
- Tools 介面可切換模式與選擇網格；一般畫面不顯示編輯狀態，按 F3 才顯示精度與除錯資訊。
- GPU 採二層追蹤：先走 64×32×64 粗網格，只在命中的 cell 測試 sparse micro AABB。

快速驗證：

```powershell
dotnet run -- --self-test
dotnet run -- --gpu-smoke
```

RTX Sandbox 是一個以「建造、破壞、物理實驗、即時光線追蹤展示」為核心的體素沙盒。它不是 Minecraft 生存模式，也沒有飢餓、食物壓力、工具耐久、怪物或科技解鎖線。

執行環境為 .NET 6。剛體模擬使用 BEPUphysics 2；AABB 僅由引擎內部用於寬相篩選，不再由遊戲自行拿來解算旋轉物體接觸。

目前可玩的定位是：

```text
Creative voxel building
+ Teardown-style structural experiments
+ editor-like region tools
+ progressive path-traced presentation
+ BepInEx / force-override modding
```

## 0.11 重點

- 世界與方塊預覽共用真正的程序化 16×16 材質圖集；自然材質有穩定的逐像素色差、粗糙度與高度法線，純色方塊不會被強制加雜訊。
- Creative Library 提供等角方塊縮圖、分類、搜尋、收藏、最近使用，以及可自行配置並持久化的九格快捷欄。
- `E`、`I` 或 `Tab` 開啟材質庫；數字鍵選快捷格、滾輪切換、中鍵吸取到目前快捷格。
- 準星有明暗場景可見的外框與情境顏色，並能在設定調整大小、透明度和開關。
- Natural v2 使用 domain warp、大陸度、侵蝕、山脊、溫濕度、河道與地質分層生成；包含 3D 洞穴與群落式植被，不生成任何生物或人工建築。

## 目前已完成

- OpenGL 4.3 compute-shader 路徑追蹤器，支援 1–12 次反射／折射、累積取樣、動態解析度與曝光。
- 可調時間循環：暫停、快轉、倒轉、日出、正午、日落、午夜。
- 第一人稱、碰撞飛行、noclip 自由相機；水為非實體、可游泳、可直接被其他材質取代。
- 196 個基礎體素定義（194 種可建造材質，另含空氣與不可破壞基岩），以 Nature、Wood、Building、Metal、Technology、Optical、Lighting、Special 分類管理。
- Creative Library 使用等角 3D 方塊預覽，支援分類、名稱／key／tag 搜尋、收藏與最近使用；材質 tooltip 可查看 metallic、roughness、transmission、IOR 與 emission。
- 五種可建立場景：Flat、Natural、Pure Nature、Void、Showcase；世界 preset、biome、seed 與選項會隨存檔保存。
- 草原、森林、竹林、沙漠、雪地、山地、沼澤及 Auto 生態；Natural 不產生人工建築，Pure Nature 另有宏觀生態過渡、河帶與洞穴。
- 單格放置、擦除、替換、材質吸取。
- 選取 A/B、區域填充、清除、依目標材質替換。
- 複製／貼上、Y 軸 90 度旋轉、X 軸鏡像。
- 球形增加／擦除筆刷、地形平整、可調半徑爆破。
- 六面結構物理：失去支撐的連通結構會整體脫離成可連續平移、旋轉、碰撞與休眠的局部 Grid；斜角接觸不傳遞支撐。
- 網格歸屬工具：左鍵選目標 Grid，右鍵把目前方塊轉入目標；只接受座標對齊且六面相鄰的完整方塊。
- 摔落衝擊斷裂是獨立實驗選項，預設關閉；基本的失去支撐斷裂永遠屬於世界規則，不再是舊式落沙開關。
- 世界 Chunk 會註冊為 BEPU 靜態碰撞體，斷裂 Grid 會轉成動態 compound body；BEPU 負責接觸流形、摩擦、零回彈、CCD、旋轉、堆疊和休眠。F3 可查看實際碰撞步數。
- 第 5 版 16-bit 世界存檔、備份恢復、自動存檔與方塊 key 重映射；第 1–4 版 byte 世界可自動遷移。
- BepInEx 6 CoreCLR、HarmonyX、設定介面與依賴檢查已內置。
- Mod API 可註冊／強制覆蓋方塊、物品、配方與建造工具。

舊版生存資料結構仍保留在組件與存檔格式中，僅用於二進位相容與既有 Mod 過渡；本體不載入生存配方、不更新生存數值，也不顯示生存 HUD。

## 操作

| 輸入 | 功能 |
|---|---|
| WASD | 移動 |
| 滑鼠 | 視角 |
| Space / Ctrl | 跳躍或飛行上升／下降；雙擊 Space 切換碰撞飛行 |
| Shift | 加速移動／飛行 |
| 左鍵 | 使用目前編輯工具 |
| 右鍵 | 快速放置目前材質 |
| 中鍵 | 吸取目標方塊材質 |
| 1–9 | 選擇可配置快捷欄的九格 |
| Q / 滾輪 | 循環快捷欄 |
| E / Tab / I | 開啟 Creative Library／編輯器 |
| N | 切換自由鏡頭／noclip；也可在設定中控制 |
| Ctrl+C / Ctrl+V | 複製選取／在目標旁貼上 |
| Ctrl+F / Ctrl+Delete | 填充／清除選取區域 |
| X / M | 旋轉／鏡像剪貼簿 |
| [ / ] | 筆刷半徑 -/+ |
| T | 暫停時間循環 |
| , / . | 時間 -/+ 1 小時 |
| - / = | 時間速度減半／加倍 |
| R | 設為正午 |
| F1 / F2 / F3 | VSync／反射品質／顯示除錯資訊 |
| F5 | 儲存世界 |
| Esc | 設定／返回 |

## 編譯與 Visual Studio

需求：Windows、64 位元 .NET 6 SDK、OpenGL 4.3 以上 GPU。NVIDIA RTX 顯示卡適合較高反射次數，但目前後端不綁 NVIDIA SDK。

直接用 Visual Studio 2022 開啟 `RTXSandbox.sln`，將 `RTXSandbox` 設為啟動專案並選擇 x64／Release 即可。Solution 目前只包含本體與 Mod API；不附帶或預載任何示範 Mod。

命令列：

```powershell
dotnet build RTXSandbox.sln -c Release
dotnet run -c Release
dotnet run -c Release --no-build -- --self-test
dotnet run -c Release --no-build -- --mod-diagnostics
```

第一次建置會從 NuGet 還原 OpenTK、ImGui.NET、BepInEx 與 BEPUphysics。遊戲輸出位於 `bin/Release/net6.0/`。

## 編輯器結構

```text
SandboxGame                    -> 輸入、編輯器 UI、時間與各系統協調
Gameplay/BuildToolSystem       -> 區域、筆刷、剪貼簿與爆破
Gameplay/VoxelPhysicsSimulator -> 六面支撐分析、局部 Grid 剛體與可選衝擊斷裂
Gameplay/PlayerController      -> 第一人稱、碰撞飛行、noclip、水中移動
World/VoxelWorld               -> 體素資料、生成、碰撞查詢與修改事件
World/BlockRegistry            -> 4,096 槽 16-bit 材質表與 last-writer-wins 覆蓋
Rendering/RayTracingRenderer   -> compute path tracer 與 GPU 資源
Persistence/WorldSaveService   -> 版本化 level/chunk/player 存檔
RTXSandbox.ModAPI              -> 穩定的公開 Mod 合約
Modding/ModHost                -> BepInEx 與服務／事件橋接
```

## 內容與 Mod

- 核心方塊：`Content/Base/base.blocks.json`
- 分類材質庫：`Content/Base/blocks/natural.blocks.json`、`wood.blocks.json`、`building.blocks.json`、`metal-tech.blocks.json`、`optical-emissive.blocks.json`
- 可覆蓋內容包：`ContentPacks/<pack>/pack.json` 與 `blocks/*.json`
- BepInEx 插件：`bin/Release/net6.0/BepInEx/plugins/`
- `BepInEx/plugins` 預設為空；所有內建方塊都屬於本體 Content，不會出現在 Mod 清單。

詳細接口見 [MODDING.md](MODDING.md)，產品界線與後續階段見 [GAME_DESIGN.md](GAME_DESIGN.md) 和 [ROADMAP.md](ROADMAP.md)。

## 有意保留到後續階段

目前世界仍是固定 64×32×64 的可驗證原型，因此 Flat 高度會誠實限制在目前容量；真正的無限 Chunk、半格／四分之一格 micro-voxel、剛體碎片、天氣體積效果、鏡頭路徑與 DXR/Vulkan 後端需要更深的資料或渲染架構，不會用假 UI 冒充完成。這些項目已按依賴順序列入 Roadmap。
