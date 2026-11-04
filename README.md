# ADOFAI Steam Workshop Manager

一个适用于 Android `StArray.ModManager` 的 ADOFAI 创意工坊谱面管理器。Steam 网络连接、登录和工坊查询由 [SteamKit](https://github.com/SteamRE/SteamKit) 完成，目标游戏 AppID 为 `977950`。在 ModManager 中打开本 Mod 的设置页，点击“打开谱面管理器”进入独立管理窗口。

## 功能

- Mod 名称为 **Steam 工坊管理器（Steam Workshop Manager）**，工程、程序集、Mod ID 和安装目录统一使用 `SteamWorkshopManager`。
- 加载时自动检查 GitHub 正式版本；设置页和管理器的“账号与设置”页可手动检查、查看版本说明、下载安装更新。发现更新会显示短暂通知，安装后重启游戏生效。
- 设置页保留登录、自动同步配置和队列暂停等简单操作；详细操作集中在独立全屏管理器，关闭窗口后下载继续。
- 界面参考 Replay Mobile 的管理器样式：深灰蓝背景、浅蓝分区标题、蓝色控件、圆角触控按钮、全宽搜索、双行项目列表和详情页；工具栏在窄屏下自动竖排。点击本地、工坊或订阅项目可查看详情并更新、下载或删除整个项目。管理器在状态栏右侧提供“关闭”触控按钮，并向内留出边距以适配圆角屏；标题栏 × 仍可关闭。顶部不设置大号工具栏；本地列表在打开管理器及安装、删除后自动刷新。
- 分页界面分为本地谱面、创意工坊、已订阅、下载管理、账号与设置五个页面；列表每页 20 项。
- 本地谱面可按标题、工坊 ID、目录名和全部 `.adofai` 文件名查找，支持多选删除和检查更新。这里管理的是本 Mod 下载的项目。
- 公开创意工坊支持关键词搜索、服务端分页和浏览最新发布内容，可直接下载搜索结果，无需先订阅。
- 工坊详情可查看封面及附加图片、上传者昵称/SteamID、作者标签与难度、发布/更新时间、大小、订阅/收藏/浏览量、赞踩数量与完整说明。横屏采用图片和信息分列，窄屏上下排列。没有难度标签或元数据时显示“未标注”，不会根据标题推算难度。
- 图片按需在后台加载，保存在 Mod 的 `data/workshop-previews/`，最多缓存 32 张、每张压缩内容不超过 8 MiB。OpenGL 使用纹理显示，Vulkan 等后端使用低分辨率通用预览；加载失败可重试，其它详情和下载操作仍可使用。
- 支持直接在输入框内粘贴 `steamcommunity.com/sharedfiles/filedetails/?id=...`、`steamcommunity.com/workshop/filedetails/?id=...`、`steam://url/CommunityFilePage/...` 链接，自动识别项目 ID；有效链接输入稳定后自动加入下载，也可关闭自动加入并点击按钮。数字项目 ID 使用下载按钮加入。
- 下载管理支持搜索、状态筛选、暂停后续任务、取消排队或正在执行的任务、失败重试和清除已结束记录；同一项目的重复等待任务会合并。
- Steam 用户名和密码登录，支持 Steam Guard 邮件验证码、手机令牌验证码和 Steam 手机确认。
- 查询账号在 ADOFAI 创意工坊中的全部订阅，支持分页。
- 登录后自动同步；默认每 15 分钟检查一次，也可以手动同步。同步将需要更新的项目加入统一下载队列；“刷新订阅列表”只更新列表。
- 可启用“仅下载已勾选的订阅”，逐项选择谱面，也可全选或清空；下载选择会保存到 Mod 设置。
- 可关闭取消订阅后的自动删除，也可在已下载清单中逐项选择并删除本地谱面。
- 通过 SteamKitSDK 下载含 `.adofai` 文件的项目；Steam 没有 `file_url` 时使用工坊 manifest 和 CDN，并保留所有资源文件。
- 一个工坊项目对应一个 `steam_<创意工坊ID>` 一级目录；项目内所有 `.adofai` 文件和资源都放在同一目录，文件名保持工坊原样。结构示例：

  ```text
  /storage/emulated/0/ADOFAI/Levels/steam_2659772424/main.adofai
  /storage/emulated/0/ADOFAI/Levels/steam_2659772424/another.adofai
  /storage/emulated/0/ADOFAI/Levels/steam_2659772424/music.ogg
  ```

  不会拆分项目内的谱面文件，也不会保留源文件树的嵌套目录。
- 若同名 `steam_<创意工坊ID>` 目录已被非 Mod 文件占用，会使用带数字后缀的目录以避免覆盖。
- 取消订阅自动删除默认开启，可在设置中关闭。自动或手动删除都只移除本 Mod 标记的谱面和资源；用户放入目录的其它文件会保留。
- 搜索或链接主动下载的项目会独立保留，不会被“清理已取消订阅”删除；订阅同步下载的项目继续遵循原有清理规则。

Steam 密码和 Steam Guard 验证码不会写入磁盘。自动登录会话中的账号、SteamID、refresh token 和 GuardData **整体加密**后保存在 `mods/SteamWorkshopManager/data/steam-session.json`：

- 使用 Android Keystore 生成不可导出的 AES-256 密钥，密钥按应用和设备隔离，不写入 Mod 目录或安装包，也不从公开的设备 ID 派生。
- 使用 AES-GCM，每次保存由系统产生新的 12 字节随机 IV，并包含 128 位认证标签；文件只包含格式、保护方式、IV 和密文。复制文件到其它设备不能使用其设备密钥解密，修改密文或 IV 会导致校验失败。
- 普通 Mod 更新保留设备密钥。换设备、清除应用数据、卸载应用或密钥失效后需要重新登录；错误或无法解密的文件可以从登录设置中清除。退出 Steam 或清除会话会删除文件及本 Mod 的 Keystore 密钥。
- 本 Mod 1.0.0 已保存的有效明文文件，在读取时原位改写为密文后才用于自动登录；不生成明文临时文件或备份。无法加密时删除该明文文件并提示重新登录，删除失败会明确提示手动处理。此处理只针对当前 Mod 自己的数据文件，不读取其它 Mod 的目录。
- Keystore 需要 Android API 23+。不可用时可以进行本次内存登录，界面会说明会话未能安全保存，**不会降级为明文存储**。

此保护针对会话文件被复制后的离线读取；已获得游戏进程控制权的代码仍可访问该进程的登录会话。

工坊搜索和下载使用已登录的 Steam 会话；下载权限由 Steam 决定。仅支持 ADOFAI 的可下载工坊项目，合集或其它游戏链接会显示错误。队列和任务历史保存在当前游戏进程中，退出登录会取消未完成任务；游戏重启后可重新加入未完成的主动下载。

## 安装与在线更新

从 [GitHub Releases](https://github.com/iidamie/ADOFAI_SteamWorkshop_Manager/releases) 下载 `SteamWorkshopManager-<版本>.zip`，解压到 ModManager 的 `mods` 目录：

```text
mods/
└── SteamWorkshopManager/
    ├── SteamWorkshopManager.dll
    ├── SteamWorkshopManager.deps.json
    ├── SteamKit2.dll
    ├── SteamKitSDK.dll
    └── 其它随包附带的运行依赖
```

这是独立发布的新 Mod，首版为 `1.0.0`。只读取自身目录下的 `SteamWorkshopManager.settings.json`、`data/steam-session.json` 以及谱面目录内的 `.steam-workshop-manager.json`。不读取或迁移其它 Mod 的配置、登录会话、谱面标记，也不重命名或合并已有谱面目录。

在线更新来源固定为 [iidamie/ADOFAI_SteamWorkshop_Manager](https://github.com/iidamie/ADOFAI_SteamWorkshop_Manager)。只检查有匹配安装包的正式 Release，跳过草稿和预发布版本。更新包包含依赖及文件校验清单；下载后在暂存目录核对文件和程序集版本，再替换本 Mod 的运行文件。常规替换失败会恢复备份；数据目录、配置、登录会话和 Levels 不属于更新范围。更新后需重启游戏，不会热重载正在运行的 Steam 连接。

## 构建与发布

需要 .NET 10 SDK。项目引用 `StArray.ModManager.dll` 和 `ImGui.NET.dll`：本地默认从同级 `../StArray.ModManager-csharp/artifacts/` 读取；其它环境可放到 `References/`，或使用 `-p:StArrayArtifacts=/path/to/artifacts` 指定目录。

```bash
dotnet build SteamWorkshopManager.csproj -c Release
python3 package_mod.py
```

打包脚本会先构建，再根据 `.deps.json` 收集运行依赖，生成：

- `dist/SteamWorkshopManager-<版本>.zip`：正式发布包，内部目录为 `SteamWorkshopManager/`。
- `dist/SteamWorkshopManager-<版本>.zip.sha256`：包的 SHA-256。
- `dist/SteamWorkshopManager.zip`：相同内容的固定名称副本，方便本地安装。

GitHub Actions 在 `main` 上的源代码改动后构建并发布，或通过 `workflow_dispatch` 手动触发。构建使用工作流中固定的 StArray.ModManager 源码提交生成编译引用。每次发版需同步修改 csproj 的 `Version` 和插件的 `Version`，工作流会创建 `v<版本>` 标签和对应安装包；已存在的标签不会被指向另一个提交，应先提升版本。`StArray.ModManager.dll` 和 `ImGui.NET.dll` 由宿主提供，不打入安装包。

## 同步和文件安全

- 只有完整读取所有订阅页后才会清理已取消订阅项目；查询失败或结果不完整时不删除本地内容。
- 主动下载的所有权标记包含 `KeepWithoutSubscription=true`。
- “仅下载已勾选的订阅”只影响下载，不会从订阅清单中取消项目。
- 手动删除仍订阅的谱面后，若使用全量下载模式，它会在后续同步中重新下载；可开启筛选并取消勾选来避免自动下载。
- 每个安装目录含 `.steam-workshop-manager.json` 所有权标记。清理时只删除标记中的直接子文件，不删除用户额外放入的文件。
- Steam CDN 文件路径会检查路径穿越和符号链接；下载目录会展平，文件与 `.adofai` 并列。下载结果只有 ZIP 压缩包时，会先解压再安装全部谱面和资源。
- 使用 Steam 返回的有效 `file_url`，缺少 URL 时通过工坊 manifest 下载；不调用 Cloud 文件详情。不含 `.adofai` 文件的项目会跳过。
- 单个工坊项目下载上限为 512 MiB，展平内容上限为 1 GiB，最多 4096 个文件。

Android 对共享外部存储的访问仍由宿主游戏和系统授权控制。如果 ModManager 进程没有权限写入 `Levels` 目录，需先在设备上授予 ADOFAI 所需的文件访问权限。

## 依赖

- [SteamKit](https://github.com/SteamRE/SteamKit)，NuGet `SteamKit2` 3.4.0，LGPL-2.1-only。
- [SteamKitSDK](https://github.com/pat2381/SteamKitSDK) 0.9.3，Apache-2.0，用于 Steam 工坊 manifest 和 CDN 下载。
- [StbImageSharp](https://github.com/StbSharp/StbImageSharp) 2.30.16，用于 JPEG、PNG 等图片解码，采用 Unlicense 许可。
- `StArray.ModManager` 与 `ImGui.NET` 运行时程序集，由目标设备上的 ModManager 提供。
- 第三方库声明随安装 ZIP 一起提供，见 `THIRD-PARTY-NOTICES.md` 和 `licenses/`。
