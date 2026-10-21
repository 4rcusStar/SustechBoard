# SustechBoard

南科大 Blackboard 课程公告聚合器。常驻托盘，自动登录 CAS/Blackboard 抓取公告，按截止日期排优先级，桌面一个小面板就能速览和查看详情。

## 界面

| 速览面板 | 详情页 | 设置 |
|---|---|---|
| `docs/panel.png` | `docs/detail.png` | `docs/settings.png` |

## 功能

- **自动认证**：CAS 表单登录 → `service ticket` → Blackboard 会话；会话失效自动重登
- **全量抓取**：读取"我的课程"模块得到全部课程，逐课抓公告（当前 10 门课 / 51 条）
- **去重比对**：SQLite + 内容哈希，只有新增或内容变化的条目才算"更新"
- **截止日期抽取**：中英双语（`2026年10月25日 23:59`、`Nov 2`、`Sunday, October 25, 23:59`），
  **年份锚定公告发布日**，避免把上学期旧公告重定到未来
- **五档优先级**：P0（≤3 天）／P1（≤14 天）／P2／P3／归档，排序逻辑在 C# 里，可复现可调试
- **双档归档**：手动归档（可还原）与过期归档（不可还原）分开，按钮就在每张卡片左下角
- **AI 标签**：卡片右上角显示模型判定的类型（考试 / quiz / 作业 / 通知 / 成绩 / 资料）
- **日程表**：月历 + 当日事件清单，色点按事件类型着色，自动定位到最近的事件日
- **两级内容发现**：先扫课程菜单找出「作业区」等关键路径并落库，之后每次同步只抓这些路径
- **显示模式**：仅 focus 时置顶 / 随时在最下层，设置里切换并实时生效
- **可缩放 + 自适应**：拖拽任意边框调整大小（320×300 ～ 1200×1600），标题与摘要行数随宽度变化
- **可选语义分析**：接任意 OpenAI 兼容接口（默认 DeepSeek），抽取硬截止、限定条件、一句话摘要；
  未配置 Key 时自动降级为纯规则分析，不影响使用
- **通知**：托盘气泡 + 可选企业微信/webhook 推送
- **定时刷新**：默认 3 小时一次，仅 8:00–23:00
- **开机自启**：注册表 Run 项，带 `--tray` 启动即最小化
- **凭据安全**：密码与 API Key 用 Windows DPAPI（当前用户）加密，非明文落盘

## 运行环境

- Windows 10 / 11 x64
- .NET 9 Desktop Runtime

## 构建与发布

```powershell
dotnet build SustechBoard.sln -c Release
```

单文件发布（依赖已安装的运行时，约 3.4 MB）：

```powershell
dotnet publish src\SustechBoard.App -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

免安装运行时的自包含发布（约 150 MB）：

```powershell
dotnet publish src\SustechBoard.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish-selfcontained
```

> 注意：`EnableCompressionInSingleFile` 只在 `--self-contained true` 时可用。

## 首次使用

1. 运行 `publish\SustechBoard.exe`，托盘出现蓝色 **S** 图标，面板在屏幕右下角弹出
2. 首次没有凭据时会自动打开设置：填学号（SID）+ 密码 → 点「测试登录」确认 → 保存
3. 自动抓取，约 20 秒完成

**密码请原样输入**，不要手工 Trim：本项目实测过密码末尾的句点属于密码本身，
早期版本 `Trim()` 导致登录被 CAS 拒绝（`401 认证信息无效`）。设置里存的就是你输入的原文。

## 配置大模型（可选）

设置 → 大模型智能分析：

1. 勾选「启用语义分析」
2. 接口地址：`https://api.deepseek.com/v1`
3. 模型名：`deepseek-flash` 或 `deepseek-chat`
4. API Key：填入后点 **「测试模型」**

测试按钮不是简单 ping 接口，而是**用一个合成公告走完整分析链路**（同样的 endpoint、
同样的 `response_format`、同样的解析器），所以测通即代表实际调用可用，并会顺带验证抽取质量：

```
✓ 工作正常（1555 ms）
分类 assignment · 截止提取 ✓ 11-02 23:59 · 硬截止
```

启用后，**全部公告都会经过模型**（含归档条目，因为归档条目的考试日期仍要上日历），
输出除原有字段外还包含：

- `label`：考试 / quiz / 作业 / 通知 / 成绩 / 资料 —— 卡片右上角的那个标签
- `events[]`：`{kind, start, end, all_day, location}`，供日程表使用

内容哈希缓存决定这是**一次性开销**：一条公告只分析一次，除非正文被老师改动。
50 条规模约 ¥0.1。

## 操作

| 操作 | 方式 |
|---|---|
| 显示/隐藏面板 | 托盘图标单击，或面板右上角 `✕` |
| 立即刷新 | 面板 `⟳` 或托盘右键「立即刷新」 |
| 查看详情 | 点击卡片任意位置；`Esc` 或「← 返回列表」回退 |
| 手动归档 | 卡片左下角「归档」；到「手动归档」页点「↩ 还原」恢复 |
| 调整大小 | 拖拽窗口任意边框或角落，尺寸会自动记住 |
| 打开原始公告 | 详情页「在 Blackboard 打开」 |
| 退出程序 | 托盘右键「退出」 |

## 归档规则

面板四个标签页：**待办 / 全部 / 手动归档 / 过期归档**。

| 归档类型 | 判定条件 | 可还原 |
|---|---|---|
| 手动归档 | 你点了卡片左下角「归档」，且内容尚未过期 | ✅ 「手动归档」页点「↩ 还原」 |
| 过期归档 | 截止已过 7 天以上 / 发布时间超过 60 天 / 事件已结束 / 判定为示例或无关内容 | ❌ 按钮自动隐藏 |

**过期优先于手动**：一条内容一旦过期，即使你之前手动归档过，也会归入「过期归档」——
因为还原一条已经失效的内容没有意义。

手动归档标记持久化在 SQLite 的 `manual_archived` 列，刷新、重启都保留，
并且**不会被内容更新覆盖**（`UPDATE` 语句刻意不触碰该列）。
旧版本数据库在启动时自动迁移，日志会记一行 `数据库迁移: announcements 新增列 manual_archived`。

## 显示模式与尺寸

设置 → 显示：

| 模式 | 行为 |
|---|---|
| 仅 focus 的时候在最上 | 点它才浮到最前，切走后台后回归普通层级 |
| 随时在最下层 | 永不置顶，失焦后立即沉到所有窗口下方；从托盘调用时仍会临时浮起以便操作 |

面板可**拖拽任意边框或角落**缩放，范围 320×300 ～ 1200×1600（DIP），
尺寸会和位置一起持久化到 `settings.json` 的 `panelWidth` / `panelHeight`。

界面按窗口宽度自适应：

| 窗口宽度 | 标题行数 | 摘要行数 |
|---|---|---|
| < 360 | 2 | 1 |
| 360 – 560 | 2 | 2 |
| ≥ 560 | 3 | 4 |

实现是 `LinesByWidthConverter` 把 `Window.ActualWidth` 映射到 `TextBlock.MaxHeight`，
纯声明式绑定，后台没有任何尺寸判断分支。

## 日程表

与「待办」同级的一个标签页，把公告里的事件摊到日历上。

| 元素 | 说明 |
|---|---|
| 月历网格 | 每天最多 3 个色点，按**事件类型**着色 |
| 色点含义 | 红=考试、紫=quiz、蓝=作业/DDL、灰=其他 |
| 选中日 | 蓝色实心；今天为描边 |
| 下方清单 | 当日事件的时间、类型、课程名、标题 |
| 自动定位 | 首次打开跳到**最近的一个有事件的日期**，避免停在无事件的"今天" |

事件来源是模型抽取的 `events[]`，一条公告可产出多个事件——例如「离散数学」那条同时抽出
作业截止与随堂期中，就产生 2 个日历条目。规则层作为降级路径，只能生成 1 个基于 deadline 的事件。

> 注意：日历只包含**从 Blackboard 公告中能读到的时间**。很多考试只发在教务系统或课程群，
> 这里不会有。请勿把它当成完整考试表。

## 内容区发现（两级抓取）

作业经常不在「公告」里，而是藏在某个一级栏下面——比如「学习评价 → 我的作业」、
「课程内容 → 课后作业Homework」。只抓公告会整片漏掉。所以抓取分成两级：

| 级别 | 触发时机 | 做什么 | 成本 |
|---|---|---|---|
| **发现** | 新增课程 / 换账号 / 每 7 天 / 手动 | 扫课程菜单 → 模型挑出像作业区的内容区 → 实际探测一次 → 路径存入 `course_paths` | 一次性，约 10 次请求 |
| **执行** | 每次同步 | 公告 + 已选中的内容区 | 只按存的路径抓 |

**选哪个区交给模型 —— 但判据是「条目内容」，不是「区名」**：

早期版本靠区名关键词判断（作业/Assignment/评价），实测漏得很惨。真实反例：一门课的
`Course Materials / Chapter 2` 里装着 `HW1 Force Vectors / Due by Monday, 14 September 2026`，
区名和图标都不像作业，于是整片被跳过。

现在的做法是**先探测、再让模型读内容**：

1. 探测该课**全部**内容区（不是靠名字预筛）
2. 把每个区的「条目标题 + 类型 + 说明前 150 字 + 附件名」拼成一份 digest
3. **一门课一次调用**，模型回答 `{"keep":[0,3]}` —— 只给编号，永远不能编造 URL
4. 代码按编号回查已知 URL，落库

模型无权删除数据，只能表达取舍；探测拿到的条目构成事实依据。无 API Key 时退化为本地
关键字判断（`kind` 含 作业/Assignment/Test/Exam，或正文含 due/截止），准确率较低但可用。

**两道数据保全闸门**（都是实测踩出来的）：

- 课程页抓取失败（返回 null）≠ 这门课没有内容区 → 保留原有路径，绝不覆盖
- 一次抓取只允许清理**它自己那门课**未返回的旧条目 → 课程列表偶尔返回不全时，
  缺失课程的数据不会被连带删除

**菜单结构变更免费发现**：公告页本身就带课程菜单，每次抓公告时顺手算 hash 与库里的比对，
不额外花请求;哈希变了就在当次同步内重新发现该课。

实测 9 门课：模型选中 `学习评价 / 我的作业`、`Course Materials / -- Assignments`、
`课程内容Content / 课后作业Homework`、`Assignments / *`，正确跳过 `Chapter 2`、`Week 1`、
`课件Course Slides` 等资料区，共带出 **40 条原本完全不可见的作业条目**。

> 内容区条目没有"发布时间"，因此它们的过期判定只依据截止日期，不参与 60 天陈旧规则。

## 数据位置

`%LOCALAPPDATA%\SustechBoard\`

| 文件 | 内容 |
|---|---|
| `settings.json` | 非敏感设置（账号名、间隔、开关、面板位置） |
| `secrets.dat` | DPAPI 加密的密码与 LLM API Key |
| `board.db` | SQLite：公告正文 + 分析缓存（含内容哈希） |
| `app.log` | 运行日志，超过 2 MB 自动轮转 |

删掉整个目录即可完全重置。

## 架构

```
SustechBoard.Core            (net9.0-windows, classlib)
├─ BlackboardClient          CAS 登录 / 会话失效重登 / 礼貌性限速
├─ BlackboardFetcher         AngleSharp 解析课程与公告（含 CDATA 拆包）
├─ DateParsing               中英文发布时间与截止日期抽取
├─ BoardDatabase             SQLite 存储与哈希去重
├─ Analysis.cs               RuleAnalyzer（规则层）+ Prioritizer（分档）
├─ LlmAnalyzer               OpenAI 兼容语义分析，失败即降级
├─ SyncService               一次刷新的编排 + 后台富化
├─ Notifier                  webhook（企业微信/通用 JSON）
└─ Settings / Util           DPAPI 凭据、日志、路径

SustechBoard.App             (WPF + WinForms NotifyIcon)
├─ App                       单实例、托盘、定时器、调度
├─ MainWindow                无边框置顶面板：列表 / 详情切换
├─ SettingsWindow            账号、刷新、通知、LLM、自启
├─ StartupManager            HKCU Run 项
└─ IconFactory               运行时生成图标（无二进制资源）

SustechBoard.Probe           (console，开发验证工具)
```

数据流：`CAS 登录 → 抓课程 → 抓公告 → SQLite 去重 → 规则分析 → 优先级 → 面板`
→ 命中 LLM 条件时后台富化并刷新面板。

## 开发验证工具

```powershell
# 抓取并输出 JSON（需凭据文件：第 1 行账号，第 2 行密码）
dotnet run --project src\SustechBoard.Probe -- --cred cred.txt --out out.json

# 只看当前数据库的排序结果，不联网
dotnet run --project src\SustechBoard.Probe -- --dump

# 通过真实代码路径写入设置与 DPAPI 凭据
dotnet run --project src\SustechBoard.Probe -- --seed cred.txt

# LLM 链路自测：本地 mock 一个 OpenAI 兼容端点，校验请求格式与字段映射
dotnet run --project src\SustechBoard.Probe -- --llm-selftest
```

App 的开发期开关（不影响正常使用）：

| 参数 | 作用 |
|---|---|
| `--tray` | 启动即隐藏到托盘（开机自启用） |
| `--dev-detail` | 启动后直接打开第一条的详情页 |
| `--dev-settings` | 启动后直接打开设置 |
| `--dev-startup-on` / `--dev-startup-off` | 只写/删开机自启注册表后退出 |
| `--dev-size 720x600` | 以指定 DIP 尺寸启动面板（验证自适应布局） |

## 资源占用（Release 实测）

| 指标 | 数值 |
|---|---|
| 单文件体积 | 3.3 MB |
| 私有内存（稳态） | ~130 MB |
| 工作集（含共享页） | ~180 MB |
| 首次渲染后 | ~102 MB 私有 |
| 数据目录合计 | < 400 KB |
| 线程 / 句柄 | 27 / ~830 |

面板窗口刻意**关闭了 `AllowsTransparency`**：WPF 的分层窗口会退回软件渲染，
实测私有内存 224 MB → 133 MB（降 40%+），代价是圆角变直角。
设置对话框仍是分层窗口（瞬时使用，不影响常驻占用）。

## 已知限制

- **依赖密码登录**：学校若给 CAS 加二次验证，需改造 `BlackboardClient` 的认证策略
- **校外需 VPN**：`cas.sustech.edu.cn`、`bb.sustech.edu.cn` 解析到 `172.18.x.x` 私网地址，
  不在校园网且未连 VPN 时无法访问
- **附件内容不解析**：公告正文若只是"见附件 PDF/docx"，程序读不到附件里的截止日期
- **相对时间无法换算**：如"大二暑假""两周后"不会转成具体日期，规则层会留空
- **LLM 属于增强**：关闭或失败时回落到规则层，界面标注"规则分析"
- **webhook 仅适配群机器人**：企业微信 markdown 格式；其他端点发通用 JSON

## 验收记录

- 真实站点端到端：登录成功 → 10 门课 → 51 条公告 → 正确分档（P0=2／P1=1／P2=8／P3=5／归档=35）
- **真实 API Key 端到端**：16 条非归档公告全部完成语义富化（`src=llm`），实测收益：
  - 概率论期中考时间由规则层的 `23:59` 纠正为真实的 **`10:00`**（置信度 0.99）
  - 离散数学截止日期被模型低估置信度（0.60）时，与规则层日期一致 → 提升至 0.85，保住 P1
- 截止日期抽取与人工核对一致（10-25、11-02、11-08）
- **归档分档端到端**（UI Automation 真实点击）：
  - 点「归档」→ `P0 2→1`、`手动归档 0→1`，日志 `手动归档: _45790_1`
  - 切「手动归档」点「↩ 还原」→ 全部复原，日志 `还原: _45790_1`
  - 已有数据库在启动时自动完成 `manual_archived` 列迁移
- **显示设置端到端**：设置里选「随时在最下层」→ 保存后 `displayMode=1`，日志即时记录 `显示模式: AlwaysBottom`
- **边框缩放**：`WM_NCHITTEST` 探测右下角 `HTBOTTOMRIGHT`、底边 `HTBOTTOM`、中央 `HTCLIENT`；
  真实鼠标拖拽使窗口从 490×700 变为 533×1111
- **自适应**：720×600 与 340×520 两种尺寸下标题/摘要行数按预期变化，无内容溢出
- **两级内容发现**（真实账号 9 门课）：
  - 82 个内容区全部探测，AI 逐课阅读条目内容后选出 17 个
  - 关键验证：`Course Materials / Chapter 2` 里的 `HW1 Force Vectors` 被正确抓取，
    截止时间从正文 `Due by Monday, 14 September 2026` 抽出（conf 0.95）
  - 总量 51 → **91 条**；`课堂测试1` 标为 quiz、`期中考试` 标为考试
- **AI 标签 + 日程表**（真实 Key 全量重跑 51 条）：
  - 标签正确：CS305 两条 → `作业`，离散/DSAA/概率论 → `考试`，成绩与行政通知 → `成绩`/`通知`
  - 多事件抽取：离散数学那条抽出 `ev=2`（作业截止 + 随堂期中）
  - 日历自动定位到 10-25（两个 P0 截止），11-08 显示红+蓝双色点（考试 + 作业撞车）
- 修复过程中定位到的真实缺陷：
  - 英文课程用 `Posted on:` 而非 `发布时间:`，导致 23/51 条公告发布时间解析为空
  - 年份用"现在"推断，把 2025 年旧公告重定到 2026 年
  - `Trim()` 密码导致登录被 CAS 拒绝（末尾句点属于密码本身）
  - `ListBox` 漏绑 `ItemsSource`，数据正确但列表空白
  - `TabTodo.IsChecked` 在 `InitializeComponent` 期间触发 `Checked`，兄弟控件尚未创建 → 启动即 NRE
  - `SystemParameters.WorkArea` 与窗口 DIP 混算，窗口被推出屏幕
  - `AllowsTransparency=True` 强制软件渲染，内存翻倍
  - LLM 自报置信度可压低明文截止日期（已用"双抽取器一致即提升"修正）
  - 后台富化每轮只处理 12 条，首次需 12 小时才清空（已改为连续多轮直到清空）
  - 归档按钮最初用 `PreviewMouseLeftButtonUp`，导致键盘与 UI Automation 完全无法触发（已改用 `Click`）
  - 跨进程 `SetWindowPos` 会与显式 `Width/Height` 打架，尺寸变得不可控（验证入口改为 WPF 属性）
- LLM 链路 mock 自测通过；开机自启读写成对验证通过
- Release 单文件运行截图验证通过
