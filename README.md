# Codex Phone Reminder

一个让 Android 手机远程跟踪、提醒和操控电脑上 Codex 任务的个人工具。Windows 本地代理读取 Codex CLI/GUI 共享的结构化会话记录,Android 客户端查看任务、发送下一条指令,并由前台服务在关键事件发生时及时提醒。

> **最新版本 v0.8.0** · [下载 Android APK](https://github.com/MistFloat/CodexPhoneReminder/releases/download/v0.8.0/CodexPhoneReminder-v0.8.0-android.apk) · [下载 Windows Agent](https://github.com/MistFloat/CodexPhoneReminder/releases/download/v0.8.0/CodexPhoneReminder-agent-v0.8.0-win-x64.zip) · [Release Notes](https://github.com/MistFloat/CodexPhoneReminder/releases/tag/v0.8.0)

## 项目背景:从局域网到云中继

本项目最初按 [PRD](./PRD.md) 设计为**纯局域网**互传 + 消息提醒:电脑跑一个本地 Agent,手机通过同一校园 Wi-Fi 直连,不依赖任何公网服务、内网穿透、域名或 VPS。MVP 阶段只考虑局域网直连,目标用户是同时使用 Codex 处理多个项目、无法持续盯电脑的个人开发者。

后来学校对校园局域网做了 **ACL 隔离**:同一 SSID 下电脑与手机已无法直接建立 TCP 连接,纯局域网方案在校园网内整体失效。为此项目增加了**自建云服务器中继**模式(`src/CodexPhoneReminder.Relay`),作为局域网不可达时的回退通道;`android/app/src/main/java/com/codexphonereminder/RelayClient.java` 与 `src/CodexPhoneReminder.Agent/CloudRelayClient.cs` 完成了端到端打通。

当前推荐部署形态是"**本地直连优先,云中继自动回退**":

- 同一局域网且 ACL 未隔离时,仍优先走本地 HTTPS 直连,延迟最低;
- 局域网被隔离、跨校园网、跨移动网络或 DHCP 地址变化时,自动回退到自建 VPS 中继;
- 中继只转发 AES-256-GCM 端到端密文,不持有配对密钥,不会把 Agent 的 `5187`/`5188` 端口暴露到公网。

> 这套云中继是**为校园网 ACL 隔离做的兜底**,不是必须项。如果你的网络环境允许局域网直连,完全可以不部署中继,只跑 Agent + Android。

## 快速开始

```powershell
dotnet run --project src/CodexPhoneReminder.Agent
```

1. 电脑浏览器打开 `http://localhost:5187/api/pair`,记下 6 位配对码与电脑指纹。
2. 手机和电脑连接同一局域网,在手机打开响应中的 `address`。
3. 输入配对码;浏览器菜单选择"添加到主屏幕"。

如果你的局域网已被 ACL 隔离,完成上面的本地配对后还需要部署云中继,详见下文"自建云中继"一节。

## Android APK

原生 Android 客户端位于 `android`,使用 Java、API 36、Android Gradle Plugin 9.3.0、Gradle Wrapper 9.5.0 和 JDK 17。当前版本 0.8.0(versionCode 11)。它支持:

- 输入代理地址与 6 位配对码;设备令牌、电脑指纹和地址由 Android Keystore 加密保存。
- 查看任务列表、CLI 可用状态、工作区映射数,以及最近 200 条项目对话。
- 从手机向同一 Codex thread 发送下一条指令。
- 项目对话详情正确解析服务端的任务包装结构,并在页面打开时每 2 秒刷新消息;刷新不清空正在编辑的输入。
- 手机发送的临时消息使用 `pending:` 标识;会话扫描器读到权威 JSONL 消息后原位替换临时气泡,避免同一用户消息显示两次。
- Android 0.4.2 在收到 401 时立即清除失效令牌并返回六位码配对页,任务页也提供"重新配对"入口。
- 服务端将设备令牌摘要和撤销状态保存到 `data/paired-devices.json`;完成一次新版配对后,代理正常重启不再撤销授权。
- Android 0.5.0 仅在用户打开某个项目对话时请求结构化进度,并每 2 秒续订 8 秒服务端租约;退出详情或进入后台后租约自动过期。
- 进度接口使用 `after` 游标增量返回当前回合的工具开始/完成、文件修改、测试构建及回合完成/失败事件,不返回命令参数、工具输出或逐 token 推理。
- Android 0.5.1 的任务列表按项目文件夹分组;每个对话可经确认后归档,归档只从列表隐藏,不删除 Codex 会话历史。
- Android 0.5.2 修复项目对话列表无法滚动;对话消息轮询不再受实时进度接口失败影响,并通过禁用缓存确保回复无需重启 App 即可更新。
- Android 0.6.0 完成后台通知闭环:审批、等待回复、完成和失败按事件去重提醒,连续断线两次后提醒并在恢复时通知;点击任务通知直达对应对话,设备重启后自动恢复监听。
- Android 0.6.1 保留两秒消息同步,但仅在消息内容变化时重绘;用户正在选择对话文字时暂停消息区重绘,复制结束后自动补上最新内容。
- Android 0.7.1 配对地址直接从 Windows 已连接网卡读取:优先 WLAN、其次物理有线网卡,并排除 VMware、Hyper-V、WSL、Docker 等虚拟适配器。Android 对已配对的局域网 HTTPS 地址强制直连,不使用手机或系统配置的 HTTP 代理。
- Android 0.8.0 支持"本地直连优先、云中继自动回退":仅在本地网络连接失败时才将 AES-256-GCM 加密信封交给自建 VPS;证书固定失败、401 或普通 API 错误绝不会悄悄改走云端。
- 以前台服务每 10 秒同步;等待批准、等待回复、完成、失败和离线状态变化触发去重通知。
- 点击任务通知进入对应对话;首次同步只建立状态基线,不重复通知历史事件。

本机构建:

```powershell
cd android
.\gradlew.bat assembleDebug
```

可安装产物位于 `android/app/build/outputs/apk/debug/app-debug.apk`。这是 Android debug key 签名的测试包;正式分发前需建立独立 release keystore,并使用 `assembleRelease` 生成发布包。

代理运行后,手机还可以直接从现有网页地址下载 APK:

```text
http://<电脑局域网地址>:5187/downloads/android-debug.apk
```

## 自建云中继

云中继用于解决校园网 ACL 隔离、移动网络或异地网络中电脑与 Android 设备不能直接建立 TCP 连接的问题。它**不需要**为电脑或手机配置固定的校园网 IP,也不需要把 Windows Agent 的 `5187`、`5188` 端口暴露到公网。电脑 Agent 和 Android 客户端都主动连接到一个固定的 HTTPS 域名;即使 DHCP 地址、SSID 或所在地变化,也能重新连接。

**完整部署步骤见 [DEPLOYMENT.md](./DEPLOYMENT.md)**,涵盖:VPS 与域名准备、`.env` 与 bootstrap key 生成、`docker-compose.yml` 与 `Caddyfile` 配置、防火墙规则、启动验证、Windows Agent 的 `Relay__Url` / `Relay__BootstrapKey` 环境变量配置、Android 配对与回退验证、日常运维与故障排查。

部署并让 Agent 显示中继已连接后,使用新版 APK 重新完成一次本地 HTTPS 配对,手机会自动、安全地保存云中继凭据;之后优先直连本机,不通时自动回退。

按需进度接口:

```text
GET /api/tasks/{thread-id}/progress?after=<last-event-id>
```

调用会为该对话续订短租约。任务列表、后台通知服务和未打开的对话不会调用此接口。

代理默认扫描 `%USERPROFILE%\.codex\sessions`。GUI 创建的用户对话会按 thread/session ID 自动出现;内部子代理会话被过滤。状态持久化在 `src/CodexPhoneReminder.Agent/data`,该目录不应提交。

## Codex CLI 接入

代理通过 `CodexCli:Executable` 和 `CodexCli:SessionsPath` 配置 CLI 与会话目录。Windows 下默认使用 `codex.exe`,并从 `PATH` 解析为绝对路径;`/api/health` 的 `cliAvailable`、`cliPath` 与 `mappedWorkspaces` 可确认发送能力。代理从 GUI 会话元数据维护仅在电脑内存中的 thread→绝对工作区映射,手机不会获得绝对路径。手机回复使用 `codex exec --cd <workspace> --sandbox workspace-write --json resume <thread-id> <reply>` 恢复同一对话,参数直接传给进程而不经过 shell。若自动定位失败,可把 `Executable` 设置为 `where.exe codex` 输出的完整 `.exe` 路径。事件注入端点仍保留给 CLI 包装器与契约测试:

```powershell
$headers = @{ 'X-Device-Token' = '<配对后得到的令牌>' }
$body = @{ taskId='task-1'; project='课程项目'; title='实现功能'; type='Completed'; summary='当前对话已完成' } | ConvertTo-Json
Invoke-RestMethod http://localhost:5187/api/demo/events -Method Post -Headers $headers -ContentType application/json -Body $body
```

支持的事件类型:`Started`、`Progress`、`ApprovalRequested`、`WaitingReply`、`Completed`、`Failed`、`Resumed`、`Offline`。调用方传入稳定的 `id` 即可获得幂等去重。

## 安全边界

- 配对码随机生成、5 分钟过期、使用一次后立即失效。
- 设备令牌只保存 SHA-256 摘要;审批带请求 ID、有效期和一次性 nonce。
- 高风险批准必须二次确认;重复提交、过期请求和错误 nonce 均被拒绝。
- 事件写入前会脱敏常见密钥与 Windows 绝对路径。
- Android 0.7.0 起,配对页仍可通过 HTTP 打开,但设备领取令牌、任务、消息、模型和审批 API 全部使用 HTTPS;Android 固定电脑长期 TLS 证书的 SHA-256 指纹,拒绝明文 API 和证书被替换的连接。升级后需重新配对一次。
- Windows 电脑开启网络代理不会影响代理的局域网监听;原生 Android 客户端会绕过代理直接连接配对得到的私有 IP。浏览器/PWA 受浏览器自己的代理设置约束,桌面端请始终通过 `http://localhost:5187` 打开配对页;若手机浏览器另行设置了全局代理,需将电脑的局域网 IP 加入该浏览器的代理绕过列表。
- 云中继只开放 VPS 的 `80/443`,Agent 与 Android 的业务负载使用配对密钥 AES-256-GCM 加密;中继仅保存令牌摘要、路由元数据和短期密文队列。手机的中继配置由 Android Keystore 加密保存,Agent 在本机保存用于断线重传的密文回执,不会把局域网设备令牌交给 VPS。
- 对话详情可选择本机 Codex 模型目录中的模型;选择经过服务端白名单校验,并作为独立的 `codex exec resume --model` 参数传递。默认项继续采用电脑的 Codex 配置。
- 手机详情页显示有效审批的操作、原因、风险和过期时间,可批准或拒绝;高风险批准必须二次确认,服务端仍校验一次性 nonce 和有效期。

## 验证

```powershell
dotnet build CodexPhoneReminder.sln
dotnet run --project tests/CodexPhoneReminder.Agent.Tests
```

## 文档索引

- [PRD.md](./PRD.md) — 产品需求文档,原始局域网 MVP 定位与目标用户。
- [ITERATION_PRD_v0.4.md](./ITERATION_PRD_v0.4.md) — Android 客户端与结构化进度的迭代规划。
- [DEPLOYMENT.md](./DEPLOYMENT.md) — **自建云中继部署指南**,校园网 ACL 隔离后的回退方案。

## 已知限制

- 解析器已读取本机 Codex Desktop 生成的结构化会话 JSONL;CLI 版本升级仍可能改变事件字段,在积累跨版本契约样本前不能宣称达到 95% 识别率。
- PWA 在 Android 后台的局域网通知能力受系统限制;需要后台提醒时应使用原生 APK。部分手机还需在系统设置中允许通知、前台服务和后台运行。
- 当前交付的是 debug APK,不是用于应用商店分发的 release APK/AAB。
- Reminder 审批会恢复同一 Codex CLI 对话,但不会绕过 CLI 自身审批和沙箱;新产生的系统级权限请求必须重新确认。
- 云中继需要你自己的 VPS、域名和可信 TLS 证书;首次领取云凭据仍必须在手机能够访问 Agent 的局域网环境完成一次重新配对。
